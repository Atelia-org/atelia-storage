using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Storage;
using Xunit;

namespace Atelia.HeadStore.Tests.Storage;

public sealed class DirectoryHeadStoreFilesTests {
    [Theory]
    [InlineData(0u)]
    [InlineData(256u)]
    [InlineData(uint.MaxValue - 1)]
    public void CreateHasOnlyGateLockAndEmptyNecessaryLayout(uint reservedUpperBound) {
        using var fixture = new StorageFixture();
        byte[] id = fixture.Create(reservedUpperBound);
        Assert.Equal(new[] { "creating", "refs", "versionstore.format", "versionstore.lock" },
            Directory.EnumerateFileSystemEntries(fixture.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(0, new FileInfo(fixture.LockPath).Length);
        Assert.True(FormatGateCodec.TryRead(File.ReadAllBytes(fixture.GatePath), out FormatGate gate));
        Assert.True(StoreIdentity.TryRead(id, out StoreIdentity identity));
        Assert.Equal(identity, gate.HeadStoreId);
        Assert.True(StoreIdentity.TryRead(fixture.Data.StoreId, out StoreIdentity dataIdentity));
        Assert.Equal(dataIdentity, gate.DataStoreId);
        Assert.Equal(reservedUpperBound, gate.ReservedHeadIdUpperBound);
        using var reopened = HeadStore.Open(fixture.Root, fixture.Data);
        Assert.Equal(id, reopened.HeadStoreId.ToArray());
        Assert.Empty(reopened.ListHeads().Unwrap());
    }

    [Fact]
    public void BorrowedReadOnlyAndDisposedGuardsPrecedeRootBootstrapAndAdmission() {
        using var fixture = new StorageFixture();
        fixture.MakeDataReadOnly();
        Assert.Throws<ArgumentException>(() => HeadStore.Create(fixture.Root, fixture.Data));
        Assert.Throws<ArgumentException>(() => HeadStore.Open(fixture.Root, fixture.Data));
        Assert.False(Directory.Exists(fixture.Root));
        fixture.Data.Dispose();
        Assert.Throws<ObjectDisposedException>(() => HeadStore.Create(fixture.Root, fixture.Data));
        Assert.Throws<ObjectDisposedException>(() => HeadStore.OpenReadOnly(fixture.Root, fixture.Data));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void InvalidReservedBoundAndMissingParentDoNotCreateAncestors() {
        using var fixture = new StorageFixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadStore.Create(fixture.Root, fixture.Data, uint.MaxValue));
        Assert.False(Directory.Exists(fixture.Root));
        string parent = Path.Combine(fixture.BasePath, "missing-parent");
        Assert.ThrowsAny<IOException>(() => HeadStore.Create(Path.Combine(parent, "version"), fixture.Data));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void CreateAcceptsOnlyEmptyRootOrPermanentEmptyLockBootstrap() {
        using var fixture = new StorageFixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllBytes(fixture.LockPath, []);
        using var store = HeadStore.Create(fixture.Root, fixture.Data);
        Assert.Empty(store.ListHeads().Unwrap());
        fixture.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("refs", true)]
    [InlineData("creating", true)]
    [InlineData("versionstore.format", false)]
    [InlineData("unknown", false)]
    public void CreateRejectsPriorLayoutOrUnknownInputWithoutRollback(string name, bool directory) {
        using var fixture = new StorageFixture();
        Directory.CreateDirectory(fixture.Root);
        string path = Path.Combine(fixture.Root, name);
        if (directory) { Directory.CreateDirectory(path); }
        else { File.WriteAllBytes(path, [3, 4]); }
        Assert.Throws<InvalidDataException>(() => HeadStore.Create(fixture.Root, fixture.Data));
        Assert.Equal(new[] { name }, Directory.EnumerateFileSystemEntries(fixture.Root).Select(Path.GetFileName));
        if (!directory) { Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(path)); }
    }

    [Fact]
    public void ActiveDataBuilderDoesNotBlockCreateAndRemainsUsable() {
        using var fixture = new StorageFixture();
        using var builder = fixture.Data.BeginAppend();
        builder.PayloadAndMeta.Write<byte>([1, 2]);
        using (var store = HeadStore.Create(fixture.Root, fixture.Data)) { Assert.Empty(store.ListHeads().Unwrap()); }
        builder.PayloadAndMeta.Write<byte>([3]);
        Assert.True(builder.EndAppend(7).IsSuccess);
        using var readOnly = HeadStore.OpenReadOnly(fixture.Root, fixture.Data);
        Assert.Empty(readOnly.ListHeads().Unwrap());
    }

    [Fact]
    public void BothModesRejectAllBadGateLengthsAndResealedInvalidFieldsBeforeDeletingPending() {
        using var fixture = new StorageFixture();
        fixture.Create();
        byte[] valid = File.ReadAllBytes(fixture.GatePath);
        File.WriteAllBytes(fixture.PendingPath, [9, 8, 7]);
        var invalid = new List<byte[]>();
        for (int length = 0; length < valid.Length; length++) { invalid.Add(valid[..length]); }
        invalid.Add([.. valid, 0]);
        byte[] corrupt = valid.ToArray(); corrupt[40] ^= 1; invalid.Add(corrupt);
        foreach (int offset in new[] { 0, 4, 20, 36 }) {
            byte[] bad = valid.ToArray();
            if (offset == 0) { BinaryPrimitives.WriteUInt32LittleEndian(bad, 2); }
            else if (offset == 36) { BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(offset), uint.MaxValue); }
            else { bad.AsSpan(offset, 16).Clear(); }
            RollingCrc.SealCodewordForward(bad);
            invalid.Add(bad);
        }
        foreach (byte[] bytes in invalid) {
            File.WriteAllBytes(fixture.GatePath, bytes);
            Assert.Throws<InvalidDataException>(() => HeadStore.Open(fixture.Root, fixture.Data));
            Assert.Throws<InvalidDataException>(() => HeadStore.OpenReadOnly(fixture.Root, fixture.Data));
            Assert.Equal(bytes, File.ReadAllBytes(fixture.GatePath));
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(fixture.PendingPath));
            fixture.Data.EnsureUsable();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4097)]
    public void PendingAnyLengthIsPreservedByReadOnlyAndCancelledByWriterWithoutContentReading(int length) {
        using var fixture = new StorageFixture();
        fixture.Create();
        byte[] bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray();
        File.WriteAllBytes(fixture.PendingPath, bytes);
        using (var readOnly = HeadStore.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Empty(readOnly.ListHeads().Unwrap());
            Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath));
        }
        using var writer = HeadStore.Open(fixture.Root, fixture.Data);
        Assert.False(File.Exists(fixture.PendingPath));
        Assert.Empty(writer.ListHeads().Unwrap());
    }

    [Theory]
    [InlineData("Pending.rbf", false)]
    [InlineData("unknown", false)]
    [InlineData("pending.rbf", true)]
    public void WrongWorkspaceNameOrTypeIsPreservedAndRejectedByBothModes(string name, bool directory) {
        using var fixture = new StorageFixture();
        fixture.Create();
        string path = Path.Combine(fixture.CreatingPath, name);
        if (directory) { Directory.CreateDirectory(path); }
        else { File.WriteAllBytes(path, [4]); }
        Assert.Throws<InvalidDataException>(() => HeadStore.Open(fixture.Root, fixture.Data));
        Assert.Throws<InvalidDataException>(() => HeadStore.OpenReadOnly(fixture.Root, fixture.Data));
        Assert.Equal(new[] { name }, Directory.EnumerateFileSystemEntries(fixture.CreatingPath).Select(Path.GetFileName));
    }

    [Fact]
    public void ReadOnlyDoesNotScanFormalSetButListAndWriterDiscoverMalformedEntries() {
        using var fixture = new StorageFixture();
        fixture.Create();
        string bad = Path.Combine(fixture.HeadsPath, "hidden-unknown");
        File.WriteAllBytes(bad, [7]);
        if (OperatingSystem.IsWindows()) { File.SetAttributes(bad, FileAttributes.Hidden); }
        File.WriteAllBytes(fixture.PendingPath, [3]);
        using (var readOnly = HeadStore.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => readOnly.ListHeads());
            Assert.Equal("HeadStore.HeadNotFound", readOnly.ReadHead(1).Error!.ErrorCode);
            Assert.True(File.Exists(fixture.PendingPath));
        }
        Assert.Throws<InvalidDataException>(() => HeadStore.Open(fixture.Root, fixture.Data));
        Assert.True(File.Exists(fixture.PendingPath));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(bad));
    }

    [Fact]
    public void ListIsPathOnlyOwnedAndIncludesBothNumberDomainsAndDamagedFiles() {
        using var fixture = new StorageFixture();
        fixture.Create(256);
        foreach (uint id in new[] { 1u, 256u, 257u, 0x89abcdefu, uint.MaxValue }) {
            File.WriteAllBytes(fixture.HeadPath(id), [0]);
        }
        IReadOnlyList<HeadId> listed;
        using (var owner = HeadStore.OpenReadOnly(fixture.Root, fixture.Data)) {
            listed = owner.ListHeads().Unwrap();
            Assert.Equal(new[] { 1u, 256u, 257u, 0x89abcdefu, uint.MaxValue }, listed.Select(x => x.LocalId).Order());
            Assert.Throws<InvalidDataException>(() => owner.ReadHead(listed[0]));
        }
        Assert.Equal(5, listed.Count);
        if (listed is IList<HeadId> mutableProjection) {
            Assert.True(mutableProjection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutableProjection.Add(default));
        }
        if (listed is System.Collections.ICollection collection) {
            Assert.False(collection.SyncRoot is HeadId[]);
        }
        using var reopened = HeadStore.Open(fixture.Root, fixture.Data);
        Assert.Equal("HeadStore.HeadIdExhausted", reopened.CreateHead([1], out _).Error!.ErrorCode);
        Assert.Equal(5, reopened.ListHeads().Unwrap().Count);
    }

    [Fact]
    public void UnknownRootContentAndOptionalTagsContentsAreOutsideOpenAudit() {
        using var fixture = new StorageFixture();
        fixture.Create();
        File.WriteAllBytes(Path.Combine(fixture.Root, "external-note"), [3]);
        string tags = Path.Combine(fixture.Root, HeadStorePaths.TagsDirectoryName);
        Directory.CreateDirectory(tags);
        File.WriteAllBytes(Path.Combine(tags, "unimplemented-bucket"), [4]);
        using var owner = HeadStore.Open(fixture.Root, fixture.Data);
        Assert.Empty(owner.ListHeads().Unwrap());
    }

    [Fact]
    public void BackendEnumeratorCleanupFaultMarksOwnerAndPreservesPrimaryError() {
        using var fixture = new StorageFixture();
        fixture.Create();
        File.WriteAllBytes(fixture.HeadPath(1), [0]);
        var primary = new IOException("controlled list enumeration error");
        var cleanup = new IOException("controlled list cleanup error");
        var operations = new FailingListOperations(primary, cleanup);
        var files = new DirectoryHeadStoreFiles(fixture.Root, operations);
        int faults = 0;
        AggregateException error = Assert.Throws<AggregateException>(() => files.ListHeadIds(() => faults++));
        Assert.Equal(new[] { primary, cleanup }, error.InnerExceptions);
        Assert.Equal(1, faults);
        Assert.Equal(1, operations.Enumerator!.DisposeCount);
    }

    private sealed class FailingListOperations(Exception moveError, Exception closeError) : HeadStoreFactoryOperations {
        internal FailingEnumerator? Enumerator { get; private set; }
        internal override IEnumerator<string> EnumerateDirectory(string directory) =>
            Enumerator = new FailingEnumerator(base.EnumerateDirectory(directory), moveError, closeError);
    }

    private sealed class FailingEnumerator(IEnumerator<string> inner, Exception moveError, Exception closeError) : IEnumerator<string> {
        private int _moves;
        internal int DisposeCount { get; private set; }
        public string Current => inner.Current;
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() {
            if (++_moves == 2) { throw moveError; }
            return inner.MoveNext();
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() {
            DisposeCount++;
            inner.Dispose();
            throw closeError;
        }
    }
}
