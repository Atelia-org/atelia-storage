using Atelia.Rbf;
using Xunit;

namespace Atelia.VersionStore.Tests.Forks;

public sealed class ForkSourceValidationTests {
    [Theory]
    [InlineData(0u, 8)]
    [InlineData(2u, 8)]
    [InlineData(1u, 7)]
    public void PublicOldRevisionExcludesControlledInvalidSourceSuffixEnvelope(uint tag, int metadataLength) {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, new byte[] { 1 }, new byte[] { 2 });
        // Only the excluded suffix is a controlled writer record; source[0] was issued by public
        // CreateRef. The completed suffix has valid RBF framing/CRC, with an invalid VS envelope.
        using (IRbfFile file = RbfFile.OpenExisting(fixture.RefPath(7), out _, RbfCacheMode.Off)) {
            var record = new ForkRecord([3], Tag: tag, MetadataLength: metadataLength);
            file.Append(record.Tag, record.Payload, record.Metadata).Unwrap();
            file.DurableFlush();
        }
        using var store = fixture.Open();
        RefRevision child = store.ForkRef(19, source[0], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 1 }, store.ReadRef(child.RefId).Unwrap().Payload.ToArray());
        Assert.Equal("VersionStore.InvalidRefRecord", store.ReadRef(source[0].RefId).Error!.ErrorCode);
        Assert.Equal(new[] { child, source[0] }, ForkFixture.Collect(store, child.RefId).Select(snapshot => snapshot.Revision));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HealthyExactOldPointCanForkDespiteUnnecessaryBadInitialOrHeadContent(bool badInitial, bool badHead) {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7,
            ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1),
            ForkFixture.Bytes(value: 0xa2), ForkFixture.Bytes(value: 0xa3));
        if (badInitial) { fixture.FlipPayload(source[0]); }
        if (badHead) { fixture.FlipPayload(source[3]); }
        // Content CRC is deliberately bad, while the same writable source factory used by ForkRef
        // still qualifies the current EOF structure. No external change occurs while VS is open.
        using (IRbfFile checkedSource = RbfFile.OpenExisting(fixture.RefPath(7), out var recovery, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
            using RbfPooledFrame exact = checkedSource.ReadPooledFrame(source[1].Ticket).Unwrap();
            Assert.Equal(source[1].Ticket, exact.Ticket);
        }
        using var store = fixture.Open();
        RefRevision child = store.ForkRef(source[1], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(ForkFixture.Bytes(value: 0xa1), store.ReadRef(child.RefId).Unwrap().Payload.ToArray());
        if (badHead) { Assert.True(store.ReadRef(source[0].RefId).IsFailure); }
        else { Assert.Equal(source[3], store.ReadRef(source[0].RefId).Unwrap().Revision); }
        if (badInitial) {
            var delivered = new List<RefRevision>();
            var history = store.ReadRefHistory(child.RefId, ForkFixture.Unlimited, snapshot => {
                delivered.Add(snapshot.Revision);
                return true;
            });
            Assert.True(history.IsFailure);
            Assert.StartsWith("Rbf.", history.Error!.ErrorCode);
            Assert.Equal(new[] { child, source[1] }, delivered);
        }
        else {
            Assert.Equal(new[] { child, source[1], source[0] }, ForkFixture.Collect(store, child.RefId).Select(snapshot => snapshot.Revision));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NecessaryExactSnapshotPayloadCrcRejectsBeforeChildOutputAndLeavesHealthyOwnerUsable(int sourceIndex) {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7,
            ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1), ForkFixture.Bytes(value: 0xa2));
        fixture.FlipPayload(source[sourceIndex]);
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkRef(source[sourceIndex], out outcome);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 257);
        Assert.Equal(source[2], store.ReadRef(source[0].RefId).Unwrap().Revision);
        RefRevision healthyChild = store.ForkRef(19, source[2], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(ForkFixture.Bytes(value: 0xa2), store.ReadRef(healthyChild.RefId).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void NecessarySourceHeaderCrcRejectsEvenWhenExactSnapshotBytesAreComplete() {
        using var fixture = new ForkFixture();
        RefRevision source = fixture.SeedClosed(7, ForkFixture.Bytes())[0];
        fixture.FlipHeaderPayload(7);
        using (IRbfFile sourceFile = RbfFile.OpenExisting(fixture.RefPath(7), out _, RbfCacheMode.Off)) {
            using RbfPooledFrame exact = sourceFile.ReadPooledFrame(source.Ticket).Unwrap();
        }
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkRef(19, source, out outcome);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 19);
    }

    [Fact]
    public void ExactAnchorTailFenceIsCheckedEvenWhenRandomFrameCrcAndCurrentEofFactoryAreHealthy() {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7,
            ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1), ForkFixture.Bytes(value: 0xa2));
        fixture.FlipFollowingFence(source[0]);
        // The intervening a1 leaves a2's left Fence healthy. This proves that failure below is
        // the exact anchor boundary check, rather than the source factory's current EOF check.
        using (IRbfFile file = RbfFile.OpenExisting(fixture.RefPath(7), out var recovery, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
            using RbfPooledFrame exact = file.ReadPooledFrame(source[0].Ticket).Unwrap();
            using RbfPooledFrame head = file.ReadPooledFrame(source[2].Ticket).Unwrap();
            Assert.Equal(source[0].Ticket, exact.Ticket);
            Assert.Equal(source[2].Ticket, head.Ticket);
            Assert.True(file.GetScanBoundaryAfter(source[0].Ticket).IsFailure);
        }
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkRef(source[0], out outcome);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 257);
        Assert.Equal(source[2], store.ReadRef(source[2].RefId).Unwrap().Revision);
    }

    [Fact]
    public void ExactOldPointDoesNotBypassWritableSourceFactoryCurrentEofQualification() {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, ForkFixture.Bytes(), ForkFixture.Bytes(value: 0x52));
        fixture.FlipFollowingFence(source[0]);
        // Adjacent frames share this Fence with the EOF head's left boundary. Writable factory
        // admission itself must reject; this test does not count as an anchor-helper rejection.
        Assert.Throws<InvalidDataException>(() => {
            using IRbfFile unexpected = RbfFile.OpenExisting(fixture.RefPath(7), out _, RbfCacheMode.Off);
        });
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidDataException>(() => { store.ForkRef(source[0], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.RefPath(257)));
        Assert.False(File.Exists(fixture.PendingPath));
        Assert.Throws<InvalidOperationException>(() => store.ListRefs());
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
        // revision. They verify defensive envelope/size checks only; normal source membership is
        // established separately by the public lifecycle tests.
        RefRevision[] source = fixture.WriteControlledRef(7,
            new ForkRecord([1]), new ForkRecord(ForkFixture.Bytes(payloadLength), Tag: tag, MetadataLength: metadataLength), new ForkRecord([3]));
        using var store = fixture.Open();
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkRef(19, source[1], out outcome);
        Assert.Equal("VersionStore.InvalidRefRecord", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 19);
        Assert.Equal(source[2], store.ReadRef(source[0].RefId).Unwrap().Revision);
    }

    [Fact]
    public void ControlledSourceTimeIsPreservedAndChildSamplesNewTimeWhileSourceFileBytesStayUnchanged() {
        using var fixture = new ForkFixture();
        using (var initialized = fixture.Create()) { }
        // Extreme legal metadata makes copying the source timestamp detectable without sleeping
        // or requiring real clock samples to differ. This is a controlled source mirror.
        RefRevision source = fixture.WriteControlledRef(7, new ForkRecord("{ \"x\":1 }"u8.ToArray(), long.MinValue))[0];
        byte[] sourceFileBefore = File.ReadAllBytes(fixture.RefPath(7));
        RefBytesSnapshot childSnapshot;
        using (var store = fixture.Open()) {
            var checkedSource = store.ReadRef(source.RefId).Unwrap();
            Assert.Equal(long.MinValue, checkedSource.RecordedAtUnixMs);
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RefRevision child = store.ForkRef(checkedSource.Revision, out var outcome).Unwrap();
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            childSnapshot = store.ReadRef(child.RefId).Unwrap();
            Assert.InRange(childSnapshot.RecordedAtUnixMs, before, after);
            Assert.NotEqual(checkedSource.RecordedAtUnixMs, childSnapshot.RecordedAtUnixMs);
            Assert.Equal(checkedSource.Payload.ToArray(), childSnapshot.Payload.ToArray());
            var unchanged = store.ReadRef(source.RefId).Unwrap();
            Assert.Equal(source, unchanged.Revision);
            Assert.Equal(long.MinValue, unchanged.RecordedAtUnixMs);
            var history = ForkFixture.Collect(store, child.RefId);
            Assert.Equal(new[] { child, source }, history.Select(snapshot => snapshot.Revision));
            Assert.Equal(long.MinValue, history[1].RecordedAtUnixMs);
        }
        Assert.Equal(sourceFileBefore, File.ReadAllBytes(fixture.RefPath(7)));
        Assert.Equal("{ \"x\":1 }"u8.ToArray(), childSnapshot.Payload.ToArray());
    }

    [Fact]
    public void ControlledMaximumOfficialIdExhaustsAutomaticForkButFreeReservedSlotStillWorks() {
        using var fixture = new ForkFixture();
        using (var initialized = fixture.Create()) { }
        // A controlled official path tests cold H reconstruction without billions of creations.
        // ReadRef, rather than the mirror's internal coordinate, supplies the source revision.
        fixture.WriteControlledRef(uint.MaxValue, new ForkRecord([0xff]));
        using var store = fixture.Open();
        RefRevision source = store.ReadRef(uint.MaxValue).Unwrap().Revision;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Equal("VersionStore.RefIdExhausted", store.ForkRef(source, out outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.PendingPath));
        RefRevision child = store.ForkRef(19, source, out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(19u, child.RefId.LocalId);
        Assert.Equal(new[] { child, source }, ForkFixture.Collect(store, child.RefId).Select(snapshot => snapshot.Revision));
    }

    [Fact]
    public void ControlledTruncationRejectsPreviouslyIssuedRevisionOutsideCompletedRangeAndHealthyPointStillForks() {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1));
        long completedEnd;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(7), RbfCacheMode.Off)) {
            completedEnd = file.GetPhysicalOffsetImmediatelyAfter(source[0].Ticket);
        }
        // Controlled corruption mirror only: the normal protocol forbids deleting published
        // positions. Both saved revisions were issued publicly before their owner was closed.
        using (var stream = new FileStream(fixture.RefPath(7), FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(completedEnd);
        }
        using (IRbfFile file = RbfFile.OpenExisting(fixture.RefPath(7), out var recovery, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
            Assert.Equal(completedEnd, file.TailOffset);
        }
        using var store = fixture.Open();
        Assert.Equal(source[0], store.ReadRef(source[0].RefId).Unwrap().Revision);
        var outcome = PublicationOutcome.Confirmed;
        var result = store.ForkRef(source[1], out outcome);
        Assert.Equal("VersionStore.InvalidRefRecord", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.AssertNoChild(store, 257);
        RefRevision child = store.ForkRef(source[0], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(257u, child.RefId.LocalId);
        Assert.Equal(ForkFixture.Bytes(value: 0xa0), store.ReadRef(child.RefId).Unwrap().Payload.ToArray());
        Assert.Equal(new[] { child, source[0] }, ForkFixture.Collect(store, child.RefId).Select(snapshot => snapshot.Revision));
    }
}
