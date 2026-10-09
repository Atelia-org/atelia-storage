using System.Runtime.ExceptionServices;
using System.Diagnostics.CodeAnalysis;

namespace Atelia.FrameStore.Internal.Runtime;

/// <summary>汇总失败也不能阻止剩余资源的一次性清理。</summary>
internal struct CleanupErrors {
    private Exception? _first;
    private List<Exception>? _errors;
    private bool _aggregationFailed;

    internal void Add(Exception error) {
        if (_first is null) {
            _first = error;
            return;
        }
        if (_aggregationFailed) { return; }
        try {
            _errors ??= [_first];
            _errors.Add(error);
        }
        catch {
            _aggregationFailed = true;
        }
    }

    internal readonly void ThrowIfAny() {
        if (_first is null) { return; }
        if (_errors is not null && !_aggregationFailed) {
            AggregateException aggregate;
            try { aggregate = new AggregateException(_errors); }
            catch { ThrowOriginal(_first); throw; }
            throw aggregate;
        }
        ThrowOriginal(_first);
    }

    [DoesNotReturn]
    private static void ThrowOriginal(Exception error) {
        // Capture itself can allocate. An allocation failure must not replace the observed cleanup error.
        ExceptionDispatchInfo? captured = null;
        try { captured = ExceptionDispatchInfo.Capture(error); }
        catch { }
        if (captured is not null) { captured.Throw(); }
        throw error;
    }
}
