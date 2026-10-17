using Atelia.Rbf;
using Xunit;

namespace Atelia.HeadStore.Tests.Forks;

public sealed class ForkSourceValidationTests {
    [Theory]
    [InlineData(0u, 8)]
    [InlineData(2u, 8)]
    [InlineData(1u, 7)]
    public void PublicOldEntryIdExcludesControlledInvalidSourceSuffixEnvelope(uint tag, int metadataLength) {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, new byte[] { 1 }, new byte[] { 2 });
        // Only the excluded suffix is a controlled writer record; source[0] was issued by public
        // CreateHead. The completed suffix has valid RBF framing/CRC, with an invalid VS envelope.
        using (IRbfFile file = RbfFile.OpenExisting(fixture.HeadPath(7), out _, RbfCacheMode.Off)) {
            var record = new ForkRecord([3], Tag: tag, MetadataLength: metadataLength);
            file.Append(record.Tag, record.Payload, record.Metadata).Unwrap();
            file.DurableFlush();
        }
        using var store = fixture.Open();
        HeadId child = store.ForkHead(19, source[0], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 1 }, store.ReadHead(child).Unwrap().Payload.ToArray());
        Assert.Equal("HeadStore.InvalidHeadRecord", store.ReadHead(source[0].HeadId).Error!.ErrorCode);
        Assert.Equal(new[] { source[0] }, ForkFixture.Collect(store, child).Select(entry => entry.Id));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HealthyExactOldPointCanForkDespiteUnnecessaryBadInitialOrHeadContent(bool badInitial, bool badHead) {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7,
            ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1),
            ForkFixture.Bytes(value: 0xa2), ForkFixture.Bytes(value: 0xa3));
        if (badInitial) { fixture.FlipPayload(source[0]); }
        if (badHead) { fixture.FlipPayload(source[3]); }
        // Content CRC is deliberately bad, while the same writable source factory used by ForkHead
        // still qualifies the current EOF structure. No external change occurs while VS is open.
        using (IRbfFile checkedSource = RbfFile.OpenExisting(fixture.HeadPath(7), out var recovery, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
            using RbfPooledFrame exact = checkedSource.ReadPooledFrame(source[1].Ticket).Unwrap();
            Assert.Equal(source[1].Ticket, exact.Ticket);
        }
        using var store = fixture.Open();
        HeadId child = store.ForkHead(source[1], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(ForkFixture.Bytes(value: 0xa1), store.ReadHead(child).Unwrap().Payload.ToArray());
        if (badHead) { Assert.True(store.ReadHead(source[0].HeadId).IsFailure); }
        else { Assert.Equal(source[3], store.ReadHead(source[0].HeadId).Unwrap().Id); }
        if (badInitial) {
            var delivered = new List<HeadEntryId>();
            var history = store.ReadHeadHistory(child, ForkFixture.Unlimited, entry => {
                delivered.Add(entry.Id);
                return true;
            });
            Assert.True(history.IsFailure);
            Assert.StartsWith("Rbf.", history.Error!.ErrorCode);
            Assert.Equal(new[] { source[1] }, delivered);
        }
        else {
            Assert.Equal(new[] { source[1], source[0] }, ForkFixture.Collect(store, child).Select(entry => entry.Id));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NecessaryExactEntryPayloadCrcRejectsBeforeChildOutputAndLeavesHealthyOwnerUsable(int sourceIndex) {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7,
            ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1), ForkFixture.Bytes(value: 0xa2));
        fixture.FlipPayload(source[sourceIndex]);
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkHead(source[sourceIndex], out outcome);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 257);
        Assert.Equal(source[2], store.ReadHead(source[0].HeadId).Unwrap().Id);
        HeadId healthyChild = store.ForkHead(19, source[2], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(ForkFixture.Bytes(value: 0xa2), store.ReadHead(healthyChild).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void NecessarySourceHeaderCrcRejectsEvenWhenExactEntryBytesAreComplete() {
        using var fixture = new ForkFixture();
        HeadEntryId source = fixture.SeedClosed(7, ForkFixture.Bytes())[0];
        fixture.FlipHeaderPayload(7);
        using (IRbfFile sourceFile = RbfFile.OpenExisting(fixture.HeadPath(7), out _, RbfCacheMode.Off)) {
            using RbfPooledFrame exact = sourceFile.ReadPooledFrame(source.Ticket).Unwrap();
        }
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkHead(19, source, out outcome);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 19);
    }

    [Fact]
    public void ExactAnchorTailFenceIsCheckedEvenWhenRandomFrameCrcAndCurrentEofFactoryAreHealthy() {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7,
            ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1), ForkFixture.Bytes(value: 0xa2));
        fixture.FlipFollowingFence(source[0]);
        // The intervening a1 leaves a2's left Fence healthy. This proves that failure below is
        // the exact anchor boundary check, rather than the source factory's current EOF check.
        using (IRbfFile file = RbfFile.OpenExisting(fixture.HeadPath(7), out var recovery, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
            using RbfPooledFrame exact = file.ReadPooledFrame(source[0].Ticket).Unwrap();
            using RbfPooledFrame head = file.ReadPooledFrame(source[2].Ticket).Unwrap();
            Assert.Equal(source[0].Ticket, exact.Ticket);
            Assert.Equal(source[2].Ticket, head.Ticket);
            Assert.True(file.GetScanBoundaryAfter(source[0].Ticket).IsFailure);
        }
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkHead(source[0], out outcome);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 257);
        Assert.Equal(source[2], store.ReadHead(source[2].HeadId).Unwrap().Id);
    }

    [Fact]
    public void ExactOldPointDoesNotBypassWritableSourceFactoryCurrentEofQualification() {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, ForkFixture.Bytes(), ForkFixture.Bytes(value: 0x52));
        fixture.FlipFollowingFence(source[0]);
        // Adjacent frames share this Fence with the EOF head's left boundary. Writable factory
        // admission itself must reject; this test does not count as an anchor-helper rejection.
        Assert.Throws<InvalidDataException>(() => {
            using IRbfFile unexpected = RbfFile.OpenExisting(fixture.HeadPath(7), out _, RbfCacheMode.Off);
        });
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidDataException>(() => { store.ForkHead(source[0], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.HeadPath(257)));
        Assert.False(File.Exists(fixture.PendingPath));
        Assert.Throws<InvalidOperationException>(() => store.ListHeads());
        fixture.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(0u, 8, 128)]
    [InlineData(2u, 8, 128)]
    [InlineData(1u, 0, 128)]
    [InlineData(1u, 7, 128)]
    [InlineData(1u, 9, 128)]
    [InlineData(1u, 8, 0)]
    [InlineData(1u, 8, 1048577)]
    public void ControlledNecessaryEnvelopeOrPayloadSizeRejectsWithoutTreatingRawCoordinatesAsPublicQualification(
        uint tag, int metadataLength, int payloadLength
    ) {
        using var fixture = new ForkFixture();
        using (var initialized = fixture.Create()) { }
        // These coordinates deliberately come from an internal test mirror, not a public source
        // entryId. They verify defensive envelope/size checks only; normal source membership is
        // established separately by the public lifecycle tests.
        HeadEntryId[] source = fixture.WriteControlledHead(7,
            new ForkRecord([1]), new ForkRecord(ForkFixture.Bytes(payloadLength), Tag: tag, MetadataLength: metadataLength), new ForkRecord([3]));
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkHead(19, source[1], out outcome);
        Assert.Equal("HeadStore.InvalidHeadRecord", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 19);
        Assert.Equal(source[2], store.ReadHead(source[0].HeadId).Unwrap().Id);
    }

    [Fact]
    public void ControlledSourceTimeAndIdentityArePreservedWhileSourceFileBytesStayUnchanged() {
        using var fixture = new ForkFixture();
        using (var initialized = fixture.Create()) { }
        // Extreme legal metadata makes copying the source timestamp detectable without sleeping
        // or requiring real clock samples to differ. This is a controlled source mirror.
        HeadEntryId source = fixture.WriteControlledHead(7, new ForkRecord("{ \"x\":1 }"u8.ToArray(), long.MinValue))[0];
        byte[] sourceFileBefore = File.ReadAllBytes(fixture.HeadPath(7));
        HeadEntry childEntry;
        using (var store = fixture.Open()) {
            var checkedSource = store.ReadHead(source.HeadId).Unwrap();
            Assert.Equal(long.MinValue, checkedSource.RecordedAtUnixMs);
            HeadId child = store.ForkHead(checkedSource.Id, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            childEntry = store.ReadHead(child).Unwrap();
            Assert.Equal(checkedSource.Id, childEntry.Id);
            Assert.Equal(checkedSource.RecordedAtUnixMs, childEntry.RecordedAtUnixMs);
            Assert.Equal(checkedSource.Payload.ToArray(), childEntry.Payload.ToArray());
            var unchanged = store.ReadHead(source.HeadId).Unwrap();
            Assert.Equal(source, unchanged.Id);
            Assert.Equal(long.MinValue, unchanged.RecordedAtUnixMs);
            var history = ForkFixture.Collect(store, child);
            Assert.Equal(new[] { source }, history.Select(entry => entry.Id));
            Assert.Equal(long.MinValue, history[0].RecordedAtUnixMs);
        }
        Assert.Equal(sourceFileBefore, File.ReadAllBytes(fixture.HeadPath(7)));
        Assert.Equal("{ \"x\":1 }"u8.ToArray(), childEntry.Payload.ToArray());
    }

    [Fact]
    public void ControlledMaximumOfficialIdExhaustsAutomaticForkButFreeReservedSlotStillWorks() {
        using var fixture = new ForkFixture();
        using (var initialized = fixture.Create()) { }
        // A controlled official path tests cold H reconstruction without billions of creations.
        // ReadHead, rather than the mirror's internal coordinate, supplies the source entryId.
        fixture.WriteControlledHead(uint.MaxValue, new ForkRecord([0xff]));
        using var store = fixture.Open();
        HeadEntryId source = store.ReadHead(uint.MaxValue).Unwrap().Id;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Equal("HeadStore.HeadIdExhausted", store.ForkHead(source, out outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.PendingPath));
        HeadId child = store.ForkHead(19, source, out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(19u, child.LocalId);
        Assert.Equal(new[] { source }, ForkFixture.Collect(store, child).Select(entry => entry.Id));
    }

    [Fact]
    public void ControlledTruncationRejectsPreviouslyIssuedEntryIdOutsideCompletedRangeAndHealthyPointStillForks() {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1));
        long completedEnd;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(7), RbfCacheMode.Off)) {
            completedEnd = file.GetPhysicalOffsetImmediatelyAfter(source[0].Ticket);
        }
        // Controlled corruption mirror only: the normal protocol forbids deleting published
        // positions. Both saved entryIds were issued publicly before their owner was closed.
        using (var stream = new FileStream(fixture.HeadPath(7), FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(completedEnd);
        }
        using (IRbfFile file = RbfFile.OpenExisting(fixture.HeadPath(7), out var recovery, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
            Assert.Equal(completedEnd, file.TailOffset);
        }
        using var store = fixture.Open();
        Assert.Equal(source[0], store.ReadHead(source[0].HeadId).Unwrap().Id);
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkHead(source[1], out outcome);
        Assert.Equal("HeadStore.InvalidHeadRecord", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 257);
        HeadId child = store.ForkHead(source[0], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(257u, child.LocalId);
        Assert.Equal(ForkFixture.Bytes(value: 0xa0), store.ReadHead(child).Unwrap().Payload.ToArray());
        Assert.Equal(new[] { source[0] }, ForkFixture.Collect(store, child).Select(entry => entry.Id));
    }
}
