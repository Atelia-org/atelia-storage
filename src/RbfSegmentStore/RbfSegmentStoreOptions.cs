using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.RbfSegmentStore;

public sealed class RbfSegmentStoreOptions {
    public RbfSegmentStoreLayout NewStoreLayout { get; init; } = RbfSegmentStoreLayout.Bucketed;
    public long SegmentSizeThresholdBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public int HistoricalReaderPoolCapacity { get; init; } = 32;
    public RbfCacheMode CacheMode { get; init; } = RbfCacheMode.Slots16;
    public bool RecoverActiveTailOnOpen { get; init; } = true;

    internal RbfSegmentStoreOptions Validated() {
        if (!Enum.IsDefined(NewStoreLayout)) { throw new ArgumentOutOfRangeException(nameof(NewStoreLayout), NewStoreLayout, "Unknown RBF segment store layout."); }

        if (SegmentSizeThresholdBytes <= RbfSegmentPath.RbfHeaderOnlyLength ||
            SegmentSizeThresholdBytes > SizedPtr.MaxOffset ||
            (SegmentSizeThresholdBytes & SizedPtr.AlignmentMask) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentSizeThresholdBytes),
                SegmentSizeThresholdBytes,
                $"Segment size threshold must be 4-byte aligned, greater than the " +
                $"header-only tail ({RbfSegmentPath.RbfHeaderOnlyLength}), and no " +
                $"greater than the maximum Frame start ({SizedPtr.MaxOffset})."
            );
        }

        if (HistoricalReaderPoolCapacity < 0) { throw new ArgumentOutOfRangeException(nameof(HistoricalReaderPoolCapacity), HistoricalReaderPoolCapacity, "Historical reader pool capacity must be non-negative."); }

        if (!Enum.IsDefined(CacheMode)) { throw new ArgumentOutOfRangeException(nameof(CacheMode), CacheMode, "Unknown RBF cache mode."); }

        return this;
    }
}
