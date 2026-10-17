using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.HeadStore.Tests.History;

public sealed class InheritedHistoryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedChainIncludesOnlyRealPublicationsAndExactSourcePrefixAndExcludesBadSourceSuffix(bool readOnly) {
        using var fixture = new HistoryFixture();
        HeadEntryId[] a = fixture.SeedPublic(1,
            HistoryFixture.Bytes(value: 0xa0), HistoryFixture.Bytes(value: 0xa1),
            HistoryFixture.Bytes(value: 0xa2), HistoryFixture.Bytes(value: 0xa3));
        HeadEntryId[] b = fixture.WriteMirror(2, a[2],
            new HistoryRecord(HistoryFixture.Bytes(value: 0xb1), -30));
        HeadEntryId[] c = fixture.WriteMirror(3, b[0],
            new HistoryRecord(HistoryFixture.Bytes(value: 0xc1), 10));
        fixture.FlipPayload(a[3]);
        using var store = fixture.Open(readOnly);
        var entries = HistoryFixture.Collect(store, fixture.Id(3));
        Assert.Equal(new[] { c[0], b[0], a[2], a[1], a[0] }, entries.Select(entry => entry.Id));
        Assert.Equal(new long[] { 10, -30 }, entries.Take(2).Select(entry => entry.RecordedAtUnixMs));
        if (!readOnly) { fixture.AssertReleased(1, 2, 3); }
        // The excluded A3 is still bad: history success cannot certify the source's current head.
        Assert.StartsWith("Rbf.", store.ReadHead(fixture.Id(1)).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(0u, 8)]
    [InlineData(2u, 8)]
    [InlineData(1u, 7)]
    public void ExcludedSourceSuffixEnvelopeIsNotTraversed(uint tag, int metadataLength) {
        using var fixture = new HistoryFixture();
        HeadEntryId[] source = fixture.WriteMirror(1, null,
            new HistoryRecord([0x11], 1), new HistoryRecord([0x12], 2), new HistoryRecord([0x13], 3, tag, metadataLength));
        HeadEntryId child = fixture.WriteMirror(2, source[1], new HistoryRecord([0x12], 4))[0];
        using var store = fixture.Open();
        var entries = HistoryFixture.Collect(store, fixture.Id(2));
        Assert.Equal(new[] { child, source[1], source[0] }, entries.Select(entry => entry.Id));
        Assert.Equal("HeadStore.InvalidHeadRecord", store.ReadHead(fixture.Id(1)).Error!.ErrorCode);
    }

