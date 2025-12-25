using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.Rbf;

/// <summary>
/// RBF 帧写入器实现。
/// </summary>
/// <remarks>
/// <para>基于 <see cref="IBufferWriter{T}"/> 实现帧写入。</para>
/// <para>写入顺序: Fence → HeadLen → FrameTag → Payload → FrameStatus → TailLen → CRC32C</para>
/// <para><b>[F-CRC32C-COVERAGE]</b>: CRC 覆盖 FrameTag + Payload + FrameStatus + TailLen</para>
/// </remarks>
public sealed class RbfFramer : IRbfFramer
{
    private readonly IBufferWriter<byte> _output;
    private readonly PayloadBufferWriter _payloadWriter;
    private long _position;
    private bool _hasOpenBuilder;

    /// <summary>
    /// 创建 RbfFramer。
    /// </summary>
    /// <param name="output">底层缓冲区写入器。</param>
    /// <param name="startPosition">起始位置（用于计算 Address64）。</param>
    /// <param name="writeGenesis">是否写入 Genesis Fence。</param>
    public RbfFramer(IBufferWriter<byte> output, long startPosition = 0, bool writeGenesis = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _payloadWriter = new PayloadBufferWriter();
        _position = startPosition;
        _hasOpenBuilder = false;

        if (writeGenesis)
        {
            WriteFence();
        }
    }

    /// <summary>
    /// 获取当前写入位置。
    /// </summary>
    public long Position => _position;

    /// <summary>
    /// 内部 Payload 写入器（供 RbfFrameBuilder 使用）。
    /// </summary>
    internal IBufferWriter<byte> PayloadWriter => _payloadWriter;

    /// <inheritdoc/>
    public Address64 Append(FrameTag tag, ReadOnlySpan<byte> payload)
    {
        if (_hasOpenBuilder)
            throw new InvalidOperationException("Cannot Append while a RbfFrameBuilder is open.");

        // 写入帧并立即提交
        var frameStart = _position;
        WriteFrameComplete(tag, payload, FrameStatus.CreateValid(1)); // StatusLen will be recalculated
        return Address64.FromOffset(frameStart);
    }

    /// <inheritdoc/>
    public RbfFrameBuilder BeginFrame(FrameTag tag)
    {
        if (_hasOpenBuilder)
            throw new InvalidOperationException("A RbfFrameBuilder is already open. Complete it before starting a new one.");

        _hasOpenBuilder = true;
        _payloadWriter.Reset();

        // 记录帧起始位置（当前位置将是 HeadLen 的位置）
        var frameStart = _position;

        return new RbfFrameBuilder(this, frameStart, tag);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        // IBufferWriter 没有 Flush 概念，由上层控制
        // 对于 ArrayBufferWriter，数据已在内存中
    }

    /// <summary>
    /// 提交帧（供 RbfFrameBuilder 调用）。
    /// </summary>
    internal Address64 CommitFrame(long frameStart, FrameTag tag, FrameStatus status)
    {
        // 获取 payload 数据
        var payload = _payloadWriter.WrittenSpan;
        WriteFrameComplete(tag, payload, status);
        return Address64.FromOffset(frameStart);
    }

    /// <summary>
    /// 结束 Builder（释放锁）。
    /// </summary>
    internal void EndBuilder()
    {
        _hasOpenBuilder = false;
        _payloadWriter.Reset();
    }

    /// <summary>
    /// 写入完整的帧（含 Fence）。
    /// </summary>
    private void WriteFrameComplete(FrameTag tag, ReadOnlySpan<byte> payload, FrameStatus status)
    {
        int payloadLen = payload.Length;
        int statusLen = RbfLayout.CalculateStatusLength(payloadLen);
        int frameLen = RbfLayout.CalculateFrameLength(payloadLen);

        // [F-FRAME-LAYOUT]:
        // HeadLen(4) + FrameTag(4) + Payload(N) + FrameStatus(1-4) + TailLen(4) + CRC32C(4)
        // 后面还要写 Fence(4)
        int totalBytes = frameLen + RbfConstants.FenceLength;

        var span = _output.GetSpan(totalBytes);
        int offset = 0;

        // 1. HeadLen (u32 LE)
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)frameLen);
        offset += 4;

        // 2. FrameTag (u32 LE)
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], tag.Value);
        offset += 4;

        // 3. Payload
        payload.CopyTo(span[offset..]);
        offset += payloadLen;

        // 4. FrameStatus (1-4 bytes, all same value)
        // 根据 status 的 IsTombstone 和 statusLen 创建正确的位域值
        var actualStatus = status.IsTombstone
            ? FrameStatus.CreateTombstone(statusLen)
            : FrameStatus.CreateValid(statusLen);
        var statusByte = actualStatus.Value;
        for (int i = 0; i < statusLen; i++)
        {
            span[offset++] = statusByte;
        }

        // 5. TailLen (u32 LE) = HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)frameLen);
        offset += 4;

        // 6. CRC32C: 覆盖 FrameTag + Payload + FrameStatus + TailLen
        // 即从 offset=4 到 offset=4+4+payloadLen+statusLen+4 = 8+payloadLen+statusLen+4
        int crcStart = 4; // FrameTag 起点
        int crcLen = 4 + payloadLen + statusLen + 4; // FrameTag + Payload + FrameStatus + TailLen
        var crcData = span.Slice(crcStart, crcLen);
        uint crc = RbfCrc.Compute(crcData);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], crc);
        offset += 4;

        // 7. Trailing Fence
        RbfConstants.FenceBytes.CopyTo(span[offset..]);
        offset += RbfConstants.FenceLength;

        _output.Advance(offset);
        _position += offset;
    }

    /// <summary>
    /// 写入 Fence。
    /// </summary>
    private void WriteFence()
    {
        var span = _output.GetSpan(RbfConstants.FenceLength);
        RbfConstants.FenceBytes.CopyTo(span);
        _output.Advance(RbfConstants.FenceLength);
        _position += RbfConstants.FenceLength;
    }

    /// <summary>
    /// 内部 Payload 缓冲区写入器。
    /// </summary>
    private sealed class PayloadBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[256];
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Reset()
        {
            _written = 0;
        }

        public void Advance(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint <= 0) sizeHint = 1;
            int required = _written + sizeHint;
            if (required > _buffer.Length)
            {
                int newSize = Math.Max(_buffer.Length * 2, required);
                Array.Resize(ref _buffer, newSize);
            }
        }
    }
}
