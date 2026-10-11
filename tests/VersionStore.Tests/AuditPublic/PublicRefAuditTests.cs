using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.VersionStore.Tests.AuditPublic;

public sealed class PublicRefAuditTests {
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HealthyCurrentHeadDoesNotHideBadInitialOrIntermediatePayloadCrc(int damagedIndex) {
        using var fixture = new AuditPublicFixture();
        RefRevision[] revisions = new RefRevision[3];
        using (var writer = fixture.Storage.Open()) {
            revisions[0] = writer.CreateRef(7, AuditPublicFixture.Bytes(0xa0), out _).Unwrap();
            revisions[1] = writer.PublishRef(revisions[0].RefId, AuditPublicFixture.Bytes(0xa1), out _).Unwrap();
            revisions[2] = writer.PublishRef(revisions[0].RefId, AuditPublicFixture.Bytes(0xa2), out _).Unwrap();
        }
        AuditPublicFixture.FlipPayload(fixture.RefPath(7), revisions[damagedIndex].Ticket);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(revisions[2], readOnly.ReadRef(7u).Unwrap().Revision);
            var audited = readOnly.Audit();
            Assert.True(audited.IsFailure);
            Assert.StartsWith("Rbf.", audited.Error!.ErrorCode);
            Assert.Equal(revisions[2], readOnly.ReadRef(7u).Unwrap().Revision);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("duplicate-header")]
    [InlineData("short-meta")]
    [InlineData("empty-root")]
    [InlineData("oversized-root")]
    [InlineData("tombstone")]
    public void EveryPhysicalSnapshotEnvelopeIsRequiredEvenWhenCurrentReadSucceeds(string defect) {
        using var fixture = new AuditPublicFixture();
        byte[] payload = defect switch {
            "empty-root" => [],
            "oversized-root" => new byte[VersionStore.MaxRootPayloadLength + 1],
            _ => AuditPublicFixture.Bytes()
        };
        uint kind = defect == "unknown-kind" ? 17u : defect == "duplicate-header" ? 0u : 1u;
        byte[] metadata = new byte[defect == "short-meta" ? 7 : 8];
        var tickets = fixture.WriteRef(7, records: [
            new AuditRefRecord(payload, kind, metadata), new AuditRefRecord([0x52])
        ]);
        if (defect == "tombstone") { AuditPublicFixture.MakeTombstone(fixture.RefPath(7), tickets[0]); }
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 0x52 }, readOnly.ReadRef(7u).Unwrap().Payload.ToArray());
            Assert.Empty(readOnly.ListForks(1).Unwrap());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(new byte[] { 0x52 }, readOnly.ReadRef(7u).Unwrap().Payload.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("empty-rbf")]
    [InlineData("header-only")]
    [InlineData("snapshot-first")]
    public void AFormalPathDoesNotProveTheRequiredHeaderAndInitialSnapshot(string defect) {
        using var fixture = new AuditPublicFixture();
        if (defect == "header-only") { fixture.WriteRef(7); }
        else {
            using IRbfFile file = RbfFile.CreateNew(fixture.RefPath(7), RbfCacheMode.Off);
            if (defect == "snapshot-first") { file.Append(1, [1], new byte[8]).Unwrap(); }
            file.DurableFlush();
        }
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(7u, Assert.Single(readOnly.ListRefs().Unwrap()).LocalId);
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("past-tail")]
    [InlineData("same-offset-wrong-length")]
    [InlineData("clone-bytes")]
    [InlineData("clone-length")]
    public void DeclaredForkEdgesNeedExactSourceTicketsAndInitialByteClones(string defect) {
        using var fixture = new AuditPublicFixture();
        RefRevision source;
        using (var writer = fixture.Storage.Open()) {
            source = writer.CreateRef(7, AuditPublicFixture.Bytes(), out _).Unwrap();
            writer.PublishRef(source.RefId, [0x52], out _).Unwrap();
        }
        SizedPtr declared = defect switch {
            "past-tail" => SizedPtr.Create(1_000_000, source.Ticket.Length),
            "same-offset-wrong-length" => SizedPtr.Create(source.Ticket.Offset, source.Ticket.Length + 4),
            _ => source.Ticket
        };
        byte[] clone = defect switch {
            "clone-bytes" => AuditPublicFixture.Bytes(0x53),
            "clone-length" => AuditPublicFixture.Bytes(length: 127),
            _ => AuditPublicFixture.Bytes()
        };
        fixture.WriteRef(12, 7, declared, new AuditRefRecord(clone), new AuditRefRecord([0x54]));
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            ForkInfo edge = Assert.Single(readOnly.ListForks(2).Unwrap());
            Assert.Equal(declared, edge.SourceSnapshotTicket);
            Assert.Equal(new byte[] { 0x54 }, readOnly.ReadRef(12u).Unwrap().Payload.ToArray());
            Assert.Equal(new byte[] { 0x52 }, readOnly.ReadRef(7u).Unwrap().Payload.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Single(readOnly.ListForks(2).Unwrap());
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void AReadableEmbeddedFrameAndValidBoundaryWitnessDoNotMakeTheTicketAMainChainMember() {
        using var fixture = new AuditPublicFixture();
        SizedPtr outer = fixture.WriteRef(7, records: [new AuditRefRecord(new byte[256])])[0];
        byte[] fakePayload = [0x31, 0x32, 0x33];
        SizedPtr fake = fixture.EmbedReadableFrame(fixture.RefPath(7), outer, fakePayload);
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(7), RbfCacheMode.Off)) {
            using var random = file.ReadPooledFrame(fake).Unwrap();
            Assert.Equal(fakePayload, random.PayloadAndMeta[..^random.TailMetaLength].ToArray());
            Assert.True(file.GetScanBoundaryAfter(fake).IsSuccess);
            var forward = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(forward.MoveNext()); // Actual header.
            Assert.True(forward.MoveNext());
            Assert.Equal(outer, forward.Current.Ticket);
            Assert.False(forward.MoveNext());
            Assert.Null(forward.TerminationError);
        }
        fixture.WriteRef(12, 7, fake, new AuditRefRecord(fakePayload));
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(fake, Assert.Single(readOnly.ListForks(2).Unwrap()).SourceSnapshotTicket);
            Assert.Equal(fakePayload, readOnly.ReadRef(12u).Unwrap().Payload.ToArray());
            Assert.True(readOnly.ReadRef(7u).IsSuccess); // The containing Snapshot also has a complete valid CRC.
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void MissingForkSourceRejectsDespiteHealthyLocalSnapshots() {
        using var fixture = new AuditPublicFixture();
        fixture.WriteRef(3, 9, SizedPtr.Create(60, 40), new AuditRefRecord([1]));
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 1 }, readOnly.ReadRef(3u).Unwrap().Payload.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void CyclicInitializedForkComponentRejectsAlongsideAHealthyPublicComponent() {
        using var fixture = new AuditPublicFixture();
        using (var writer = fixture.Storage.Open()) {
            RefRevision root = writer.CreateRef(7, [2], out _).Unwrap();
            writer.ForkRef(12, root, out _).Unwrap();
        }
        fixture.WriteRef(3, 4, SizedPtr.Create(72, 40), new AuditRefRecord([1]));
        fixture.WriteRef(4, 3, SizedPtr.Create(72, 40), new AuditRefRecord([1]));
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(4, readOnly.ListRefs().Unwrap().Count);
            Assert.True(readOnly.ReadRef(3u).IsSuccess);
            Assert.True(readOnly.ReadRef(4u).IsSuccess);
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }
}
