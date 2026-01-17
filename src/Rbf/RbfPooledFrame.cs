using System.Buffers;
using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>
/// 携带 ArrayPool buffer 的 RBF 帧。
/// </summary>
/// <remarks>
/// <para><b>属性契约</b>：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。</para>
/// <para>调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。</para>
/// <para><b>生命周期警告</b>：Dispose 后 Payload 变为 dangling，不可再访问。</para>
/// </remarks>
public sealed class RbfPooledFrame : IRbfFrame, IDisposable {
    private byte[]? _buffer;
    private readonly int _payloadOffset;
    private readonly int _payloadLength;

    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Payload => _buffer is not null
        ? _buffer.AsSpan(_payloadOffset, _payloadLength)
        : throw new ObjectDisposedException(nameof(RbfPooledFrame));

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>
    /// 内部构造函数。
    /// </summary>
    internal RbfPooledFrame(byte[] buffer, SizedPtr ptr, uint tag, int payloadOffset, int payloadLength, bool isTombstone) {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _payloadOffset = payloadOffset;
        _payloadLength = payloadLength;
        Ticket = ptr;
        Tag = tag;
        IsTombstone = isTombstone;
    }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose() {
        var buf = _buffer;
        if (buf is not null) {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
