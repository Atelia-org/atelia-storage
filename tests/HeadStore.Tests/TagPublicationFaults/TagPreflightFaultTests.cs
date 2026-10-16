using Atelia.Data;
using Atelia.HeadStore.Tests.TagFaults;
using Xunit;

namespace Atelia.HeadStore.Tests.TagPublicationFaults;

public sealed class TagPreflightFaultTests {
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("overlong")]
    public void InvalidNameRejectsBeforeAnyTagPathClockOrBarrier(string? name) {
        using var f = new TagPublicationFaultFixture();
        if (name == "overlong") { name = new string('a', HeadStore.MaxTagNameCodeUnits + 1); }
        var outcome = PublicationOutcome.Confirmed;

        Assert.True(f.Store.CreateTag(name!, [1], out outcome).IsFailure);
        Assert.True(f.Store.ResolveTag(name!).IsFailure);

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Events);
        AssertNoPublication(f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048577)]
    public void InvalidPayloadRejectsBeforePathClockAndBarrier(int length) {
        using var f = new TagPublicationFaultFixture();
        var outcome = PublicationOutcome.Confirmed;

        Assert.True(f.Store.CreateTag(TagPublicationFaultFixture.Name, new byte[length], out outcome).IsFailure);

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Events);
        AssertNoPublication(f);
    }

    [Fact]
    public void ReadOnlyMutationRejectsBeforeTagDiscoveryOrClock() {
        using var f = new TagPublicationFaultFixture(readOnly: true);
        var outcome = PublicationOutcome.Confirmed;

        Assert.Equal("HeadStore.ReadOnly", f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out outcome).Error!.ErrorCode);

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Events);
        AssertNoPublication(f);
    }

    [Fact]
    public void DisposedOwnerInitializesOutcomeBeforeThrowingWithoutTagDiscovery() {
        using var f = new TagPublicationFaultFixture();
        f.Store.Dispose();
        f.Events.Clear();
        var outcome = PublicationOutcome.Confirmed;

        Assert.Throws<ObjectDisposedException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out outcome));
        Assert.Throws<ObjectDisposedException>(() => f.Store.ResolveTag(TagPublicationFaultFixture.Name));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Events);
        AssertNoPublication(f);
    }

    [Fact]
    public void ActiveHistoryRejectsMutationWithoutTagDiscoveryAndHistoryCanFinish() {
        using var f = new TagPublicationFaultFixture();
        HeadId id = f.SeedHead();
        var outcome = PublicationOutcome.Confirmed;
        int callbacks = 0;

        var history = f.Store.ReadHeadHistory(id, new HeadHistoryBudget(100, 100), _ => {
            callbacks++;
            Assert.Equal("HeadStore.HistoryActive",
                f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out outcome).Error!.ErrorCode);
            return true;
        });

        Assert.Equal(HeadHistoryEnd.Complete, history.Unwrap());
        Assert.Equal(1, callbacks);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Tags.ResolveCalls);
        Assert.DoesNotContain("clock", f.Events);
        AssertNoPublication(f);
    }

    [Theory]
    [InlineData("occupied")]
    [InlineData("uncertain")]
    [InlineData("capacity")]
    public void DefiniteBucketRejectionClosesTheSameReaderBeforeAnyBarrierOrOutput(string reason) {
        using var f = new TagPublicationFaultFixture();
        TagBucketSeed seed;
        if (reason == "occupied") {
            seed = f.SeedBucket(TagPublicationFaultFixture.Record(), TagPublicationFaultFixture.Record(TagPublicationFaultFixture.OtherName));
            f.CorruptRoot(seed.Tickets[0]); // Name occupancy does not require healthy root CRC.
        }
        else if (reason == "uncertain") {
            byte[] meta = TagFaultFixture.Metadata(TagPublicationFaultFixture.OtherName);
            meta[^1] ^= 1;
            seed = f.SeedBucket(new TagFaultRecord(TagFaultFixture.Payload(), meta));
        }
        else {
            seed = f.SeedBucket();
            f.Tags.ConfigureOpen = file => file.TailOffsetOverride = SizedPtr.MaxOffset + 4;
        }
        var outcome = PublicationOutcome.Confirmed;

        var result = f.Store.CreateTag(TagPublicationFaultFixture.Name, [3], out outcome);

        Assert.True(result.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        AssertNoPublication(f);
        Assert.Equal(1, f.Tags.OpenCalls);
        TagPublicationFile file = Assert.Single(f.Tags.Opened);
        Assert.Equal(1, file.DisposeCalls);
        Assert.All(seed.Tickets, ticket => Assert.DoesNotContain(file.LoggedReads(), read => read.Offset == ticket.Offset && read.Length == ticket.Length));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.ListHeads().IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationClockExceptionDoesNotStartMutationOrFaultTheOwner(bool existingBucket) {
        using var f = new TagPublicationFaultFixture();
        if (existingBucket) { f.SeedBucket(); }
        var failure = new OutOfMemoryException("prepared metadata allocation window");
        f.Hooks.GetRecordedAtUnixMs = () => throw failure;
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        AssertNoPublication(f);
        Assert.All(f.Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Hooks.GetRecordedAtUnixMs = null;
        Assert.True(f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out _).Unwrap());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PureTargetPreparationExceptionDoesNotStartMutation(bool preparingNewTarget) {
        using var f = new TagPublicationFaultFixture();
        var failure = new IOException("tag target qualification");
        if (preparingNewTarget) { f.Tags.PrepareFailure = failure; }
        else { f.Tags.ResolveFailure = failure; }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        AssertNoPublication(f);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Tags.PrepareFailure = null;
        f.Tags.ResolveFailure = null;
        Assert.True(f.Store.CreateTag(TagPublicationFaultFixture.Name, [1], out outcome).Unwrap());
    }

    internal static void AssertNoPublication(TagPublicationFaultFixture f) {
        Assert.DoesNotContain("data:confirm", f.Events);
        Assert.Equal(0, f.Tags.EnsureCalls);
        Assert.Equal(0, f.Heads.PendingCreateCalls);
        Assert.Equal(0, f.Tags.RenameCalls);
        Assert.All(f.Tags.Opened, file => { Assert.Equal(0, file.AppendCalls); Assert.Equal(0, file.FlushCalls); });
        Assert.False(File.Exists(f.PendingPath));
    }
}