    [Fact]
    public void StopAtChildInitialDoesNotResolveItsMissingSourceAndFullTraversalFails() {
        using var fixture = new HistoryFixture();
        var source = fixture.DeclaredInitial(19, 1);
        HeadEntryId child = fixture.WriteMirror(2, source, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadHeadHistory(child.HeadId, new(1, 5), _ => false).Unwrap());
        int calls = 0;
        var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, _ => { calls++; return true; });
        Assert.Equal("HeadStore.HeadNotFound", result.Error!.ErrorCode);
        Assert.Equal(1, calls);
        fixture.AssertReleased(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstLocalPayloadMayDifferFromSourceAndBothRealEntriesAreDelivered(bool sameLength) {
        using var fixture = new HistoryFixture();
        byte[] sourceBytes = "{\"x\":1}"u8.ToArray();
        byte[] localBytes = sameLength ? "{\"x\":2}"u8.ToArray() : "{ \"x\":1 }"u8.ToArray();
        HeadEntryId source = fixture.SeedPublic(1, sourceBytes)[0];
        HeadEntryId child = fixture.WriteMirror(2, source, new HistoryRecord(localBytes))[0];
        using var store = fixture.Open();
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadHeadHistory(child.HeadId, new(1, 5), _ => false).Unwrap());
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
        Assert.Equal(new[] { child, source }, delivered);
        fixture.AssertReleased(1, 2);
        Assert.Equal(source, store.ReadHead(source.HeadId).Unwrap().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredBadSourceAnchorFailsWithoutDeliveringItAndRetainsOnlyRealChildPrefix(bool hasLocalEntry) {
        using var fixture = new HistoryFixture();
        byte[] payload = HistoryFixture.Bytes();
        HeadEntryId source = fixture.SeedPublic(1, payload)[0];
        HeadEntryId[] local = fixture.WriteMirror(2, source, hasLocalEntry ? [new HistoryRecord(payload)] : []);
        fixture.FlipPayload(source);
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(fixture.Id(2), HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(local, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void RequiredBadOlderSourceNodeFailsInsteadOfCompletingOrSkippingIt() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] source = fixture.SeedPublic(1, HistoryFixture.Bytes(value: 0x31), HistoryFixture.Bytes(value: 0x32));
        HeadEntryId child = fixture.WriteMirror(2, source[1], new HistoryRecord(HistoryFixture.Bytes(value: 0x32)))[0];
        fixture.FlipPayload(source[0]);
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(new[] { child, source[1] }, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void InheritanceCycleIsRejectedAfterTheCheckedPrefixAndTemporaryHandlesAreReleased() {
        using var fixture = new HistoryFixture();
        HeadEntryId declaredA = fixture.DeclaredInitial(1, 1);
        HeadEntryId declaredB = fixture.DeclaredInitial(2, 1);
        HeadEntryId a = fixture.WriteMirror(1, declaredB, new HistoryRecord([1]))[0];
        HeadEntryId b = fixture.WriteMirror(2, declaredA, new HistoryRecord([1]))[0];
        Assert.Equal(declaredA, a);
        Assert.Equal(declaredB, b);
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(a.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { a, b }, delivered);
        fixture.AssertReleased(1, 2);
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadHeadHistory(a.HeadId, HistoryFixture.Unlimited, _ => false).Unwrap());
    }

    [Fact]
    public void CallbackSourceQueriesReuseTheTemporaryAnchorFileAndDoNotReplaceItsHistoryUpperBound() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] source = fixture.SeedPublic(1, [1], [2], [3]);
        HeadEntryId child = fixture.WriteMirror(2, source[1], new HistoryRecord([2]))[0];
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            if (entry.Id.HeadId == source[0].HeadId) {
                Assert.Equal(source[2], store.ReadHead(source[0].HeadId).Unwrap().Id);
                Assert.Equal(source[2], store.ReadHead(1u).Unwrap().Id);
            }
            return true;
        });
        Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
        Assert.Equal(new[] { child, source[1], source[0] }, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void PreviouslyOwnedSourceHandleIsBorrowedAndRemainsUsableAfterHistory() {
        using var fixture = new HistoryFixture();
        HeadEntryId source = fixture.SeedPublic(1, [1])[0];
        HeadEntryId child = fixture.WriteMirror(2, source, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        Assert.Equal(source, store.ReadHead(source.HeadId).Unwrap().Id);
        Assert.Equal(new[] { child, source }, HistoryFixture.Collect(store, child.HeadId).Select(entry => entry.Id));
        fixture.AssertReleased(2);
        // A borrowed owned source must not be closed at the inheritance transition or final cleanup.
        HeadEntryId next = store.PublishHead(source.HeadId, [2], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(next, store.ReadHead(source.HeadId).Unwrap().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceAnchorTailFenceFailureIsRequiredAtFactoryOrHistoryBoundary(bool hasInterveningSuffix) {
        using var fixture = new HistoryFixture();
        byte[][] payloads = hasInterveningSuffix
            ? [HistoryFixture.Bytes(), HistoryFixture.Bytes(value: 0x52), HistoryFixture.Bytes(value: 0x53)]
            : [HistoryFixture.Bytes(), HistoryFixture.Bytes(value: 0x52)];
        HeadEntryId[] source = fixture.SeedPublic(1, payloads);
        HeadEntryId child = fixture.WriteMirror(2, source[0], new HistoryRecord(HistoryFixture.Bytes()))[0];
        fixture.FlipFollowingFence(source[0]);
        using var store = fixture.Open(readOnly: true);
        var delivered = new List<HeadEntryId>();
        Func<HeadEntry, bool> visitor = entry => {
            delivered.Add(entry.Id);
            return true;
        };
        if (hasInterveningSuffix) {
            // One intervening frame leaves the current EOF head's left Fence healthy. Factory
            // admission succeeds; the history anchor helper must still reject the older bad Fence.
            using (IRbfFile sourceReader = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(1), RbfCacheMode.Off)) {
                using RbfPooledFrame head = sourceReader.ReadPooledFrame(source[^1].Ticket).Unwrap();
                Assert.Equal(source[^1].Ticket, head.Ticket);
            }
            var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, visitor);
            Assert.True(result.IsFailure);
            Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        }
        else {
            // Adjacent frames share this Fence: it is also the EOF head's left Fence. The source
            // factory must reject it before history boundary validation, preserving the exception.
            Assert.Throws<InvalidDataException>(() => {
                store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, visitor);
            });
        }
        Assert.Equal(new[] { child }, delivered);
    }

    [Fact]
    public void DeclaredSourceTicketPastCompletedTailFailsWithoutReadingOrDeliveringAnUnrelatedSourcePoint() {
        using var fixture = new HistoryFixture();
        HeadEntryId source = fixture.SeedPublic(1, [1])[0];
        HeadEntryId outside = HeadEntryId.Create(source.HeadId, SizedPtr.Create(1_000_000, source.Ticket.Length));
        HeadEntryId child = fixture.WriteMirror(2, outside, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        fixture.AssertReleased(1, 2);
    }
}
