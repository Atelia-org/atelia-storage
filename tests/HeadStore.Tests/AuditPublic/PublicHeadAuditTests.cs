using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.HeadStore.Tests.AuditPublic;

public sealed class PublicHeadAuditTests {
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HealthyCurrentHeadDoesNotHideBadInitialOrIntermediatePayloadCrc(int damagedIndex) {
        using var fixture = new AuditPublicFixture();
        HeadEntryId[] entryIds = new HeadEntryId[3];
        using (var writer = fixture.Storage.Open()) {
            entryIds[0] = writer.CreateHead(7, AuditPublicFixture.Bytes(0xa0), out _).Unwrap();
            entryIds[1] = writer.PublishHead(entryIds[0].HeadId, AuditPublicFixture.Bytes(0xa1), out _).Unwrap();
            entryIds[2] = writer.PublishHead(entryIds[0].HeadId, AuditPublicFixture.Bytes(0xa2), out _).Unwrap();
        }
        AuditPublicFixture.FlipPayload(fixture.HeadPath(7), entryIds[damagedIndex].Ticket);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(entryIds[2], readOnly.ReadHead(7u).Unwrap().Id);
            var audited = readOnly.Audit();
            Assert.True(audited.IsFailure);
            Assert.StartsWith("Rbf.", audited.Error!.ErrorCode);
            Assert.Equal(entryIds[2], readOnly.ReadHead(7u).Unwrap().Id);
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
    public void EveryPhysicalEntryEnvelopeIsRequiredEvenWhenCurrentReadSucceeds(string defect) {
        using var fixture = new AuditPublicFixture();
        byte[] payload = defect switch {
            "empty-root" => [],
            "oversized-root" => new byte[HeadStore.MaxHeadEntryPayloadLength + 1],
            _ => AuditPublicFixture.Bytes()
        };
        uint kind = defect == "unknown-kind" ? 17u : defect == "duplicate-header" ? 0u : 1u;
        byte[] metadata = new byte[defect == "short-meta" ? 7 : 8];
        var tickets = fixture.WriteHead(7, records: [
            new AuditHeadRecord(payload, kind, metadata), new AuditHeadRecord([0x52])
        ]);
        if (defect == "tombstone") { AuditPublicFixture.MakeTombstone(fixture.HeadPath(7), tickets[0]); }
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 0x52 }, readOnly.ReadHead(7u).Unwrap().Payload.ToArray());
            Assert.Empty(readOnly.ListHeadOrigins(1).Unwrap());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(new byte[] { 0x52 }, readOnly.ReadHead(7u).Unwrap().Payload.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("empty-rbf")]
    [InlineData("header-only")]
    [InlineData("entry-first")]
    public void AFormalPathDoesNotProveTheRequiredHeaderAndInitialEntry(string defect) {
        using var fixture = new AuditPublicFixture();
        if (defect == "header-only") { fixture.WriteHead(7); }
        else {
            using IRbfFile file = RbfFile.CreateNew(fixture.HeadPath(7), RbfCacheMode.Off);
            if (defect == "entry-first") { file.Append(1, [1], new byte[8]).Unwrap(); }
            file.DurableFlush();
        }
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(7u, Assert.Single(readOnly.ListHeads().Unwrap()).LocalId);
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("past-tail")]
    [InlineData("same-offset-wrong-length")]
    public void DeclaredForkEdgesNeedExactSourceTicketsEvenWithHealthyLocalEntries(string defect) {
        using var fixture = new AuditPublicFixture();
        HeadEntryId source;
        using (var writer = fixture.Storage.Open()) {
            source = writer.CreateHead(7, AuditPublicFixture.Bytes(), out _).Unwrap();
            writer.PublishHead(source.HeadId, [0x52], out _).Unwrap();
        }
        SizedPtr declared = defect switch {
            "past-tail" => SizedPtr.Create(1_000_000, source.Ticket.Length),
            "same-offset-wrong-length" => SizedPtr.Create(source.Ticket.Offset, source.Ticket.Length + 4),
            _ => source.Ticket
        };
        fixture.WriteHead(12, 7, declared, new AuditHeadRecord([0x53]), new AuditHeadRecord([0x54]));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            HeadOriginInfo edge = Assert.Single(readOnly.ListHeadOrigins(2).Unwrap());
            Assert.Equal(declared, edge.SourceEntryTicket);
            Assert.Equal(new byte[] { 0x54 }, readOnly.ReadHead(12u).Unwrap().Payload.ToArray());
            Assert.Equal(new byte[] { 0x52 }, readOnly.ReadHead(7u).Unwrap().Payload.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Single(readOnly.ListHeadOrigins(2).Unwrap());
        }
        fixture.AssertUnchanged(before);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkMayHaveNoLocalEntryOrAnIndependentFirstPayload(bool publishLocal) {
        using var fixture = new AuditPublicFixture();
        HeadEntryId source;
        using (var writer = fixture.Storage.Open()) {
            source = writer.CreateHead(7, [0x51], out _).Unwrap();
        }
        AuditHeadRecord[] records = publishLocal ? [new AuditHeadRecord([0x52, 0x53])] : [];
        fixture.WriteHead(12, 7, source.Ticket, records);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            HeadId child = Assert.Single(readOnly.ListHeads().Unwrap(), id => id.LocalId == 12);
            if (publishLocal) { Assert.Equal(new byte[] { 0x52, 0x53 }, readOnly.ReadHead(child).Unwrap().Payload.ToArray()); }
            else { Assert.Equal(source, readOnly.ReadHead(child).Unwrap().Id); }
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 2, publishLocal ? 2 : 1, 1);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void AReadableEmbeddedFrameAndValidBoundaryWitnessDoNotMakeTheTicketAMainChainMember() {
        using var fixture = new AuditPublicFixture();
        SizedPtr outer = fixture.WriteHead(7, records: [new AuditHeadRecord(new byte[256])])[0];
        byte[] fakePayload = [0x31, 0x32, 0x33];
        SizedPtr fake = fixture.EmbedReadableFrame(fixture.HeadPath(7), outer, fakePayload);
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(7), RbfCacheMode.Off)) {
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
        fixture.WriteHead(12, 7, fake, new AuditHeadRecord(fakePayload));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(fake, Assert.Single(readOnly.ListHeadOrigins(2).Unwrap()).SourceEntryTicket);
            Assert.Equal(fakePayload, readOnly.ReadHead(12u).Unwrap().Payload.ToArray());
            Assert.True(readOnly.ReadHead(7u).IsSuccess); // The containing Entry also has a complete valid CRC.
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void MissingForkSourceRejectsDespiteHealthyLocalEntries() {
        using var fixture = new AuditPublicFixture();
        fixture.WriteHead(3, 9, SizedPtr.Create(60, 40), new AuditHeadRecord([1]));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 1 }, readOnly.ReadHead(3u).Unwrap().Payload.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void CyclicInitializedForkComponentRejectsAlongsideAHealthyPublicComponent() {
        using var fixture = new AuditPublicFixture();
        using (var writer = fixture.Storage.Open()) {
            HeadEntryId root = writer.CreateHead(7, [2], out _).Unwrap();
            writer.ForkHead(12, root, out _).Unwrap();
        }
        fixture.WriteHead(3, 4, SizedPtr.Create(72, 40), new AuditHeadRecord([1]));
        fixture.WriteHead(4, 3, SizedPtr.Create(72, 40), new AuditHeadRecord([1]));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(4, readOnly.ListHeads().Unwrap().Count);
            Assert.True(readOnly.ReadHead(3u).IsSuccess);
            Assert.True(readOnly.ReadHead(4u).IsSuccess);
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }
}
