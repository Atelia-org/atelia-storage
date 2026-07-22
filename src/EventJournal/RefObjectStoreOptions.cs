using Atelia.Rbf;

namespace Atelia.EventJournal;

public sealed class RefObjectStoreOptions {
    public long SegmentSizeThresholdBytes { get; init; } = 64L * 1024 * 1024;
    public RbfCacheMode CacheMode { get; init; } = RbfCacheMode.Slots16;
    public bool RecoverActiveTailOnOpen { get; init; } = true;

    internal RefObjectStoreOptions Validated() {
        if (SegmentSizeThresholdBytes <= 0 || (SegmentSizeThresholdBytes & 3) != 0) { throw new ArgumentOutOfRangeException(nameof(SegmentSizeThresholdBytes), SegmentSizeThresholdBytes, "Ref object segment size threshold must be positive and 4-byte aligned."); }

        if (!Enum.IsDefined(CacheMode)) { throw new ArgumentOutOfRangeException(nameof(CacheMode), CacheMode, "Unknown RBF cache mode."); }

        return this;
    }
}
