using Atelia.Rbf.Internal;
using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf;

/// <summary>正向扫描枚举器。</summary>
/// <remarks>
/// 从文件头部之后的第一帧开始，读取 HeadLen 定位下一帧，并产出 <see cref="RbfFrameInfo"/>。
/// </remarks>
public ref struct RbfForwardEnumerator {
    private readonly RandomAccessReader _reader;
    private readonly long _dataTail;
    private readonly bool _showTombstone;
    private long _nextFrameOffset;
    private RbfFrameInfo _current;
    private AteliaError? _terminationError;

    internal RbfForwardEnumerator(RandomAccessReader reader, long dataStart, long dataTail, bool showTombstone) {
        _reader = reader;
        _nextFrameOffset = dataStart;
        _dataTail = dataTail;
        _showTombstone = showTombstone;
        _current = default;
        _terminationError = null;
    }

    /// <summary>当前帧元信息。</summary>
    public RbfFrameInfo Current => _current;

    /// <summary>迭代终止时的错误（如有）。</summary>
    public AteliaError? TerminationError => _terminationError;

    /// <summary>移动到下一帧（正向）。</summary>
    public bool MoveNext() {
        while (_nextFrameOffset < _dataTail) {
            var result = RbfReadImpl.ReadFrameInfoAt(_reader, _nextFrameOffset, _dataTail);
            if (!result.IsSuccess) {
                _terminationError = result.Error;
                return false;
            }

            _current = result.Value;
            _nextFrameOffset = _current.Ticket.Offset + _current.Ticket.Length + RbfLayout.FenceSize;

            if (!_showTombstone && _current.IsTombstone) { continue; }

            return true;
        }

        if (_nextFrameOffset != _dataTail) {
            _terminationError = new RbfFramingError(
                $"Forward scan cursor overshot data tail: cursor={_nextFrameOffset}, tail={_dataTail}.",
                RecoveryHint: "The frame length or tail offset may be corrupted."
            );
        }

        return false;
    }
}
