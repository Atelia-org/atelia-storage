using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

/// <summary>实际字节残留与目录拒绝向量；不声称这些镜像由外部 kill 产生。</summary>
public sealed class PublicAdmissionTests {
    public static IEnumerable<object[]> GatePrefixes() => Enumerable.Range(0, 24).Select(i => new object[] { i });
    public static IEnumerable<object[]> IncompleteHeaderPrefixes() =>
        Enumerable.Range(0, checked((int)PublicStoreFixture.InitializationBoundary)).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(GatePrefixes))]
    public void EveryIncompleteGatePrefixRepeatedlyRejectsWithoutRepairOrIdentityAdoption(int length) {
        using var fixture = new PublicStoreFixture();
        MakeRecoverableActive(fixture);
        byte[] gate = File.ReadAllBytes(fixture.GatePath);
        File.WriteAllBytes(fixture.GatePath, gate[..length]);
        RejectBothUnchanged(fixture);
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("crc")]
    [InlineData("version")]
    [InlineData("zero-id")]
    public void FullLengthGateErrorsDoNotRecoverActiveOrClaimDataIdentity(string kind) {
        using var fixture = new PublicStoreFixture();
        MakeRecoverableActive(fixture);
        byte[] gate = File.ReadAllBytes(fixture.GatePath);
        switch (kind) {
            case "trailing": gate = [.. gate, 0]; break;
            case "crc": gate[^1] ^= 1; break;
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(gate, 2); RollingCrc.SealCodewordForward(gate); break;
            case "zero-id": gate.AsSpan(4, 16).Clear(); RollingCrc.SealCodewordForward(gate); break;
        }
        File.WriteAllBytes(fixture.GatePath, gate);
        RejectBothUnchanged(fixture);
    }

    [Theory]
    [MemberData(nameof(IncompleteHeaderPrefixes))]
    public void EveryShortFormalHeaderRejectsBeforeRbfCanCompleteIt(int length) {
        using var fixture = new PublicStoreFixture();
        byte[] identity = fixture.CreateClosed();
        byte[] image = fixture.HeaderImage(identity, 1);
        Assert.Equal(PublicStoreFixture.InitializationBoundary, image.Length);
        File.WriteAllBytes(fixture.Active(1), image[..length]);
        RejectBothUnchanged(fixture);
    }

    [Theory]
    [InlineData("other-id")]
    [InlineData("other-file")]
    [InlineData("version")]
    [InlineData("tag")]
    [InlineData("meta")]
    public void CompleteHeaderMustMatchAdmittedIdentityPathAndShape(string kind) {
        using var fixture = new PublicStoreFixture();
        byte[] identity = fixture.CreateClosed();
        if (kind == "other-id") { identity[0] ^= 1; }
        byte[] image = fixture.HeaderImage(identity, kind == "other-file" ? 2u : 1u,
            version: kind == "version" ? 2u : 1u, tag: kind == "tag" ? 1u : 0u,
            tailMeta: kind == "meta" ? new byte[] { 1 } : null);
        File.WriteAllBytes(fixture.Active(1), image);
        RejectBothUnchanged(fixture);
    }

    [Fact]
    public void CompleteHeaderPayloadCrcFailureCannotBeAdoptedAsAnInitializedFile() {
        using var fixture = new PublicStoreFixture();
        byte[] identity = fixture.CreateClosed();
        byte[] image = fixture.HeaderImage(identity, 1);
        // Independent RBF3 byte vector: this is inside the first encoded payload, before framing/closure.
        image[16] ^= 1;
        File.WriteAllBytes(fixture.Active(1), image);
        RejectBothUnchanged(fixture);
    }

    [Theory]
    [InlineData("active/backup")]
    [InlineData("active/00000000.rbf")]
    [InlineData("active/0000000A.rbf")]
    [InlineData("active/00000002.RBF")]
    [InlineData("archive/400000")]
    [InlineData("archive/000000/00000400.rbf")]
    [InlineData("archive/000001/00000002.rbf")]
    [InlineData("archive/000000/0000002.rbf")]
    [InlineData("creating/hidden.tmp")]
    [InlineData("creating/00000003.rbf")]
    public void EveryManagedItemIsCheckedBeforeActiveRecovery(string relative) {
        using var fixture = new PublicStoreFixture();
        MakeRecoverableActive(fixture);
        string path = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (relative == "archive/400000") { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, []); }
        RejectBothUnchanged(fixture);
    }

    [Theory]
    [InlineData("active/00000002.rbf")]
    [InlineData("creating/00000002.rbf")]
    [InlineData("archive/000000/00000002.rbf")]
    public void CanonicalFileNamesDoNotAuthorizeDirectories(string relative) {
        using var fixture = new PublicStoreFixture();
        MakeRecoverableActive(fixture);
        Directory.CreateDirectory(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        RejectBothUnchanged(fixture);
    }

    [Fact]
    public void DuplicateFormalFileIdIsRejectedBeforePrivateCancellationOrActiveRecovery() {
        using var fixture = new PublicStoreFixture();
        MakeRecoverableActive(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Archive(1))!);
        File.Copy(fixture.Active(1), fixture.Archive(1));
        File.WriteAllBytes(fixture.Private(2), []);
        RejectBothUnchanged(fixture);
    }

    [Theory]
    [InlineData("bad-bytes")]
    [InlineData("other-identity")]
    [InlineData("user-suffix")]
    [InlineData("multiple")]
    public void IncompatiblePrivateCreationIsRetainedAndBlocksActiveRecovery(string kind) {
        using var fixture = new PublicStoreFixture();
        byte[] identity = MakeRecoverableActive(fixture);
        byte[] image;
        if (kind == "bad-bytes") { image = [0xff]; }
        else {
            if (kind == "other-identity") { identity[0] ^= 1; }
            image = fixture.HeaderImage(identity, 2, userSuffix: kind == "user-suffix");
        }
        File.WriteAllBytes(fixture.Private(2), image);
        if (kind == "multiple") { File.WriteAllBytes(fixture.Private(3), []); }
        RejectBothUnchanged(fixture);
    }

    [Fact]
    public void EveryPrivateInitializationPrefixIsKeptByReadOnlyAndCancelledBeforeWritableRecovery() {
        // Prefix facts exercise the actual public RBF writer image, not a duplicated FrameStore encoder.
        for (int length = 0; length <= PublicStoreFixture.InitializationBoundary; length++) {
            using var fixture = new PublicStoreFixture();
            byte[] identity = fixture.CreateClosed();
            byte[] image = fixture.HeaderImage(identity, 1);
            File.WriteAllBytes(fixture.Private(1), image[..length]);
            var before = fixture.Snapshot();
            using (var ro = Store.OpenReadOnly(fixture.Root)) {
                Assert.Equal(identity, ro.StoreId.ToArray());
                Assert.Empty(ro.RecoveryReports);
            }
            fixture.AssertUnchanged(before);
            using (var writable = Store.Open(fixture.Root)) {
                Assert.False(File.Exists(fixture.Private(1)));
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "creating")));
                Assert.Empty(writable.RecoveryReports);
                var first = writable.Append(1, [1]).Unwrap();
                Assert.Equal(1u, PublicStoreFixture.FileId(first));
            }
        }
    }

    [Fact]
    public void WritableOpenRecoversMultipleActiveTailsAndReadOnlyNeverMutatesThem() {
        using var fixture = new PublicStoreFixture();
        FrameAddress first;
        FrameAddress second;
        byte[] identity;
        using (var store = Store.Create(fixture.Root)) {
            identity = store.StoreId.ToArray();
            var a = store.BeginAppend(1, 0, out first);
            var b = store.BeginAppend(1, 0, out second);
            PublicStoreFixture.Write(b, [2]);
            Assert.Equal(second, b.EndAppend(2).Unwrap());
            PublicStoreFixture.Write(a, [1]);
            Assert.Equal(first, a.EndAppend(1).Unwrap());
            store.ConfirmDurable();
        }
        byte[] firstImage = File.ReadAllBytes(fixture.Active(1));
        byte[] secondImage = File.ReadAllBytes(fixture.Active(2));
        File.WriteAllBytes(fixture.Active(1), firstImage[..^1]); // Only the final Fence is incomplete.
        File.WriteAllBytes(fixture.Active(2), [.. secondImage, 1]); // Partial next HeadLen is truncated.
        var before = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root); });
        fixture.AssertUnchanged(before);
        using (var recovered = Store.Open(fixture.Root)) {
            Assert.Equal(identity, recovered.StoreId.ToArray());
            Assert.Equal(2, recovered.RecoveryReports.Count);
            var completed = recovered.RecoveryReports[1];
            Assert.Equal(RbfTailRecoveryAction.CompletedTail, completed.Action);
            Assert.Equal(firstImage.Length - 1, completed.OriginalLength);
            Assert.Equal(firstImage.Length, completed.FinalLength);
            Assert.True(completed.AffectedFrameOffset.HasValue && completed.AffectedFrameOffset.Value >= PublicStoreFixture.InitializationBoundary);
            var truncated = recovered.RecoveryReports[2];
            Assert.Equal(RbfTailRecoveryAction.Truncated, truncated.Action);
            Assert.Equal(secondImage.Length + 1, truncated.OriginalLength);
            Assert.Equal(secondImage.Length, truncated.FinalLength);
            PublicStoreFixture.AssertFrame(recovered, first, 1, [1]);
            PublicStoreFixture.AssertFrame(recovered, second, 2, [2]);
            recovered.ConfirmDurable();
        }
        using var cold = Store.Open(fixture.Root);
        Assert.All(cold.RecoveryReports.Values, report => Assert.Equal(RbfTailRecoveryAction.None, report.Action));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("archive")]
    [InlineData("creating")]
    public void ValidGateCannotReplaceMissingRequiredLayout(string directory) {
        using var fixture = new PublicStoreFixture();
        fixture.CreateClosed();
        Directory.Delete(Path.Combine(fixture.Root, directory));
        var before = fixture.Snapshot();
        Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root); });
        Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root); });
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void ArchiveContentIsCheckedOnReadWithoutWritableRepairOrOpeningAudit() {
        using var fixture = new PublicStoreFixture();
        FrameAddress address;
        using (var store = Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary)) {
            address = store.Append(1, [1]).Unwrap();
            store.ConfirmDurable();
        }
        byte[] image = File.ReadAllBytes(fixture.Archive(1));
        File.WriteAllBytes(fixture.Archive(1), image[..^1]);
        var before = fixture.Snapshot();
        using (var writable = Store.Open(fixture.Root)) {
            Assert.Throws<InvalidDataException>(() => writable.ReadFrame(address));
            writable.ConfirmDurable(); // A pure read qualification failure does not fault this owner.
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void InvalidThresholdAndConfigRejectBeforeBootstrapAndCreateNeverAdoptsExistingContent() {
        using var fixture = new PublicStoreFixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => { using var unexpected = Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary - 1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { using var unexpected = Store.Open(fixture.Root, SizedPtr.MaxOffset + 1); });
        Assert.False(Directory.Exists(fixture.Root));
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "framestore.config.json"), "{\"unknown\":1}");
        var invalidConfig = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Create(fixture.Root); });
        fixture.AssertUnchanged(invalidConfig);
        File.Delete(Path.Combine(fixture.Root, "framestore.config.json"));
        File.WriteAllBytes(fixture.GatePath, []);
        var badGate = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Create(fixture.Root); });
        fixture.AssertUnchanged(badGate);
    }

    [Fact]
    public void EmptyControlBootstrapCanBeReusedAndMissingOrNonemptyControlIsNeverRepaired() {
        using var fixture = new PublicStoreFixture();
        Directory.CreateDirectory(fixture.Root);
        string control = Path.Combine(fixture.Root, "framestore.lock");
        File.WriteAllBytes(control, []);
        byte[] identity = fixture.CreateClosed();
        File.WriteAllBytes(control, [1]);
        RejectBothUnchanged(fixture);
        File.Delete(control);
        var before = fixture.Snapshot();
        Assert.Throws<FileNotFoundException>(() => { using var unexpected = Store.Open(fixture.Root); });
        Assert.Throws<FileNotFoundException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root); });
        fixture.AssertUnchanged(before);
        Assert.Equal(identity, File.ReadAllBytes(fixture.GatePath).AsSpan(4, 16).ToArray());
    }

    private static byte[] MakeRecoverableActive(PublicStoreFixture fixture) {
        byte[] identity;
        using (var store = Store.Create(fixture.Root)) {
            identity = store.StoreId.ToArray();
            store.Append(1, [1, 2, 3]).Unwrap();
            store.ConfirmDurable();
        }
        byte[] image = File.ReadAllBytes(fixture.Active(1));
        File.WriteAllBytes(fixture.Active(1), [.. image, 1]);
        return identity;
    }

    private static void RejectBothUnchanged(PublicStoreFixture fixture) {
        var before = fixture.Snapshot();
        for (int attempt = 0; attempt < 2; attempt++) {
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root); });
            fixture.AssertUnchanged(before);
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root); });
            fixture.AssertUnchanged(before);
        }
    }
}
