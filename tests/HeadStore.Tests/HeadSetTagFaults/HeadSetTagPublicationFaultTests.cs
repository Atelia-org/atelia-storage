using Atelia.HeadStore.Tests.ConditionalUpdateFaults;
using Xunit;

namespace Atelia.HeadStore.Tests.HeadSetTagFaults;

/// <summary>真实文件与窄 I/O 代理；异常注入不等于进程终止或断电证据。</summary>
public sealed class HeadSetTagPublicationFaultTests {
    private const string Name = "head-set-fault";

    [Fact]
    public void DataConfirmationFailureDoesNotAttemptTagOrConfirmHeads() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var failure = new IOException("data confirmation");
        f.Hooks.ConfirmDataDurable = () => throw failure;
        PublicationOutcome outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => store.CreateTag(Name, [f.Id(1)], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.All(f.Operations.Files, file => Assert.Equal(0, file.FlushCalls));
        f.AssertFaultCleanup();
        AssertColdMissing(f);
    }

    [Theory]
    [InlineData(1u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(2u, true)]
    public void DependencyFlushFailureBeforeOrAfterRealFlushLeavesTagNotAttempted(uint failingId, bool flushFirst) {
        using var f = new ConditionalUpdateFaultFixture();
        SeedCu(f);
        var failure = new IOException("necessary dependency flush");
        f.Operations.ConfigureOpen = file => {
            if (file.Id == failingId) {
                file.FlushFailure = failure;
                file.FlushThenThrow = flushFirst;
            }
        };
        var store = f.Open();
        PublicationOutcome outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => store.CreateTag(Name, [f.Id(1)], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Contains(f.Operations.Files, file => file.Id == failingId && file.FlushCalls == 1);
        f.AssertFaultCleanup();
        AssertColdMissing(f);
    }

    [Fact]
    public void SelectedCuConfirmsAllDependenciesAfterDataWithoutCachingUnselectedPeer() {
        using var f = new ConditionalUpdateFaultFixture();
        var published = SeedCu(f);
        var store = f.Open();
        f.Events.Clear();

        HeadSetTag tag = store.CreateTag(Name, [f.Id(1)], out var outcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(published[f.Id(1)], Assert.Single(tag.Entries).Value);
        Assert.Equal(new uint[] { 1 }, ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        Assert.Equal(new[] { "data:confirm", "flush:1", "flush:2" },
            f.Events.Where(value => value == "data:confirm" || value.StartsWith("flush:", StringComparison.Ordinal)));
        Assert.All(f.Operations.Files.Where(file => file.Id == 2), file => Assert.Equal(1, file.DisposeCalls));
        Assert.DoesNotContain(f.Operations.Files, file => file.Id == 3);
        store.PublishHead(f.Id(2), [0xc2], out _).Unwrap();
        Assert.Equal(published[f.Id(1)], store.ReadTag(Name).Unwrap().Entries[f.Id(1)]);
    }

    [Fact]
    public void SharedCuDependenciesAreFlushedOnceInLocalIdOrder() {
        using var f = new ConditionalUpdateFaultFixture();
        SeedCu(f);
        var store = f.Open();
        f.Events.Clear();

        var tag = store.CreateTag(Name, [f.Id(2), f.Id(1)], out _).Unwrap();

        Assert.Equal(2, tag.Entries.Count);
        Assert.Equal(new[] { "data:confirm", "flush:1", "flush:2" },
            f.Events.Where(value => value == "data:confirm" || value.StartsWith("flush:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderOnlySelectedHeadsConfirmThemselvesAndSharedSourceCuDependenciesOnce(bool siblings) {
        using var f = new ConditionalUpdateFaultFixture();
        var published = SeedCu(f);
        HeadId child;
        HeadId sibling;
        using (var writer = f.OpenPublic()) {
            child = writer.ForkHead(4, published[f.Id(1)], out _).Unwrap();
            sibling = writer.ForkHead(5, published[f.Id(1)], out _).Unwrap();
        }
        var store = f.Open();
        f.Events.Clear();

        HeadSetTag tag = store.CreateTag(Name, siblings ? [sibling, child] : [child], out var outcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.All(tag.Entries, pair => Assert.Equal(published[f.Id(1)], pair.Value));
        Assert.Equal(siblings ? 2 : 1, tag.Entries.Count);
        Assert.Equal(siblings
            ? new[] { "data:confirm", "flush:1", "flush:2", "flush:4", "flush:5" }
            : new[] { "data:confirm", "flush:1", "flush:2", "flush:4" },
            f.Events.Where(value => value == "data:confirm" || value.StartsWith("flush:", StringComparison.Ordinal)));
        Assert.DoesNotContain(f.Operations.Files, file => file.Id == 3);
        Assert.All(f.Operations.Files.Where(file => file.Id == 2), file => Assert.Equal(1, file.DisposeCalls));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void HeaderOnlySelectionCannotAppendTagAfterDataSourcePeerOrSelectedConfirmationFails(uint failingId) {
        using var f = new ConditionalUpdateFaultFixture();
        var published = SeedCu(f);
        HeadId child;
        using (var writer = f.OpenPublic()) { child = writer.ForkHead(4, published[f.Id(1)], out _).Unwrap(); }
        var failure = new IOException("reference tag dependency confirmation");
        f.Operations.ConfigureOpen = file => { if (file.Id == failingId) { file.FlushFailure = failure; } };
        var store = f.Open();
        if (failingId == 0) { f.Hooks.ConfirmDataDurable = () => throw failure; }
        PublicationOutcome outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => store.CreateTag(Name, [child], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        f.AssertFaultCleanup();
        AssertColdMissing(f);
    }

    [Fact]
    public void UnknownCuWithAllEndsCompleteIsAcceptedAfterReopenAndTagReconfirmsItsDependencies() {
        using var f = new ConditionalUpdateFaultFixture();
        var failure = new IOException("CU flush after all complete Ends");
        f.Operations.ConfigureOpen = file => {
            if (file.Id == 2) { file.FlushFailure = failure; }
        };
        var writer = f.Open();
        PublicationOutcome cuOutcome = PublicationOutcome.NotAttempted;
        Assert.Same(failure, Assert.Throws<IOException>(() =>
            writer.PublishHeads(f.Changes((1, [0xa1]), (2, [0xb1])), out cuOutcome)));
        Assert.Equal(PublicationOutcome.Unknown, cuOutcome);
        f.AssertFaultCleanup();
        f.Close();
        f.Operations.ConfigureOpen = null;
        var reopened = f.Open();
        HeadEntry a = reopened.ReadHead(f.Id(1)).Unwrap();
        Assert.Equal(new byte[] { 0xa1 }, a.Payload.ToArray());
        Assert.NotEqual(f.Initial[1], a.Id);
        f.Events.Clear();

        var tag = reopened.CreateTag(Name, [f.Id(1)], out var tagOutcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, tagOutcome);
        Assert.Equal(a.Id, tag.Entries[f.Id(1)]);
        Assert.Equal(new[] { "data:confirm", "flush:1", "flush:2" },
            f.Events.Where(value => value == "data:confirm" || value.StartsWith("flush:", StringComparison.Ordinal)));
        Assert.Equal(new uint[] { 1 }, ConditionalUpdateFaultFixture.OwnedHeadIds(reopened));
    }

    [Fact]
    public void ConfirmedOutcomeSurvivesDeliveryExceptionAndColdReadPreservesTheCapturedEntry() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var failure = new OutOfMemoryException("delivery after durable bucket");
        f.Hooks.AfterPublicationConfirmed = () => throw failure;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;

        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => store.CreateTag(Name, [f.Id(1)], out outcome)));

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        f.AssertFaultCleanup();
        f.Close();
        using var reader = f.OpenPublic(readOnly: true);
        var tag = reader.ReadTag(Name).Unwrap();
        Assert.Equal(f.Initial[1], tag.Entries[f.Id(1)]);
        Assert.Equal(ConditionalUpdateFaultFixture.Old(1), reader.ReadEntry(tag.Entries[f.Id(1)]).Unwrap().Payload.ToArray());
    }

    private static IReadOnlyDictionary<HeadId, HeadEntryId> SeedCu(ConditionalUpdateFaultFixture f) {
        using var writer = f.OpenPublic();
        return writer.PublishHeads(f.Changes((1, [0xa1]), (2, [0xb1])), out _).Unwrap();
    }

    private static void AssertColdMissing(ConditionalUpdateFaultFixture f) {
        f.Close();
        using var reader = f.OpenPublic(readOnly: true);
        var result = reader.ReadTag(Name);
        Assert.True(result.IsFailure);
        Assert.Equal("HeadStore.TagNotFound", result.Error!.ErrorCode);
    }
}
