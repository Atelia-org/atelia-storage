using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf;

/// <summary>正向扫描序列（duck-typed 枚举器，支持 foreach）。</summary>
/// <remarks>
/// 设计说明：返回 ref struct 而非 IEnumerable，上层通过 foreach 消费，不依赖 LINQ。
/// </remarks>
public ref struct RbfForwardSequence {
    private readonly RandomAccessReader _reader;
    private readonly long _dataStart;
    private readonly long _dataTail;
    private readonly bool _showTombstone;

    internal RbfForwardSequence(RandomAccessReader reader, long dataStart, long dataTail, bool showTombstone) {
        _reader = reader;
        _dataStart = dataStart;
        _dataTail = dataTail;
        _showTombstone = showTombstone;
    }

    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfForwardEnumerator GetEnumerator() {
        return new RbfForwardEnumerator(_reader, _dataStart, _dataTail, _showTombstone);
    }
}
