using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

public sealed class EventJournalOptions {
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

        return new EventJournalOptions {
            EventSegmentStoreOptions = WithLayout(EventSegmentStoreOptions, RbfSegmentStoreLayout.Bucketed),
            RefSegmentStoreOptions = WithLayout(RefSegmentStoreOptions, RbfSegmentStoreLayout.Flat),
            RefOpLogOptions = RefOpLogOptions
        };
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
