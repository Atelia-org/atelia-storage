using Atelia.Data;
using Atelia.RbfSegmentStore;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class ForwardPlanBoundedTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), "forward-plan-bounded-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_path)) { Directory.Delete(_path, recursive: true); }
    }

    [Fact]
    public void ManyHeads_EvictOldestAndRebuildExactParentChainWithoutDiskArtifacts() {
        using var journal = EventJournal.CreateNew(_path);
        EventAddress oldest = journal.AppendEventFrame(null, "root"u8).Unwrap();
        RefId main = journal.CreateBranch("main", oldest).Unwrap();
        journal.ReadChronologicalChain(main).Unwrap();
        EventAddress current = oldest;
        for (int i = 1; i <= 4096; i++) {
            // Each root is independent: cache eviction cannot hide behind prefix reuse.
            EventAddress next = journal.AppendEventFrame(null, "root"u8).Unwrap();
            journal.MoveRef(main, current, next).Unwrap();
            Assert.Equal(new[] { next }, journal.ReadChronologicalChain(main).Unwrap());
            current = next;
        }

        Assert.Equal(4096, journal.ForwardPlanCacheEntryCount);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.Evictions);
        Assert.InRange(journal.ForwardPlanCacheEstimatedBytes, 1, 16 * 1024 * 1024);
        ulong misses = journal.ForwardPlanCacheStats.Misses;
        journal.MoveRef(main, current, oldest).Unwrap();
        Assert.Equal(new[] { oldest }, journal.ReadChronologicalChain(main).Unwrap());
        Assert.Equal(misses + 1, journal.ForwardPlanCacheStats.Misses);
        Assert.Equal<ulong>(2, journal.ForwardPlanCacheStats.Evictions);
        Assert.False(Directory.Exists(Path.Combine(_path, "cache")));
        journal.Dispose();
        Assert.Equal(0, journal.ForwardPlanCacheEntryCount);
        Assert.Equal(0, journal.ForwardPlanCacheEstimatedBytes);
    }

    [Fact]
    public void Cache_ByteBudgetEvictsAndOversizedPlanIsNotRetained() {
        const int budget = 16 * 1024 * 1024;
        var cache = new EventJournal.ForwardPlanCache(4096, budget);
        EventAddress first = Address(4);
        EventAddress second = Address(36);
        // Dense redirect plans consume the bytes budget before the entries budget.
        var redirects = new RouteRedirect[budget / 32 / 2];
        cache.AddOrReplace(Plan(first, redirects));
        cache.AddOrReplace(Plan(second, redirects));
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(second, out _));
        Assert.Equal<ulong>(1, cache.Stats.Evictions);
        Assert.InRange(cache.EstimatedBytes, 1, budget);

        EventAddress oversized = Address(68);
        cache.AddOrReplace(Plan(oversized, new RouteRedirect[budget / 32]));
        Assert.False(cache.TryGet(oversized, out _));
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedBytes);
    }

    [Fact]
    public void InterleavedBranchesAcrossSegments_ColdReplayAndRewindFollowParent() {
        var options = new EventJournalOptions {
            EventSegmentStoreOptions = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 200 }
        };
        RefId main;
        EventAddress root;
        EventAddress middle;
        EventAddress head;
        EventAddress sibling;
        using (var journal = EventJournal.CreateNew(_path, options)) {
            root = journal.AppendEventFrame(null, "root"u8).Unwrap();
            main = journal.CreateBranch("main", root).Unwrap();
            RefId other = journal.ForkBranch("other", main, root).Unwrap();
            sibling = journal.CommitToRef(other, root, "sibling"u8).Unwrap().EventAddress;
            middle = journal.CommitToRef(main, root, "middle"u8).Unwrap().EventAddress;
            journal.AppendEventFrame(sibling, "orphan"u8).Unwrap();
            head = journal.CommitToRef(main, middle, "head"u8).Unwrap().EventAddress;
            Assert.NotEqual(root.SegmentNumber, head.SegmentNumber);
            Assert.Equal(new[] { root, middle, head }, journal.ReadChronologicalChain(main, checkedRead: true).Unwrap());
        }

        using var reopened = EventJournal.OpenExisting(_path, options);
        var replay = reopened.ReadChronologicalChain(main, checkedRead: true).Unwrap();
        Assert.Equal(new[] { root, middle, head }, replay);
        Assert.Equal(reopened.ReadAncestorChain(head, checkedRead: true).Unwrap().Reverse(), replay);
        Assert.Equal<ulong>(3, reopened.ForwardPlanCacheStats.ParentWalkReads);
        reopened.MoveRef(main, head, middle).Unwrap();
        Assert.Equal(new[] { root, middle }, reopened.ReadChronologicalChain(main, checkedRead: true).Unwrap());
        Assert.Equal(new[] { root, sibling }, reopened.ReadChronologicalChain(sibling, checkedRead: true).Unwrap());
        Assert.False(Directory.Exists(Path.Combine(_path, "cache")));
    }

    [Fact]
    public void CachedPlan_CheckedReplayRejectsHistoricalPayloadCrcAndEvicts() {
        EventAddress root;
        EventAddress head;
        using (var journal = EventJournal.CreateNew(_path)) {
            root = journal.AppendEventFrame(null, "root"u8).Unwrap();
            head = journal.AppendEventFrame(root, "head"u8).Unwrap();
            journal.ReadChronologicalChain(head).Unwrap();
        }

        string eventFile = Path.Combine(_path, "events", "buckets", "000000", $"{root.SegmentNumber:x8}.rbf");
        using (var stream = new FileStream(eventFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.Position = root.Ticket.Offset + 4;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 1));
        }

        using var reopened = EventJournal.OpenExisting(_path);
        Assert.Equal(new[] { root, head }, reopened.ReadChronologicalChain(head).Unwrap());
        Assert.Equal(1, reopened.ForwardPlanCacheEntryCount);
        var result = reopened.ReadChronologicalChain(head, checkedRead: true);
        Assert.True(result.IsFailure);
        Assert.Equal(reopened.ReadEventHeaderChecked(root).Error!.ErrorCode, result.Error!.ErrorCode);
        Assert.Equal<ulong>(1, reopened.ForwardPlanCacheStats.ExactHits);
        Assert.Equal(0, reopened.ForwardPlanCacheEntryCount);
        Assert.False(Directory.Exists(Path.Combine(_path, "cache")));
    }

    [Fact]
    public void ExactHeadAccess_PromotesLruEntry() {
        var cache = new EventJournal.ForwardPlanCache(2, 16 * 1024 * 1024);
        EventAddress first = Address(4);
        EventAddress second = Address(36);
        EventAddress third = Address(68);
        cache.AddOrReplace(Plan(first, Array.Empty<RouteRedirect>()));
        cache.AddOrReplace(Plan(second, Array.Empty<RouteRedirect>()));
        Assert.True(cache.TryGet(first, out _));
        cache.AddOrReplace(Plan(third, Array.Empty<RouteRedirect>()));
        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out _));
        Assert.Equal(2, cache.Count);
    }

    private static EventAddress Address(long offset) => new(SizedPtr.Create(offset, 32), 1, default);

    private static EphemeralForwardPlan Plan(EventAddress head, IReadOnlyList<RouteRedirect> redirects) => new() {
        RootEvent = head,
        TargetHead = head,
        EventCount = 1,
        Redirects = redirects
    };
}
