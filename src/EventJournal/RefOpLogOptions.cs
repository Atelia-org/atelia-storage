using Atelia.Rbf;

namespace Atelia.EventJournal;

public sealed class RefOpLogOptions {
    public RbfCacheMode CacheMode { get; init; } = RbfCacheMode.Slots16;
    public bool RecoverActiveTailOnOpen { get; init; } = true;

    internal RefOpLogOptions Validated() {
        if (!Enum.IsDefined(CacheMode)) { throw new ArgumentOutOfRangeException(nameof(CacheMode), CacheMode, "Unknown RBF cache mode."); }
        return this;
    }
}
