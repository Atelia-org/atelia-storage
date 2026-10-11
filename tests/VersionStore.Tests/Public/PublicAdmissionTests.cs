using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Public;

public sealed class PublicAdmissionTests {
    public static IEnumerable<object[]> ShortGateLengths() => Enumerable.Range(0, 44).Select(length => new object[] { length });

    [Theory]
    [MemberData(nameof(ShortGateLengths))]
    public void EveryShortGateRejectsBeforePendingCancellationOrFormalRefRecovery(int length) {
        using var fixture = new PublicVersionStoreFixture();
        MakeRecoverableRefAndPending(fixture);
        File.WriteAllBytes(fixture.GatePath, File.ReadAllBytes(fixture.GatePath)[..length]);
        RejectBothUnchanged<InvalidDataException>(fixture);
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("crc")]
    [InlineData("version")]
    [InlineData("zero-vs-id")]
    [InlineData("zero-data-id")]
    [InlineData("invalid-reservation")]
    [InlineData("missing")]
    public void GateMustBeExactSupportedCheckedRecordAndIsNeverRepaired(string kind) {
        using var fixture = new PublicVersionStoreFixture();
        MakeRecoverableRefAndPending(fixture);
        byte[] gate = File.ReadAllBytes(fixture.GatePath);
        switch (kind) {
            case "trailing": gate = [.. gate, 0]; break;
            case "crc": gate[^1] ^= 1; break;
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(gate, 2); RollingCrc.SealCodewordForward(gate); break;
            case "zero-vs-id": gate.AsSpan(4, 16).Clear(); RollingCrc.SealCodewordForward(gate); break;
            case "zero-data-id": gate.AsSpan(20, 16).Clear(); RollingCrc.SealCodewordForward(gate); break;
            case "invalid-reservation": BinaryPrimitives.WriteUInt32LittleEndian(gate.AsSpan(36), uint.MaxValue); RollingCrc.SealCodewordForward(gate); break;
        }
        if (kind == "missing") { File.Delete(fixture.GatePath); }
        else { File.WriteAllBytes(fixture.GatePath, gate); }
        if (kind == "missing") { RejectBothUnchanged<FileNotFoundException>(fixture); }
        else { RejectBothUnchanged<InvalidDataException>(fixture); }
    }

    [Fact]
    public void WrongBorrowedDataRejectsBeforePendingCancellationAndRefRecovery() {
        using var fixture = new PublicVersionStoreFixture();
        MakeRecoverableRefAndPending(fixture);
        using var other = DataStore.Create(Path.Combine(fixture.Sandbox, "other-data"));
        var before = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, other); });
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, other); });
        fixture.AssertUnchanged(before);
        other.EnsureUsable();
        fixture.Data.EnsureUsable();
        using var valid = Store.Open(fixture.Root, fixture.Data);
        Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData("refs")]
    [InlineData("creating")]
    public void ValidGateDoesNotAuthorizeRepairOfMissingRequiredLayout(string name) {
        using var fixture = new PublicVersionStoreFixture();
        using (var store = Store.Create(fixture.Root, fixture.Data)) { }
        Directory.Delete(Path.Combine(fixture.Root, name));
        RejectBothUnchanged<DirectoryNotFoundException>(fixture);
    }

    [Theory]
    [InlineData("layout-without-gate")]
    [InlineData("short-gate")]
    [InlineData("unknown")]
    public void CreateIsFreshOnlyAndDoesNotResumePartialInitialization(string kind) {
        using var fixture = new PublicVersionStoreFixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllBytes(fixture.LockPath, []);
        if (kind == "layout-without-gate") { Directory.CreateDirectory(Path.Combine(fixture.Root, "refs")); }
        if (kind == "short-gate") { File.WriteAllBytes(fixture.GatePath, [1]); }
        if (kind == "unknown") { File.WriteAllBytes(Path.Combine(fixture.Root, ".unknown"), [5]); }
        var before = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Create(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void BootstrapWithOnlyEmptyLockCanCreateButMissingParentCannotBeCreatedRecursively() {
        using var fixture = new PublicVersionStoreFixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllBytes(fixture.LockPath, []);
        using (var store = Store.Create(fixture.Root, fixture.Data)) { Assert.Empty(store.ListRefs().Unwrap()); }
        string missingParent = Path.Combine(fixture.Sandbox, "missing-parent");
        Assert.Throws<DirectoryNotFoundException>(() => { using var unexpected = Store.Create(Path.Combine(missingParent, "store"), fixture.Data); });
        Assert.False(Directory.Exists(missingParent));
    }

    [Fact]
    public void BorrowModeDisposedGuardAndInvalidReservationRejectBeforeAnyVersionStoreIo() {
        using var fixture = new PublicVersionStoreFixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => { using var unexpected = Store.Create(fixture.Root, fixture.Data, uint.MaxValue); });
        Assert.False(Directory.Exists(fixture.Root));
        fixture.Data.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { using var unexpected = Store.Create(fixture.Root, fixture.Data); });
        Assert.Throws<ObjectDisposedException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        Assert.Throws<ObjectDisposedException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        Assert.False(Directory.Exists(fixture.Root));
        using var readOnlyData = DataStore.OpenReadOnly(fixture.DataRoot);
        Assert.Throws<ArgumentException>(() => { using var unexpected = Store.Create(fixture.Root, readOnlyData); });
        Assert.Throws<ArgumentException>(() => { using var unexpected = Store.Open(fixture.Root, readOnlyData); });
        Assert.False(Directory.Exists(fixture.Root));
        readOnlyData.EnsureUsable();
    }

    private static void MakeRecoverableRefAndPending(PublicVersionStoreFixture fixture) {
        fixture.SeedClosed();
        byte[] image = File.ReadAllBytes(fixture.RefPath(7));
        File.WriteAllBytes(fixture.RefPath(7), image[..^1]);
        File.WriteAllBytes(fixture.PendingPath, [0xff, 0x00, 0xfe]);
    }

    private static void RejectBothUnchanged<TException>(PublicVersionStoreFixture fixture) where TException : Exception {
        var before = fixture.Snapshot();
        Assert.Throws<TException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
        Assert.Throws<TException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
        fixture.Data.EnsureUsable();
    }
}
