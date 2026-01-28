using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>逆向扫描序列（duck-typed 枚举器，支持 foreach）。</summary>
/// <remarks>
/// 设计说明：返回 ref struct 而非 IEnumerable，因为 RbfFrameInfo 是只读结构体。
/// 上层通过 foreach 消费，不依赖 LINQ。
/// 规范引用：design-draft.md §5
/// </remarks>
public ref struct RbfReverseSequence {
    private readonly SafeFileHandle _handle;
    private readonly long _dataTail;
    private readonly bool _showTombstone;

    /// <summary>初始化逆向扫描序列。</summary>
    /// <param name="handle">RBF 文件句柄。</param>
    /// <param name="dataTail">扫描起始位置（文件逻辑尾部）。</param>
    /// <param name="showTombstone">是否包含墓碑帧。</param>
    internal RbfReverseSequence(SafeFileHandle handle, long dataTail, bool showTombstone) {
        _handle = handle;
        _dataTail = dataTail;
        _showTombstone = showTombstone;
    }

    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfReverseEnumerator GetEnumerator() {
        return new RbfReverseEnumerator(_handle, _dataTail, _showTombstone);
    }
}
