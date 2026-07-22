using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

public sealed class EventJournalOptions {
    public RbfSegmentStoreOptions SegmentStoreOptions { get; init; } = new();
    public RefObjectStoreOptions RefObjectStoreOptions { get; init; } = new();
}
