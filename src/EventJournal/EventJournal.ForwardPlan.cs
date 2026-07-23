using System.Diagnostics.CodeAnalysis;
using Atelia.Rbf;

namespace Atelia.EventJournal;

internal readonly record struct RouteRedirect(EventAddress FromEvent, EventAddress ToChild);

internal sealed class EphemeralForwardPlan {
    public required EventAddress RootEvent { get; init; }
    public required EventAddress TargetHead { get; init; }
    public required ulong EventCount { get; init; }
    public required IReadOnlyList<RouteRedirect> Redirects { get; init; }
}

internal readonly record struct ForwardPlanCacheStats(
    ulong ExactHits,
    ulong Misses,
    ulong PrefixHits,
    ulong Evictions
);

public sealed partial class EventJournal {
    internal int ForwardPlanCacheEntryCount => _forwardPlanCache.Count;
    internal ForwardPlanCacheStats ForwardPlanCacheStats => _forwardPlanCache.Stats;

    internal void EvictForwardPlan(EventAddress targetHead) {
        _forwardPlanCache.Remove(targetHead);
    }

    internal AteliaResult<EphemeralForwardPlan> BuildEphemeralForwardPlan(
        EventAddress head,
        int? maxDepth = null,
        bool detectCycles = true,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (maxDepth is <= 0) {
            return MaxDepthInvalidError(maxDepth.Value);
        }

        if (_forwardPlanCache.TryGet(head, out EphemeralForwardPlan? cachedPlan)) {
            if (ExceedsMaxDepth(cachedPlan.EventCount, maxDepth)) {
                return TraversalDepthExceededError(maxDepth.GetValueOrDefault(), "cached forward plan");
            }

            _forwardPlanCache.RecordExactHit();
            return cachedPlan;
        }

        _forwardPlanCache.RecordMiss();
        var buildResult = BuildEphemeralForwardPlanCore(head, maxDepth, detectCycles, cancellationToken);
        if (buildResult.IsFailure) { return buildResult.Error!; }

        EphemeralForwardPlan plan = buildResult.Unwrap();
        _forwardPlanCache.AddOrReplace(plan);
        return plan;
    }

