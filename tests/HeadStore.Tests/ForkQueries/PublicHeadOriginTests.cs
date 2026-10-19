using System.Buffers.Binary;
using Atelia.HeadStore.Tests.RecoveryReportsPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.ForkQueries;

public sealed class PublicHeadOriginTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootAndEmptyForkKeepTheirFixedOriginAcrossParentAdvanceChildPublishAndColdOpen(bool readOnly) {
        using var f = new ForkQueryFixture();
        HeadEntryId a0;
        HeadId child;
        HeadOrigin retained;
        using (var writer = f.Create()) {
            a0 = writer.CreateHead(7, [1], out _).Unwrap();
            Assert.Null(writer.ReadHeadOrigin(a0.HeadId).Unwrap().SourceEntryId);
            child = writer.ForkHead(12, a0, out _).Unwrap();
            retained = writer.ReadHeadOrigin(child).Unwrap();
            Assert.Equal(a0, retained.SourceEntryId);
            Assert.Equal(a0, writer.ReadHead(child).Unwrap().Id);
            writer.PublishHead(a0.HeadId, [2], out _).Unwrap();
            Assert.Equal(a0, writer.ReadHeadOrigin(child).Unwrap().SourceEntryId);
        }
        using (var cold = f.Open(readOnly)) {
            Assert.Null(cold.ReadHeadOrigin(a0.HeadId).Unwrap().SourceEntryId);
            Assert.Equal(a0, cold.ReadHeadOrigin(child).Unwrap().SourceEntryId);
        }
        using (var writer = f.Open()) {
            HeadEntryId c1 = writer.PublishHead(child, [3], out _).Unwrap();
            Assert.Equal(child, c1.HeadId);
            Assert.Equal(a0, writer.ReadHeadOrigin(child).Unwrap().SourceEntryId);
        }
        using (var cold = f.Open(readOnly)) {
            Assert.Equal(a0, cold.ReadHeadOrigin(child).Unwrap().SourceEntryId);
        }
        Assert.Equal(a0, retained.SourceEntryId); // The returned declaration survives every owner above.
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("past-tail")]
    [InlineData("corrupt-entry")]
    public void DeclarationDoesNotGrantSourceEntryQualification(string defect) {
        using var f = new ForkQueryFixture();
        using (var initialized = f.Create()) { }
        HeadEntryId declared;
        if (defect == "corrupt-entry") {
            declared = f.WriteMirror(7, null, new QueryRecord(ForkQueryFixture.Bytes()))[0];
            f.FlipPayload(declared);
        }
        else {
            if (defect == "past-tail") { f.WriteMirror(7, null); }
            declared = f.DeclaredPoint(7);
        }
        f.WriteMirror(12, declared); // Header only: no synthetic local Entry is required.
        using var owner = f.Open(readOnly: true);
        Assert.Equal(declared, owner.ReadHeadOrigin(f.Id(12)).Unwrap().SourceEntryId);
        Assert.True(owner.ReadEntry(declared).IsFailure);
        Assert.Equal(declared, owner.ReadHeadOrigin(f.Id(12)).Unwrap().SourceEntryId);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("head-id")]
    [InlineData("length")]
    [InlineData("source-ticket")]
    [InlineData("self-source")]
    [InlineData("crc")]
    public void InvalidTargetHeaderFailsButDoesNotContaminateAnUnrelatedRoot(string defect) {
        using var f = new ForkQueryFixture();
        using (var initialized = f.Create()) { }
        f.WriteMirror(5, null, new QueryRecord(ForkQueryFixture.Bytes()));
        byte[] header = defect is "source-ticket" or "self-source"
            ? f.Header(7, f.DeclaredPoint(9)) : f.Header(7);
        switch (defect) {
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue); break;
            case "identity": header[4] ^= 1; break;
            case "head-id": BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 8); break;
            case "length": header = [.. header, 0]; break;
            case "source-ticket": BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(28), 0); break;
            case "self-source": BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 7); break;
        }
        f.WriteHeaderMirror(7, header);
        if (defect == "crc") { f.FlipHeaderPayload(7); }
        f.AssertFactoryQualified(5, 7);
        using var owner = f.Open(readOnly: true);
        Assert.Null(owner.ReadHeadOrigin(f.Id(5)).Unwrap().SourceEntryId);
        var result = owner.ReadHeadOrigin(f.Id(7));
        Assert.True(result.IsFailure);
        if (defect == "crc") { Assert.StartsWith("Rbf.", result.Error!.ErrorCode); }
        else { Assert.Equal("HeadStore.InvalidHeadHeader", result.Error!.ErrorCode); }
        Assert.Null(owner.ReadHeadOrigin(f.Id(5)).Unwrap().SourceEntryId);
    }

    [Fact]
    public void DefaultMissingAndDisposedTargetsDoNotMasqueradeAsRoots() {
        using var f = new ForkQueryFixture();
        using var owner = f.Create();
        Assert.Equal("HeadStore.HeadNotFound", owner.ReadHeadOrigin(default).Error!.ErrorCode);
        Assert.Equal("HeadStore.HeadNotFound", owner.ReadHeadOrigin(f.Id(7)).Error!.ErrorCode);
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.ReadHeadOrigin(default));
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LazyRecoveryTouchesOnlyTargetAndReadOnlyFailurePreservesBytes(bool completeTail) {
        using var f = new ForkQueryFixture();
        HeadId target;
        using (var writer = f.Create()) {
            target = writer.CreateHead(7, [1, 2, 3], out _).Unwrap().HeadId;
            writer.CreateHead(12, [4, 5, 6], out _).Unwrap();
        }
        TailImage targetImage = RecoveryReportsFixture.Damage(f.HeadPath(7), completeTail);
        TailImage unrelatedImage = RecoveryReportsFixture.Damage(f.HeadPath(12), completeTail: false);
        using (var readOnly = f.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => readOnly.ReadHeadOrigin(target));
            Assert.Empty(readOnly.RecoveryReports);
            readOnly.EnsureUsable();
        }
        Assert.Equal(targetImage.Damaged, File.ReadAllBytes(targetImage.Path));
        Assert.Equal(unrelatedImage.Damaged, File.ReadAllBytes(unrelatedImage.Path));
        using (var writer = f.Open()) {
            Assert.Null(writer.ReadHeadOrigin(target).Unwrap().SourceEntryId);
            targetImage.AssertReport(Assert.Single(writer.RecoveryReports), HeadStoreRecoveryFileKind.Head, 7);
            targetImage.AssertRestored();
            Assert.Equal(unrelatedImage.Damaged, File.ReadAllBytes(unrelatedImage.Path));
            f.AssertReleased(7);
            Assert.Null(writer.ReadHeadOrigin(target).Unwrap().SourceEntryId);
            Assert.Single(writer.RecoveryReports);
        }
    }
}
