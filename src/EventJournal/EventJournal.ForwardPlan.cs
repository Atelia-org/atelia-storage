using Atelia.Rbf;

namespace Atelia.EventJournal;

internal readonly record struct RouteRedirect(EventAddress FromEvent, EventAddress ToChild);

internal sealed class EphemeralForwardPlan {
    public required EventAddress RootEvent { get; init; }
    public required EventAddress TargetHead { get; init; }
    public required ulong EventCount { get; init; }
    public required IReadOnlyList<RouteRedirect> Redirects { get; init; }
}

public sealed partial class EventJournal {
    internal AteliaResult<EphemeralForwardPlan> BuildEphemeralForwardPlan(
        EventAddress head,
        int? maxDepth = null,
        bool detectCycles = true,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (maxDepth is <= 0) {
            return new EventJournalError(
                "MaxDepthInvalid",
                $"maxDepth must be positive when provided, got {maxDepth}.",
                "Use null for unbounded traversal or a positive depth limit."
            );
        }

        var redirectsReverse = new List<RouteRedirect>();
        HashSet<EventAddress>? seen = detectCycles ? new HashSet<EventAddress>() : null;
        EventAddress child = head;
        ulong eventCount = 0;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            if (maxDepth is { } depthLimit && eventCount >= (ulong)depthLimit) {
                return new EventJournalError(
                    "TraversalDepthExceeded",
                    $"Forward-plan build exceeded maxDepth={depthLimit} before reaching root.",
                    "Increase maxDepth or inspect the parent chain for unexpectedly long history."
                );
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

            eventCount++;
            EventFrameHeader childHeader = childHeaderResult.Unwrap();
            if (childHeader.Parent is not { } parent) {
                redirectsReverse.Reverse();
                return new EphemeralForwardPlan {
                    RootEvent = child,
                    TargetHead = head,
                    EventCount = eventCount,
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
}
