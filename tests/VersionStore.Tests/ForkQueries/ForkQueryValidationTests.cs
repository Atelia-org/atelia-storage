using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.VersionStore.Tests.ForkQueries;

public sealed class ForkQueryValidationTests {
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PublicDeclaredEdgeRemainsQueryableWithBadInitialOrHeadPayloadAndQualifiedFactoryEof(
        bool badInitial, bool badHead) {
        using var fixture = new ForkQueryFixture();
        RefRevision[] source = new RefRevision[3];
        RefRevision child;
        using (var store = fixture.Create()) {
            source[0] = store.CreateRef(7, ForkQueryFixture.Bytes(0xa0), out _).Unwrap();
            source[1] = store.PublishRef(source[0].RefId, ForkQueryFixture.Bytes(0xa1), out _).Unwrap();
            source[2] = store.PublishRef(source[0].RefId, ForkQueryFixture.Bytes(0xa2), out _).Unwrap();
            child = store.ForkRef(12, source[1], out _).Unwrap();
        }
        // Closed-owner byte damage preserves RBF framing/trailers. The actual public factory
        // qualification is checked separately; these mirrors are not crash or device faults.
        if (badInitial) { fixture.FlipPayload(source[0]); }
        if (badHead) { fixture.FlipPayload(source[2]); }
        fixture.AssertFactoryQualified(7, 12);
        using var readOnly = fixture.Open(readOnly: true);
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (child.RefId, source[1].RefId, source[1].Ticket));
        if (badHead) {
            Assert.True(readOnly.ReadRef(source[0].RefId).IsFailure);
        }
        else {
            Assert.Equal(source[2], readOnly.ReadRef(source[0].RefId).Unwrap().Revision);
        }
        var actualHistory = readOnly.ReadRefHistory(source[0].RefId, ForkQueryFixture.Unlimited, _ => true);
        Assert.True(actualHistory.IsFailure);
        Assert.StartsWith("Rbf.", actualHistory.Error!.ErrorCode);
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (child.RefId, source[1].RefId, source[1].Ticket));
    }

    [Theory]
    [InlineData(false, "kind")]
    [InlineData(true, "kind")]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    public void ControlledInvalidInitialOrHeadEnvelopeDoesNotBecomeAHeaderQueryRequirement(bool badHead, string kind) {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        var good = new QueryRecord(ForkQueryFixture.Bytes());
        QueryRecord bad = kind == "kind" ? good with { Tag = 2 } : good with { MetadataLength = 7 };
        RefRevision[] source = fixture.WriteMirror(9, null, badHead ? good : bad, badHead ? bad : good);
        RefRevision selected = badHead ? source[0] : source[1];
        RefRevision child = fixture.WriteMirror(3, selected, good)[0];
        fixture.AssertFactoryQualified(9, 3);
        using var readOnly = fixture.Open(readOnly: true);
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (child.RefId, selected.RefId, selected.Ticket));
        if (badHead) {
            Assert.Equal("VersionStore.InvalidRefRecord", readOnly.ReadRef(source[0].RefId).Error!.ErrorCode);
        }
        else {
            Assert.Equal(source[1], readOnly.ReadRef(source[0].RefId).Unwrap().Revision);
        }
        Assert.Equal("VersionStore.InvalidRefRecord",
            readOnly.ReadRefHistory(source[0].RefId, ForkQueryFixture.Unlimited, _ => true).Error!.ErrorCode);
    }

    [Fact]
    public void HeaderOnlyMirrorsDeclareAnEdgeWithoutProvingInitializationOrIssuingTheDeclaredSourceRevision() {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        fixture.WriteMirror(9, null);
        RefRevision declaration = fixture.DeclaredPoint(9);
        fixture.WriteMirror(3, declaration);
        fixture.AssertFactoryQualified(9, 3);
        using var readOnly = fixture.Open(readOnly: true);
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (fixture.Id(3), declaration.RefId, declaration.Ticket));
        Assert.Equal("VersionStore.InvalidRefRecord", readOnly.ReadRef(3u).Error!.ErrorCode);
        Assert.Equal("VersionStore.InvalidRefRecord", readOnly.ReadRef(9u).Error!.ErrorCode);
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (fixture.Id(3), declaration.RefId, declaration.Ticket));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("snapshot-first")]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("ref-id")]
    [InlineData("length")]
    [InlineData("origin-ticket")]
    [InlineData("crc")]
    public void MissingOrInvalidNecessaryHeaderRejectsDespiteAnAdmittedRbfFile(string damage) {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        if (damage is "empty" or "snapshot-first") {
            using IRbfFile file = RbfFile.CreateNew(fixture.RefPath(7), RbfCacheMode.Off);
            if (damage == "snapshot-first") {
                var record = new QueryRecord(ForkQueryFixture.Bytes());
                file.Append(record.Tag, record.Payload, record.Metadata).Unwrap();
            }
            file.DurableFlush();
        }
        else {
            byte[] header = damage == "origin-ticket" ? fixture.Header(7, fixture.DeclaredPoint(9)) : fixture.Header(7);
            switch (damage) {
                case "version": BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue); break;
                case "identity": header[4] ^= 1; break;
                case "ref-id": BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 8); break;
                case "length": header = [.. header, 0]; break;
                case "origin-ticket": BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(28), 0); break;
            }
            fixture.WriteHeaderMirror(7, header);
            if (damage == "crc") { fixture.FlipHeaderPayload(7); }
        }
        fixture.AssertFactoryQualified(7);
        using var readOnly = fixture.Open(readOnly: true);
        var queried = readOnly.ListForks(1);
        Assert.True(queried.IsFailure);
        if (damage == "crc") { Assert.StartsWith("Rbf.", queried.Error!.ErrorCode); }
        else { Assert.Equal("VersionStore.InvalidRefHeader", queried.Error!.ErrorCode); }
        Assert.Equal(new[] { fixture.Id(7) }, readOnly.ListRefs().Unwrap());
    }

    [Fact]
    public void MissingDeclaredSourceRejectsTheCompleteGraph() {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        fixture.WriteMirror(3, fixture.DeclaredPoint(9));
        using var readOnly = fixture.Open(readOnly: true);
        var queried = readOnly.ListForks(1);
        Assert.True(queried.IsFailure);
        Assert.Equal("VersionStore.InvalidForkOrigin", queried.Error!.ErrorCode);
    }

    [Fact]
    public void ControlledSelfLinkIsRejectedByTheNecessaryHeaderQualification() {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        fixture.WriteMirror(9, null);
        byte[] header = fixture.Header(3, fixture.DeclaredPoint(9));
        // The codec deliberately refuses to write self links. A raw payload edit before public
        // Append recomputes the complete RBF CRC and isolates the VS header rejection.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 3);
        fixture.WriteHeaderMirror(3, header);
        fixture.AssertFactoryQualified(3, 9);
        using var readOnly = fixture.Open(readOnly: true);
        Assert.Equal("VersionStore.InvalidRefHeader", readOnly.ListForks(2).Error!.ErrorCode);
    }

    [Fact]
    public void IndependentCyclicComponentRejectsEvenWhenAnotherComponentHasAHealthyPublicEdge() {
        using var fixture = new ForkQueryFixture();
        using (var store = fixture.Create()) {
            RefRevision root = store.CreateRef(7, [1], out _).Unwrap();
            store.ForkRef(12, root, out _).Unwrap();
        }
        fixture.WriteMirror(3, fixture.DeclaredPoint(4));
        fixture.WriteMirror(4, fixture.DeclaredPoint(3));
        fixture.AssertFactoryQualified(3, 4, 7, 12);
        using var readOnly = fixture.Open(readOnly: true);
        var queried = readOnly.ListForks(4);
        Assert.True(queried.IsFailure);
        Assert.Equal("VersionStore.InvalidForkOrigin", queried.Error!.ErrorCode);
        Assert.Equal(4, readOnly.ListRefs().Unwrap().Count);
    }

    [Theory]
    [InlineData("past-tail")]
    [InlineData("wrong-length")]
    [InlineData("clone-mismatch")]
    [InlineData("source-crc")]
    public void DeclaredSourceTicketAndCloneContentsAreNotAuditedUntilHistoryActuallyVisitsThem(string defect) {
        using var fixture = new ForkQueryFixture();
        RefRevision source;
        using (var store = fixture.Create()) {
            source = store.CreateRef(7, ForkQueryFixture.Bytes(), out _).Unwrap();
            store.PublishRef(source.RefId, ForkQueryFixture.Bytes(0x52), out _).Unwrap();
        }
        RefRevision declared = defect switch {
            "past-tail" => fixture.DeclaredPoint(7),
            "wrong-length" => RefRevision.Create(source.RefId, SizedPtr.Create(source.Ticket.Offset, source.Ticket.Length + 4)),
            _ => source
        };
        byte[] clone = ForkQueryFixture.Bytes(defect == "clone-mismatch" ? (byte)0x53 : (byte)0x51);
        RefRevision child = fixture.WriteMirror(12, declared, new QueryRecord(clone))[0];
        if (defect == "source-crc") { fixture.FlipPayload(source); }
        fixture.AssertFactoryQualified(7, 12);
        using var readOnly = fixture.Open(readOnly: true);
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (child.RefId, declared.RefId, declared.Ticket));
        var delivered = new List<RefRevision>();
        var history = readOnly.ReadRefHistory(child.RefId, ForkQueryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.True(history.IsFailure);
        Assert.Equal(new[] { child }, delivered);
        if (defect is "past-tail" or "clone-mismatch") {
            Assert.Equal("VersionStore.InvalidForkOrigin", history.Error!.ErrorCode);
        }
        else { Assert.StartsWith("Rbf.", history.Error!.ErrorCode); }
        ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (child.RefId, declared.RefId, declared.Ticket));
    }

    [Fact]
    public void HeaderQueryDoesNotBypassTheUnderlyingFactoryCurrentEofStructuralQualification() {
        using var fixture = new ForkQueryFixture();
        RefRevision initial;
        RefRevision child;
        using (var store = fixture.Create()) {
            initial = store.CreateRef(7, ForkQueryFixture.Bytes(), out _).Unwrap();
            store.PublishRef(initial.RefId, ForkQueryFixture.Bytes(0x52), out _).Unwrap();
            child = store.ForkRef(12, initial, out _).Unwrap();
        }
        fixture.FlipFollowingFence(initial);
        // This shared Fence is also the current head's left boundary. The failure belongs to
        // RBF factory admission, while the child's complete header and value remain readable.
        Assert.Throws<InvalidDataException>(() => {
            using IRbfFile unexpected = RbfFile.OpenReadOnlyExisting(fixture.RefPath(7), RbfCacheMode.Off);
        });
        fixture.AssertFactoryQualified(12);
        using var readOnly = fixture.Open(readOnly: true);
        Assert.Equal(child, readOnly.ReadRef(child.RefId).Unwrap().Revision);
        Assert.Throws<InvalidDataException>(() => { readOnly.ListForks(2); });
    }
}
