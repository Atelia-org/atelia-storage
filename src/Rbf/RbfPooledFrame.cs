using System.Buffers;
using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>携带 ArrayPool buffer 的 RBF 帧。</summary>
/// <remarks>
/// 属性契约：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。
/// 调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。
/// 生命周期警告：Dispose 后 Payload 变为 dangling，不可再访问。
/// </remarks>
public sealed class RbfPooledFrame : IRbfFrame, IDisposable {
    private byte[]? _buffer;
    private readonly int _payloadOffset;
    private readonly int _payloadAndMetaLength;
    private readonly int _tailMetaLength;

    private ReadOnlySpan<byte> GetBufferSpan(int offset, int length) {
        var buffer = _buffer;
        if (buffer is null) { throw new ObjectDisposedException(nameof(RbfPooledFrame)); }

        return buffer.AsSpan(offset, length);
    }

    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> PayloadAndMeta => GetBufferSpan(_payloadOffset, _payloadAndMetaLength);

    /// <inheritdoc/>
    public int TailMetaLength => _tailMetaLength;

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>内部构造函数。</summary>
    internal RbfPooledFrame(byte[] buffer, SizedPtr ptr, uint tag, int payloadOffset, int payloadAndMetaLength, int tailMetaLength, bool isTombstone) {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _payloadOffset = payloadOffset;
        _payloadAndMetaLength = payloadAndMetaLength;
        _tailMetaLength = tailMetaLength;
        Ticket = ptr;
        Tag = tag;
        IsTombstone = isTombstone;
    }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose() {
        var buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null) { ArrayPool<byte>.Shared.Return(buf); }
    }
}
