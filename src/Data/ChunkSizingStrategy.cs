using System;
using System.Numerics;

namespace Atelia.Data;

/// <summary>
/// Chunk 尺寸策略：封装最小/最大块大小和自适应增长逻辑
/// </summary>
internal sealed class ChunkSizingStrategy {
    private readonly int _minChunkSize;
    private readonly int _maxChunkSize;
    private int _currentTargetSize;
    private const double GrowthFactor = 2.0;

    /// <summary>
    /// 构造 Chunk 尺寸策略
    /// </summary>
    public ChunkSizingStrategy(int minSize, int maxSize) {
        if (minSize < 1024) { throw new ArgumentException("MinChunkSize must be >= 1024", nameof(minSize)); }
        if (maxSize < minSize) { throw new ArgumentException("MaxChunkSize must be >= MinChunkSize", nameof(maxSize)); }

        _minChunkSize = minSize;
        _maxChunkSize = maxSize;
        _currentTargetSize = minSize;
    }

    /// <summary>
    /// 根据 sizeHint 计算实际分配的 chunk 大小
    /// </summary>
    public int ComputeChunkSize(int sizeHint) {
        sizeHint = Math.Max(sizeHint, 1);
        int required = Math.Max(sizeHint, _minChunkSize);

        if (required > _maxChunkSize) { return required; /* oversize direct rent */ }

        int candidate = Math.Max(required, _currentTargetSize);

        // 仅对 ≤1GB 的值执行 RoundUp，避免 uint→int 溢出
        // (RoundUpToPowerOf2 对 >1GB 的输入返回 0x80000000，转 int 为负数)
        const int SafeRoundUpLimit = 1 << 30;
        if (candidate <= SafeRoundUpLimit) {
            candidate = (int)BitOperations.RoundUpToPowerOf2((uint)candidate);
        }

        return Math.Min(candidate, _maxChunkSize);
    }

    /// <summary>
    /// 通知已创建指定大小的 chunk，更新自适应增长目标
    /// </summary>
    public void NotifyChunkCreated(int actualSize) {
        if (actualSize < _maxChunkSize && _currentTargetSize < _maxChunkSize) {
            long next = (long)(_currentTargetSize * GrowthFactor);
            _currentTargetSize = (int)Math.Min(next, _maxChunkSize);
        }
    }
}
