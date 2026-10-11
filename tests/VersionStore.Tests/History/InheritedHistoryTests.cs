using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.VersionStore.Tests.History;

public sealed class InheritedHistoryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedChainIncludesEachCloneAndExactSourcePrefixAndExcludesBadSourceSuffix(bool readOnly) {
        using var fixture = new HistoryFixture();
        RefRevision[] a = fixture.SeedPublic(1,
            HistoryFixture.Bytes(value: 0xa0), HistoryFixture.Bytes(value: 0xa1),
            HistoryFixture.Bytes(value: 0xa2), HistoryFixture.Bytes(value: 0xa3));
        RefRevision[] b = fixture.WriteMirror(2, a[2],
            new HistoryRecord(HistoryFixture.Bytes(value: 0xa2), -20), new HistoryRecord(HistoryFixture.Bytes(value: 0xb1), -30));
        RefRevision[] c = fixture.WriteMirror(3, b[1],
            new HistoryRecord(HistoryFixture.Bytes(value: 0xb1), 40), new HistoryRecord(HistoryFixture.Bytes(value: 0xc1), 10));
        fixture.FlipPayload(a[3]);
        using var store = fixture.Open(readOnly);
        // C: enter/header 3 + 2 records * 2 = 7.
        // B/A: enter/header 3 + two anchor-helper complete reads 2 + records * 2 = 9/11.
        var snapshots = HistoryFixture.Collect(store, fixture.Id(3), new(7, 27));
        Assert.Equal(new[] { c[1], c[0], b[1], b[0], a[2], a[1], a[0] }, snapshots.Select(snapshot => snapshot.Revision));
        Assert.Equal(snapshots[1].Payload.ToArray(), snapshots[2].Payload.ToArray());
        Assert.NotEqual(snapshots[1].Revision, snapshots[2].Revision);
        Assert.Equal(snapshots[3].Payload.ToArray(), snapshots[4].Payload.ToArray());
        Assert.NotEqual(snapshots[3].Revision, snapshots[4].Revision);
        Assert.Equal(new long[] { 10, 40, -30, -20 }, snapshots.Take(4).Select(snapshot => snapshot.RecordedAtUnixMs));
        if (!readOnly) { fixture.AssertReleased(1, 2, 3); }
        // The excluded A3 is still bad: history success cannot certify the source's current head.
        Assert.StartsWith("Rbf.", store.ReadRef(fixture.Id(1)).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(0u, 8)]
    [InlineData(2u, 8)]
    [InlineData(1u, 7)]
    public void ExcludedSourceSuffixEnvelopeIsNotTraversed(uint tag, int metadataLength) {
        using var fixture = new HistoryFixture();
        RefRevision[] source = fixture.WriteMirror(1, null,
            new HistoryRecord([0x11], 1), new HistoryRecord([0x12], 2), new HistoryRecord([0x13], 3, tag, metadataLength));
        RefRevision child = fixture.WriteMirror(2, source[1], new HistoryRecord([0x12], 4))[0];
        using var store = fixture.Open();
        var snapshots = HistoryFixture.Collect(store, fixture.Id(2));
        Assert.Equal(new[] { child, source[1], source[0] }, snapshots.Select(snapshot => snapshot.Revision));
        Assert.Equal("VersionStore.InvalidRefRecord", store.ReadRef(fixture.Id(1)).Error!.ErrorCode);
    }

