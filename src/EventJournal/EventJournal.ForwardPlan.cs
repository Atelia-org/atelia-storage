using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Atelia.Data.Hashing;
using Atelia.Rbf;

namespace Atelia.EventJournal;

internal readonly record struct RouteRedirect(EventAddress FromEvent, EventAddress ToChild);

internal sealed class EphemeralForwardPlan {
    public required EventAddress RootEvent { get; init; }
    public required EventAddress TargetHead { get; init; }
    public required ulong EventCount { get; init; }
    public required IReadOnlyList<RouteRedirect> Redirects { get; init; }
}

internal readonly record struct OptionalEphemeralForwardPlan {
    private readonly EphemeralForwardPlan? _value;

    public OptionalEphemeralForwardPlan(EphemeralForwardPlan value) {
        _value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public EphemeralForwardPlan Value => HasValue
        ? _value!
        : throw new InvalidOperationException("OptionalEphemeralForwardPlan has no value.");

    public static OptionalEphemeralForwardPlan None => default;
}

internal readonly record struct ForwardPlanCacheStats(
    ulong ExactHits,
    ulong Misses,
    ulong PrefixHits,
    ulong Evictions,
    ulong DiskHits,
    ulong DiskWrites
);

public sealed partial class EventJournal {
    internal int ForwardPlanCacheEntryCount => _forwardPlanCache.Count;
    internal ForwardPlanCacheStats ForwardPlanCacheStats => _forwardPlanCache.Stats;

    internal void EvictForwardPlan(EventAddress targetHead) {
        _forwardPlanCache.Remove(targetHead);
        TryDeleteCompiledForwardPlan(targetHead);
    }

    public AteliaResult<IReadOnlyList<EventAddress>> ReadChronologicalChain(
        RefId refId,
        bool checkedRead = false,
        int? maxDepth = null,
        bool detectCycles = true,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();

        var stateResult = LoadRefState(refId);
        if (stateResult.IsFailure) { return stateResult.Error!; }

        RefState state = stateResult.Unwrap();
        if (state.Closed) { return RefClosedError(refId); }
        if (state.Head is not { } head) { return Array.Empty<EventAddress>(); }

        return ReadChronologicalChain(head, checkedRead, maxDepth, detectCycles, cancellationToken);
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

        var diskLoadResult = TryLoadCompiledForwardPlan(head);
        if (diskLoadResult.IsFailure) { return diskLoadResult.Error!; }
        OptionalEphemeralForwardPlan optionalDiskPlan = diskLoadResult.Unwrap();
        if (optionalDiskPlan.HasValue) {
            EphemeralForwardPlan diskPlan = optionalDiskPlan.Value;
            if (ExceedsMaxDepth(diskPlan.EventCount, maxDepth)) {
                return TraversalDepthExceededError(maxDepth.GetValueOrDefault(), "compiled forward plan");
            }

            _forwardPlanCache.AddOrReplace(diskPlan);
            _forwardPlanCache.RecordDiskHit();
            return diskPlan;
        }

        _forwardPlanCache.RecordMiss();
        var buildResult = BuildEphemeralForwardPlanCore(head, maxDepth, detectCycles, cancellationToken);
        if (buildResult.IsFailure) { return buildResult.Error!; }

        EphemeralForwardPlan plan = buildResult.Unwrap();
        _forwardPlanCache.AddOrReplace(plan);
        if (TrySaveCompiledForwardPlan(plan)) {
            _forwardPlanCache.RecordDiskWrite();
        }

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
        private ulong _diskHits;
        private ulong _diskWrites;

        internal ForwardPlanCache(int maxEntries, long maxEstimatedBytes) {
            _maxEntries = maxEntries;
            _maxEstimatedBytes = maxEstimatedBytes;
        }

        internal int Count => _entries.Count;
        internal ForwardPlanCacheStats Stats => new(_exactHits, _misses, _prefixHits, _evictions, _diskHits, _diskWrites);

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

        internal void RecordDiskHit() {
            _diskHits++;
        }

        internal void RecordDiskWrite() {
            _diskWrites++;
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

    private AteliaResult<OptionalEphemeralForwardPlan> TryLoadCompiledForwardPlan(EventAddress head) {
        string path = CompiledForwardPlanPath(head);
        if (!File.Exists(path)) { return OptionalEphemeralForwardPlan.None; }

        try {
            byte[] bytes = File.ReadAllBytes(path);
            var decodeResult = ForwardPlanCompiledCacheCodec.Decode(bytes, head);
            if (decodeResult.IsFailure) {
                TryDeleteFile(path);
                return OptionalEphemeralForwardPlan.None;
            }

            EphemeralForwardPlan plan = decodeResult.Unwrap();
            var headerResult = ReadEventHeaderPreview(plan.TargetHead);
            if (headerResult.IsFailure) {
                TryDeleteFile(path);
                return OptionalEphemeralForwardPlan.None;
            }

            return new OptionalEphemeralForwardPlan(plan);
        }
        catch (Exception ex) when (IsCompiledCacheException(ex)) {
            TryDeleteFile(path);
            return OptionalEphemeralForwardPlan.None;
        }
    }

    private bool TrySaveCompiledForwardPlan(EphemeralForwardPlan plan) {
        try {
            Directory.CreateDirectory(_forwardPlanCachePath);
            string finalPath = CompiledForwardPlanPath(plan.TargetHead);
            string tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = ForwardPlanCompiledCacheCodec.Encode(plan);

            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (IsCompiledCacheException(ex)) {
            return false;
        }
    }

    private void TryDeleteCompiledForwardPlan(EventAddress targetHead) {
        TryDeleteFile(CompiledForwardPlanPath(targetHead));
    }

    private string CompiledForwardPlanPath(EventAddress targetHead) {
        string fileName = $"s{targetHead.SegmentNumber:x8}-t{targetHead.Ticket.Packed:x16}-h{targetHead.Hint.Packed:x8}.efplan";
        return Path.Combine(_forwardPlanCachePath, fileName);
    }

    private static void TryDeleteFile(string path) {
        try {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch {
            // Best-effort cleanup: compiled cache files are disposable.
        }
    }

    private static bool IsCompiledCacheException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException;

    private static class ForwardPlanCompiledCacheCodec {
        private const uint Magic = 0x5046_4A45; // "EJFP" as little-endian bytes.
        private const uint FormatVersion = 1;
        private const uint PolicyVersion = 1;
        private const int FixedHeaderLength =
            sizeof(uint) + sizeof(uint) + sizeof(uint) + sizeof(uint) +
            EventAddressCodec.EventAddressLength +
            EventAddressCodec.EventAddressLength +
            sizeof(ulong) +
            sizeof(uint);
        private const int CrcLength = sizeof(uint);
        private const int RedirectLength = EventAddressCodec.EventAddressLength * 2;

        internal static byte[] Encode(EphemeralForwardPlan plan) {
            checked {
                int length = FixedHeaderLength + plan.Redirects.Count * RedirectLength + CrcLength;
                byte[] bytes = new byte[length];
                Span<byte> span = bytes;
                int offset = 0;

                WriteUInt32(span, ref offset, Magic);
                WriteUInt32(span, ref offset, FormatVersion);
                WriteUInt32(span, ref offset, PolicyVersion);
                WriteUInt32(span, ref offset, 0);
                WriteAddress(span, ref offset, plan.TargetHead);
                WriteAddress(span, ref offset, plan.RootEvent);
                WriteUInt64(span, ref offset, plan.EventCount);
                WriteUInt32(span, ref offset, (uint)plan.Redirects.Count);

                foreach (RouteRedirect redirect in plan.Redirects) {
                    WriteAddress(span, ref offset, redirect.FromEvent);
                    WriteAddress(span, ref offset, redirect.ToChild);
                }

                uint crc = RollingCrc.CrcForward(span[..offset]);
                WriteUInt32(span, ref offset, crc);
                return bytes;
            }
        }

        internal static AteliaResult<EphemeralForwardPlan> Decode(ReadOnlySpan<byte> bytes, EventAddress expectedHead) {
            if (bytes.Length < FixedHeaderLength + CrcLength) {
                return InvalidCompiledCache("Compiled ForwardPlan cache file is too short.");
            }

            uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^CrcLength..]);
            uint expectedCrc = RollingCrc.CrcForward(bytes[..^CrcLength]);
            if (actualCrc != expectedCrc) {
                return InvalidCompiledCache("Compiled ForwardPlan cache CRC mismatch.");
            }

            int offset = 0;
            uint magic = ReadUInt32(bytes, ref offset);
            uint formatVersion = ReadUInt32(bytes, ref offset);
            uint policyVersion = ReadUInt32(bytes, ref offset);
            _ = ReadUInt32(bytes, ref offset);
            if (magic != Magic || formatVersion != FormatVersion || policyVersion != PolicyVersion) {
                return InvalidCompiledCache("Compiled ForwardPlan cache format or policy version does not match this implementation.");
            }

            var targetResult = ReadAddress(bytes, ref offset);
            if (targetResult.IsFailure) { return targetResult.Error!; }
            EventAddress targetHead = targetResult.Unwrap();
            if (targetHead != expectedHead) {
                return InvalidCompiledCache("Compiled ForwardPlan cache target head does not match requested head.");
            }

            var rootResult = ReadAddress(bytes, ref offset);
            if (rootResult.IsFailure) { return rootResult.Error!; }
            EventAddress rootEvent = rootResult.Unwrap();
            ulong eventCount = ReadUInt64(bytes, ref offset);
            uint redirectCount = ReadUInt32(bytes, ref offset);
            if (eventCount == 0) {
                return InvalidCompiledCache("Compiled ForwardPlan cache has zero EventCount.");
            }

            int expectedLength = checked(FixedHeaderLength + (int)redirectCount * RedirectLength + CrcLength);
            if (bytes.Length != expectedLength) {
                return InvalidCompiledCache("Compiled ForwardPlan cache length does not match RedirectCount.");
            }

            var redirects = new RouteRedirect[redirectCount];
            for (int i = 0; i < redirects.Length; i++) {
                var fromResult = ReadAddress(bytes, ref offset);
                if (fromResult.IsFailure) { return fromResult.Error!; }
                var toResult = ReadAddress(bytes, ref offset);
                if (toResult.IsFailure) { return toResult.Error!; }
                redirects[i] = new RouteRedirect(fromResult.Unwrap(), toResult.Unwrap());
            }

            return new EphemeralForwardPlan {
                RootEvent = rootEvent,
                TargetHead = targetHead,
                EventCount = eventCount,
                Redirects = redirects
            };
        }

        private static void WriteAddress(Span<byte> destination, ref int offset, EventAddress address) {
            EventAddressCodec.Encode(address, destination.Slice(offset, EventAddressCodec.EventAddressLength));
            offset += EventAddressCodec.EventAddressLength;
        }

        private static AteliaResult<EventAddress> ReadAddress(ReadOnlySpan<byte> source, ref int offset) {
            var result = EventAddressCodec.Decode(source.Slice(offset, EventAddressCodec.EventAddressLength));
            offset += EventAddressCodec.EventAddressLength;
            return result;
        }

        private static void WriteUInt32(Span<byte> destination, ref int offset, uint value) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, sizeof(uint)), value);
            offset += sizeof(uint);
        }

        private static void WriteUInt64(Span<byte> destination, ref int offset, ulong value) {
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, sizeof(ulong)), value);
            offset += sizeof(ulong);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset) {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, sizeof(uint)));
            offset += sizeof(uint);
            return value;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset) {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));
            offset += sizeof(ulong);
            return value;
        }

        private static EventJournalError InvalidCompiledCache(string message) => new(
            "ForwardPlanCompiledCacheInvalid",
            message,
            "Delete the compiled cache file and rebuild the ForwardPlan from the parent chain."
        );
    }
}
