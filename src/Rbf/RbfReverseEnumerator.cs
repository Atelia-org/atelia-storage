using Atelia.Rbf.Internal;
using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf;

/// <summary>逆向扫描枚举器。</summary>
/// <remarks>
/// 从 <c>dataTail</c> 开始，调用 <c>ReadTrailerBefore</c> 逐帧往回迭代。
/// 当 <c>dataTail</c> 到达 <c>MinValidOffset</c> 或 <c>ReadTrailerBefore</c> 失败时停止。
/// 规范引用：design-draft.md §5
/// </remarks>
public ref struct RbfReverseEnumerator {
    private readonly RandomAccessReader _reader;
    private readonly bool _showTombstone;
    private long _dataTail;
    private RbfFrameInfo _current;
    private AteliaError? _terminationError;

    /// <summary>初始化逆向扫描枚举器。</summary>
    /// <param name="handle">RBF 文件句柄。</param>
    /// <param name="dataTail">扫描起始位置（文件逻辑尾部）。</param>
    /// <param name="showTombstone">是否包含墓碑帧。</param>
    internal RbfReverseEnumerator(RandomAccessReader reader, long dataTail, bool showTombstone) {
        _reader = reader;
        _dataTail = dataTail;
        _showTombstone = showTombstone;
        _current = default;
        _terminationError = null;
    }

    /// <summary>当前帧元信息。</summary>
    public RbfFrameInfo Current => _current;

    /// <summary>迭代终止时的错误（如有）。</summary>
    /// <remarks>
    /// 正常到达文件头部时为 <c>null</c>。
    /// 遇到损坏帧时为非空，包含具体错误信息。
    /// </remarks>
    public AteliaError? TerminationError => _terminationError;

    /// <summary>移动到下一帧（逆向）。</summary>
    /// <returns>成功移动返回 <c>true</c>，到达文件头或遇到错误返回 <c>false</c>。</returns>
    public bool MoveNext() {
        while (_dataTail >= RbfLayout.MinFirstFrameFenceEnd) {
            var result = RbfReadImpl.ReadTrailerBefore(_reader, _dataTail);

            if (result.IsFailure) {
                _terminationError = result.Error;
                return false;
            }

            _current = result.Value;
            _dataTail = _current.Ticket.Offset;  // 下一次从当前帧起始位置继续

            // 过滤墓碑帧（如果 showTombstone = false）
            if (!_showTombstone && _current.IsTombstone) { continue; }

            return true;
        }
        return false;
    }
}
