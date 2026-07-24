using Atelia.RbfSegmentStore;
using Atelia.Rbf;

namespace Atelia.EventJournal;

public sealed class EventJournalOptions {
    public int MaxLogicalPayloadLength { get; init; } = RbfFile.MaxPayloadAndMetaLength - EventFrameHeaderCodec.FixedLength;

    public EventPayloadCodecPolicy PayloadCodecPolicy { get; init; } = EventPayloadCodecPolicy.Identity;

    public RbfSegmentStoreOptions EventSegmentStoreOptions { get; init; } = new() {
        NewStoreLayout = RbfSegmentStoreLayout.Bucketed,
        SegmentSizeThresholdBytes = 64L * 1024 * 1024 * 1024
    };

    public RbfSegmentStoreOptions RefSegmentStoreOptions { get; init; } = new() {
        NewStoreLayout = RbfSegmentStoreLayout.Flat,
        SegmentSizeThresholdBytes = 64L * 1024 * 1024
    };

    public RefOpLogOptions RefOpLogOptions { get; init; } = new();

    internal EventJournalOptions Normalized() {
        RefOpLogOptions.Validated();
        ValidateMaxLogicalPayloadLength(MaxLogicalPayloadLength);
        if (EventPayloadCodec.ValidatePolicy(PayloadCodecPolicy) is { } policyError) { throw new ArgumentException(policyError.Message, nameof(PayloadCodecPolicy)); }

        return new EventJournalOptions {
            MaxLogicalPayloadLength = MaxLogicalPayloadLength,
            PayloadCodecPolicy = PayloadCodecPolicy,
            EventSegmentStoreOptions = WithLayout(EventSegmentStoreOptions, RbfSegmentStoreLayout.Bucketed),
            RefSegmentStoreOptions = WithLayout(RefSegmentStoreOptions, RbfSegmentStoreLayout.Flat),
            RefOpLogOptions = RefOpLogOptions
        };
    }

    private static void ValidateMaxLogicalPayloadLength(int maxLogicalPayloadLength) {
        if (maxLogicalPayloadLength < 0) { throw new ArgumentOutOfRangeException(nameof(maxLogicalPayloadLength), maxLogicalPayloadLength, "MaxLogicalPayloadLength must be non-negative."); }

        int maxSingleFrameLogicalPayload = RbfFile.MaxPayloadAndMetaLength - EventFrameHeaderCodec.FixedLength;
        if (maxLogicalPayloadLength > maxSingleFrameLogicalPayload) {
            throw new ArgumentOutOfRangeException(
                nameof(maxLogicalPayloadLength),
                maxLogicalPayloadLength,
                $"MaxLogicalPayloadLength cannot exceed the EventFrame single-frame logical limit {maxSingleFrameLogicalPayload}."
            );
        }
    }

    private static RbfSegmentStoreOptions WithLayout(RbfSegmentStoreOptions source, RbfSegmentStoreLayout layout) {
        return new RbfSegmentStoreOptions {
            NewStoreLayout = layout,
            SegmentSizeThresholdBytes = source.SegmentSizeThresholdBytes,
            HistoricalReaderPoolCapacity = source.HistoricalReaderPoolCapacity,
            CacheMode = source.CacheMode,
            RecoverActiveTailOnOpen = source.RecoverActiveTailOnOpen
        };
    }
}
