using System.Buffers;
using System.Threading;
using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>携带 ArrayPool buffer 的 TailMeta 预览结果（L2 信任）。</summary>
/// <remarks>
/// 调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。
/// 生命周期警告：Dispose 后 TailMeta 变为 dangling，不可再访问。
/// Buffer 租用：只租 TailMetaLength 大小，不租整帧大小。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// </remarks>
public sealed class RbfPooledTailMeta : IDisposable, IRbfTailMeta {
    private byte[]? _buffer;
    private readonly int _tailMetaLength;

    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">对象已 Dispose（且原有 buffer）。</exception>
    public ReadOnlySpan<byte> TailMeta {
        get {
            // 无 buffer 场景（TailMetaLength = 0）：始终返回空 Span
            if (_tailMetaLength == 0) { return ReadOnlySpan<byte>.Empty; }

            var buffer = _buffer;
            if (buffer is null) { throw new ObjectDisposedException(nameof(RbfPooledTailMeta)); }

            return buffer.AsSpan(0, _tailMetaLength);
        }
    }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>内部构造函数（供 RbfReadImpl 使用）。</summary>
    internal RbfPooledTailMeta(byte[] buffer, SizedPtr ticket, uint tag, int tailMetaLength, bool isTombstone) {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        if ((uint)tailMetaLength > (uint)buffer.Length) { throw new ArgumentOutOfRangeException(nameof(tailMetaLength)); }
        _tailMetaLength = tailMetaLength;
        Ticket = ticket;
        Tag = tag;
        IsTombstone = isTombstone;
    }

    /// <summary>内部构造函数（TailMetaLength = 0 时无需 buffer）。</summary>
    internal RbfPooledTailMeta(SizedPtr ticket, uint tag, bool isTombstone) {
        _buffer = null;
        _tailMetaLength = 0;
        Ticket = ticket;
        Tag = tag;
        IsTombstone = isTombstone;
    }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose() {
        var buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null) { ArrayPool<byte>.Shared.Return(buf); }
    }
}