    private AteliaResult<EphemeralForwardPlan> BuildEphemeralForwardPlanCore(
        EventAddress head,
        int? maxDepth,
        bool detectCycles,
        CancellationToken cancellationToken
    ) {
        var redirectsReverse = new List<RouteRedirect>();
        HashSet<EventAddress>? seen = detectCycles ? new HashSet<EventAddress>() : null;
        EventAddress child = head;
        ulong suffixEventCount = 0;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            if (suffixEventCount > 0 && _forwardPlanCache.TryGet(child, out EphemeralForwardPlan? cachedPrefix)) {
                ulong totalEventCount = checked(cachedPrefix.EventCount + suffixEventCount);
                if (ExceedsMaxDepth(totalEventCount, maxDepth)) {
                    return TraversalDepthExceededError(maxDepth.GetValueOrDefault(), "cached-prefix forward-plan build");
                }

                redirectsReverse.Reverse();
                var redirects = new RouteRedirect[cachedPrefix.Redirects.Count + redirectsReverse.Count];
                for (int i = 0; i < cachedPrefix.Redirects.Count; i++) {
                    redirects[i] = cachedPrefix.Redirects[i];
                }

                for (int i = 0; i < redirectsReverse.Count; i++) {
                    redirects[cachedPrefix.Redirects.Count + i] = redirectsReverse[i];
                }

                var combined = new EphemeralForwardPlan {
                    RootEvent = cachedPrefix.RootEvent,
                    TargetHead = head,
                    EventCount = totalEventCount,
                    Redirects = redirects
                };
                _forwardPlanCache.RecordPrefixHit();
                return combined;
            }

            if (maxDepth is { } depthLimit && suffixEventCount >= (ulong)depthLimit) {
                return TraversalDepthExceededError(depthLimit, "forward-plan build");
            }

            if (seen is not null && !seen.Add(child)) {
                return new EventJournalError(
                    "TraversalCycleDetected",
                    "Forward-plan build encountered the same EventAddress twice.",
                    "Inspect the EventFrame parent chain for corruption."
                );
            }

            var childHeaderResult = ReadEventHeaderPreview(child);
            if (childHeaderResult.IsFailure) { return childHeaderResult.Error!; }

            suffixEventCount++;
            EventFrameHeader childHeader = childHeaderResult.Unwrap();
            if (childHeader.Parent is not { } parent) {
                redirectsReverse.Reverse();
                return new EphemeralForwardPlan {
                    RootEvent = child,
                    TargetHead = head,
                    EventCount = suffixEventCount,
                    Redirects = redirectsReverse.ToArray()
                };
            }

            if (ComparePhysicalCoordinate(parent, child) >= 0) {
                return new EventJournalError(
                    "ParentPhysicalOrderInvalid",
                    "EventFrame parent must be physically earlier than its child.",
                    "Inspect the EventFrame parent chain for corruption."
                );
            }

            if (!IsImplicitEdge(parent, child)) {
                redirectsReverse.Add(new RouteRedirect(parent, child));
            }

            child = parent;
        }
    }

    private AteliaResult<IReadOnlyList<EventAddress>> ReplayForwardPlanAddresses(
        EphemeralForwardPlan plan,
        bool checkedRead,
        CancellationToken cancellationToken = default
    ) {
        if (plan.EventCount > int.MaxValue) {
            return new EventJournalError(
                "TraversalTooLarge",
                $"Forward traversal contains {plan.EventCount} events, which exceeds List<T> capacity.",
                "Use a streaming traversal API for very large histories."
            );
        }

        var addresses = new List<EventAddress>((int)plan.EventCount);
        EventAddress current = plan.RootEvent;
        EventAddress? previous = null;
        int redirectIndex = 0;

        for (ulong producedCount = 1; producedCount <= plan.EventCount; producedCount++) {
            cancellationToken.ThrowIfCancellationRequested();

            var headerResult = checkedRead ? ReadEventHeaderChecked(current) : ReadEventHeaderPreview(current);
            if (headerResult.IsFailure) { return headerResult.Error!; }

            EventFrameHeader header = headerResult.Unwrap();
            if (!Nullable.Equals(header.Parent, previous)) {
                return new EventJournalError(
                    "ForwardPlanParentMismatch",
                    "Forward-plan replay reached an EventFrame whose Parent does not match the previous path event.",
                    "Rebuild the plan and inspect the EventFrame parent chain if the error repeats."
                );
            }

            addresses.Add(current);

            if (current == plan.TargetHead) {
                if (producedCount != plan.EventCount || redirectIndex != plan.Redirects.Count) {
                    return new EventJournalError(
                        "ForwardPlanConsumptionMismatch",
                        "Forward-plan replay reached the target head before consuming the expected event count or redirects.",
                        "Treat the forward plan as invalid and rebuild it."
                    );
                }

                return addresses;
            }

            EventAddress next;
            if (redirectIndex < plan.Redirects.Count && plan.Redirects[redirectIndex].FromEvent == current) {
                next = plan.Redirects[redirectIndex].ToChild;
                redirectIndex++;
            }
            else {
                var nextResult = ReadPhysicalEventImmediatelyAfter(current);
                if (nextResult.IsFailure) { return nextResult.Error!; }
                next = nextResult.Unwrap();
            }

            previous = current;
            current = next;
        }

        return new EventJournalError(
            "ForwardPlanDidNotReachTargetHead",
            "Forward-plan replay consumed its event budget without reaching the target head.",
            "Treat the forward plan as invalid and rebuild it."
        );
    }

    private bool IsImplicitEdge(EventAddress parent, EventAddress child) {
        if (parent.SegmentNumber != child.SegmentNumber) { return false; }

        using var lease = _segments.OpenReader(parent.SegmentNumber);
        return child.Ticket.Offset == lease.File.GetPhysicalOffsetImmediatelyAfter(parent.Ticket);
    }

    private AteliaResult<EventAddress> ReadPhysicalEventImmediatelyAfter(EventAddress current) {
        try {
            using var lease = _segments.OpenReader(current.SegmentNumber);
            var infoResult = lease.File.ReadFrameInfoImmediatelyAfter(current.Ticket);
            if (infoResult.IsFailure) { return infoResult.Error!; }

            OptionalRbfFrameInfo optionalInfo = infoResult.Unwrap();
            if (!optionalInfo.HasValue) {
                return new EventJournalError(
                    "ForwardPlanImplicitEdgeMissing",
                    "Forward-plan replay expected an implicit physical successor, but the current frame is at segment tail.",
                    "Treat the forward plan as invalid and rebuild it."
                );
            }

            return DecodePhysicalEventAddress(lease.SegmentNumber, optionalInfo.Value);
        }
        catch (Exception ex) when (IsReadException(ex)) {
            return ReadFailedError(current, ex);
        }
    }

    private static AteliaResult<EventAddress> DecodePhysicalEventAddress(uint segmentNumber, RbfFrameInfo info) {
        if (info.Tag != EventFrameTag) { return WrongFrameTagError(info.Tag); }
        if (info.IsTombstone) { return TombstoneError(); }
        if (info.TailMetaLength != EventFrameHeaderCodec.FixedLength) {
            return new EventJournalError(
                "FrameTailMetaLengthInvalid",
                $"EventFrame TailMeta length must be {EventFrameHeaderCodec.FixedLength}, got {info.TailMetaLength}.",
                "Verify that the frame was written by EventJournal v1."
            );
        }

        using var tailMetaResult = info.ReadPooledTailMeta().ToDisposable();
        if (tailMetaResult.IsFailure) { return tailMetaResult.Error!; }

        var headerResult = EventFrameHeaderCodec.Decode(tailMetaResult.Unwrap().TailMeta);
        if (headerResult.IsFailure) { return headerResult.Error!; }

        EventFrameHeader header = headerResult.Unwrap();
        return new EventAddress(info.Ticket, segmentNumber, header.Hint);
    }

    private static int ComparePhysicalCoordinate(EventAddress left, EventAddress right) {
        int segmentComparison = left.SegmentNumber.CompareTo(right.SegmentNumber);
        return segmentComparison != 0 ? segmentComparison : left.Ticket.Offset.CompareTo(right.Ticket.Offset);
    }

    private static bool ExceedsMaxDepth(ulong eventCount, int? maxDepth) =>
        maxDepth is { } depthLimit && eventCount > (ulong)depthLimit;

    private static EventJournalError MaxDepthInvalidError(int maxDepth) => new(
        "MaxDepthInvalid",
        $"maxDepth must be positive when provided, got {maxDepth}.",
        "Use null for unbounded traversal or a positive depth limit."
    );

    private static EventJournalError TraversalDepthExceededError(int maxDepth, string operation) => new(
        "TraversalDepthExceeded",
        $"{operation} exceeded maxDepth={maxDepth} before reaching root.",
        "Increase maxDepth or inspect the parent chain for unexpectedly long history."
    );

    private sealed class ForwardPlanCache {
        private const long BasePlanEstimatedBytes = 128;
        private const long RedirectEstimatedBytes = 32;

        private readonly int _maxEntries;
        private readonly long _maxEstimatedBytes;
        private readonly Dictionary<EventAddress, LinkedListNode<Entry>> _entries = new();
        private readonly LinkedList<Entry> _lru = new();
        private long _estimatedBytes;
        private ulong _exactHits;
        private ulong _misses;
        private ulong _prefixHits;
        private ulong _evictions;

        internal ForwardPlanCache(int maxEntries, long maxEstimatedBytes) {
            _maxEntries = maxEntries;
            _maxEstimatedBytes = maxEstimatedBytes;
        }

        internal int Count => _entries.Count;
        internal ForwardPlanCacheStats Stats => new(_exactHits, _misses, _prefixHits, _evictions);

        internal bool TryGet(EventAddress targetHead, [NotNullWhen(true)] out EphemeralForwardPlan? plan) {
            if (_entries.TryGetValue(targetHead, out LinkedListNode<Entry>? node)) {
                _lru.Remove(node);
                _lru.AddFirst(node);
                plan = node.Value.Plan;
                return true;
            }

            plan = null;
            return false;
        }

        internal void RecordMiss() {
            _misses++;
        }

        internal void RecordExactHit() {
            _exactHits++;
        }

        internal void RecordPrefixHit() {
            _prefixHits++;
        }

        internal void AddOrReplace(EphemeralForwardPlan plan) {
            if (_maxEntries <= 0 || _maxEstimatedBytes <= 0) { return; }

            long estimatedBytes = EstimateBytes(plan);
            if (estimatedBytes > _maxEstimatedBytes) { return; }

            if (_entries.TryGetValue(plan.TargetHead, out LinkedListNode<Entry>? existing)) {
                _estimatedBytes -= existing.Value.EstimatedBytes;
                _lru.Remove(existing);
                _entries.Remove(plan.TargetHead);
            }

            var entry = new Entry(plan, estimatedBytes);
            var node = new LinkedListNode<Entry>(entry);
            _lru.AddFirst(node);
            _entries.Add(plan.TargetHead, node);
            _estimatedBytes += estimatedBytes;

            EvictOverBudget();
        }

        internal bool Remove(EventAddress targetHead) {
            if (!_entries.Remove(targetHead, out LinkedListNode<Entry>? node)) { return false; }

            _lru.Remove(node);
            _estimatedBytes -= node.Value.EstimatedBytes;
            return true;
        }

        private void EvictOverBudget() {
            while (_entries.Count > _maxEntries || _estimatedBytes > _maxEstimatedBytes) {
                LinkedListNode<Entry>? last = _lru.Last;
                if (last is null) { return; }

                _lru.RemoveLast();
                _entries.Remove(last.Value.Plan.TargetHead);
                _estimatedBytes -= last.Value.EstimatedBytes;
                _evictions++;
            }
        }

        private static long EstimateBytes(EphemeralForwardPlan plan) =>
            BasePlanEstimatedBytes + plan.Redirects.Count * RedirectEstimatedBytes;

        private sealed record Entry(EphemeralForwardPlan Plan, long EstimatedBytes);
    }
}