    [Fact]
    public void StopAtChildInitialDoesNotResolveItsMissingSourceAndFullTraversalFails() {
        using var fixture = new HistoryFixture();
        var source = fixture.DeclaredInitial(19, 1);
        RefRevision child = fixture.WriteMirror(2, source, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        Assert.Equal(HistoryEnd.VisitorStopped, store.ReadRefHistory(child.RefId, new(1, 5), _ => false).Unwrap());
        int calls = 0;
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, _ => { calls++; return true; });
        Assert.Equal("VersionStore.RefNotFound", result.Error!.ErrorCode);
        Assert.Equal(1, calls);
        fixture.AssertReleased(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnequalCloneBytesFailBeforeSourceIsDeliveredEvenWhenApplicationValuesCouldMatch(bool sameLength) {
        using var fixture = new HistoryFixture();
        byte[] sourceBytes = "{\"x\":1}"u8.ToArray();
        byte[] cloneBytes = sameLength ? "{\"x\":2}"u8.ToArray() : "{ \"x\":1 }"u8.ToArray();
        RefRevision source = fixture.SeedPublic(1, sourceBytes)[0];
        RefRevision child = fixture.WriteMirror(2, source, new HistoryRecord(cloneBytes))[0];
        using var store = fixture.Open();
        Assert.Equal(HistoryEnd.VisitorStopped, store.ReadRefHistory(child.RefId, new(1, 5), _ => false).Unwrap());
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        fixture.AssertReleased(1, 2);
        Assert.Equal(source, store.ReadRef(source.RefId).Unwrap().Revision);
    }

    [Fact]
    public void RequiredBadSourceAnchorFailsWithoutDeliveringItAndRetainsChildPrefix() {
        using var fixture = new HistoryFixture();
        byte[] payload = HistoryFixture.Bytes();
        RefRevision source = fixture.SeedPublic(1, payload)[0];
        RefRevision child = fixture.WriteMirror(2, source, new HistoryRecord(payload))[0];
        fixture.FlipPayload(source);
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void RequiredBadOlderSourceNodeFailsInsteadOfCompletingOrSkippingIt() {
        using var fixture = new HistoryFixture();
        RefRevision[] source = fixture.SeedPublic(1, HistoryFixture.Bytes(value: 0x31), HistoryFixture.Bytes(value: 0x32));
        RefRevision child = fixture.WriteMirror(2, source[1], new HistoryRecord(HistoryFixture.Bytes(value: 0x32)))[0];
        fixture.FlipPayload(source[0]);
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
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
        RefRevision declaredA = fixture.DeclaredInitial(1, 1);
        RefRevision declaredB = fixture.DeclaredInitial(2, 1);
        RefRevision a = fixture.WriteMirror(1, declaredB, new HistoryRecord([1]))[0];
        RefRevision b = fixture.WriteMirror(2, declaredA, new HistoryRecord([1]))[0];
        Assert.Equal(declaredA, a);
        Assert.Equal(declaredB, b);
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(a.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { a, b }, delivered);
        fixture.AssertReleased(1, 2);
        Assert.Equal(HistoryEnd.VisitorStopped, store.ReadRefHistory(a.RefId, HistoryFixture.Unlimited, _ => false).Unwrap());
    }

    [Fact]
    public void CallbackSourceQueriesReuseTheTemporaryAnchorFileAndDoNotReplaceItsHistoryUpperBound() {
        using var fixture = new HistoryFixture();
        RefRevision[] source = fixture.SeedPublic(1, [1], [2], [3]);
        RefRevision child = fixture.WriteMirror(2, source[1], new HistoryRecord([2]))[0];
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            if (snapshot.Revision.RefId == source[0].RefId) {
                Assert.Equal(source[2], store.ReadRef(source[0].RefId).Unwrap().Revision);
                Assert.Equal(source[2], store.ReadRef(1u).Unwrap().Revision);
            }
            return true;
        });
        Assert.Equal(HistoryEnd.Complete, result.Unwrap());
        Assert.Equal(new[] { child, source[1], source[0] }, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void PreviouslyOwnedSourceHandleIsBorrowedAndRemainsUsableAfterHistory() {
        using var fixture = new HistoryFixture();
        RefRevision source = fixture.SeedPublic(1, [1])[0];
        RefRevision child = fixture.WriteMirror(2, source, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        Assert.Equal(source, store.ReadRef(source.RefId).Unwrap().Revision);
        Assert.Equal(new[] { child, source }, HistoryFixture.Collect(store, child.RefId).Select(snapshot => snapshot.Revision));
        fixture.AssertReleased(2);
        // A borrowed owned source must not be closed at the inheritance transition or final cleanup.
        RefRevision next = store.PublishRef(source.RefId, [2], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(next, store.ReadRef(source.RefId).Unwrap().Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceAnchorTailFenceFailureIsRequiredAtFactoryOrHistoryBoundary(bool hasInterveningSuffix) {
        using var fixture = new HistoryFixture();
        byte[][] payloads = hasInterveningSuffix
            ? [HistoryFixture.Bytes(), HistoryFixture.Bytes(value: 0x52), HistoryFixture.Bytes(value: 0x53)]
            : [HistoryFixture.Bytes(), HistoryFixture.Bytes(value: 0x52)];
        RefRevision[] source = fixture.SeedPublic(1, payloads);
        RefRevision child = fixture.WriteMirror(2, source[0], new HistoryRecord(HistoryFixture.Bytes()))[0];
        fixture.FlipFollowingFence(source[0]);
        using var store = fixture.Open(readOnly: true);
        var delivered = new List<RefRevision>();
        Func<RefBytesSnapshot, bool> visitor = snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        };
        if (hasInterveningSuffix) {
            // One intervening frame leaves the current EOF head's left Fence healthy. Factory
            // admission succeeds; the history anchor helper must still reject the older bad Fence.
            using (IRbfFile sourceReader = RbfFile.OpenReadOnlyExisting(fixture.RefPath(1), RbfCacheMode.Off)) {
                using RbfPooledFrame head = sourceReader.ReadPooledFrame(source[^1].Ticket).Unwrap();
                Assert.Equal(source[^1].Ticket, head.Ticket);
            }
            var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, visitor);
            Assert.True(result.IsFailure);
            Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        }
        else {
            // Adjacent frames share this Fence: it is also the EOF head's left Fence. The source
            // factory must reject it before history boundary validation, preserving the exception.
            Assert.Throws<InvalidDataException>(() => {
                store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, visitor);
            });
        }
        Assert.Equal(new[] { child }, delivered);
    }

    [Fact]
    public void DeclaredSourceTicketPastCompletedTailFailsWithoutReadingOrDeliveringAnUnrelatedSourcePoint() {
        using var fixture = new HistoryFixture();
        RefRevision source = fixture.SeedPublic(1, [1])[0];
        RefRevision outside = RefRevision.Create(source.RefId, SizedPtr.Create(1_000_000, source.Ticket.Length));
        RefRevision child = fixture.WriteMirror(2, outside, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        fixture.AssertReleased(1, 2);
    }
}
