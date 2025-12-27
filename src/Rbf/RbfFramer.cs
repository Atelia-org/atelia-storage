using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>
/// RBF 帧写入器实现。
/// </summary>
/// <remarks>
/// <para>基于 <see cref="IBufferWriter{T}"/> 实现帧写入。</para>
/// <para>写入顺序: Fence → HeadLen → FrameTag → Payload → FrameStatus → TailLen → CRC32C</para>
/// <para><b>[F-CRC32C-COVERAGE]</b>: CRC 覆盖 FrameTag + Payload + FrameStatus + TailLen</para>
/// </remarks>
public sealed class RbfFramer : IRbfFramer {
    private readonly IBufferWriter<byte> _output;
    private long _position;
    private bool _hasOpenBuilder;

    private ChunkedReservableWriter? _activeChunkedWriter;
    private CrcPositionTrackingBufferWriter? _activeInnerWriter;

    /// <summary>
    /// 创建 RbfFramer。
    /// </summary>
    /// <param name="output">底层缓冲区写入器。</param>
    /// <param name="startPosition">起始位置（用于计算 Address64）。</param>
    /// <param name="writeGenesis">是否写入 Genesis Fence。</param>
    public RbfFramer(IBufferWriter<byte> output, long startPosition = 0, bool writeGenesis = true) {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _position = startPosition;
        _hasOpenBuilder = false;
        _activeChunkedWriter = null;
        _activeInnerWriter = null;

        if (writeGenesis) {
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
    internal IBufferWriter<byte> PayloadWriter => throw new InvalidOperationException("Use BeginFrame streaming writer.");

    /// <inheritdoc/>
    public Address64 Append(FrameTag tag, ReadOnlySpan<byte> payload) {
        if (_hasOpenBuilder) { throw new InvalidOperationException("Cannot Append while a RbfFrameBuilder is open."); }

        // 写入帧并立即提交
        var frameStart = _position;
        WriteFrameComplete(tag, payload, FrameStatus.CreateValid(1)); // StatusLen will be recalculated
        return Address64.FromOffset(frameStart);
    }

    /// <inheritdoc/>
    public RbfFrameBuilder BeginFrame(FrameTag tag) {
        if (_hasOpenBuilder) { throw new InvalidOperationException("A RbfFrameBuilder is already open. Complete it before starting a new one."); }

        _hasOpenBuilder = true;

        // Streaming builder powered by ChunkedReservableWriter (reservation + zero-I/O abort).
        // The actual bytes are only flushed to _output after the headLen reservation is committed.
        var frameStart = _position;

        _activeInnerWriter = new CrcPositionTrackingBufferWriter(this, _output);
        _activeChunkedWriter = new ChunkedReservableWriter(_activeInnerWriter);

        // Reserve HeadLen (NOT covered by CRC)
        var headLenSpan = _activeChunkedWriter.ReserveSpan(4, out var headLenToken, tag: "Rbf.HeadLen");

        // Write FrameTag (covered by CRC)
        Span<byte> tagSpan = _activeChunkedWriter.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(tagSpan, tag.Value);
        _activeChunkedWriter.Advance(4);

        return new RbfFrameBuilder(this, frameStart, tag, _activeChunkedWriter, headLenToken, headLenSpan);
    }

    /// <inheritdoc/>
    public void Flush() {
        // IBufferWriter 没有 Flush 概念，由上层控制
        // 对于 ArrayBufferWriter，数据已在内存中
    }

    /// <summary>
    /// 提交帧（供 RbfFrameBuilder 调用）。
    /// </summary>
    internal Address64 CommitFrame(long frameStart, FrameTag tag, FrameStatus status) {
        throw new NotSupportedException("Non-streaming frame builder is no longer supported; use BeginFrame streaming writer.");
    }

    internal Address64 CommitFrameStreaming(
        long frameStart,
        FrameTag tag,
        ChunkedReservableWriter writer,
        int headLenReservationToken,
        Span<byte> headLenSpan,
        FrameStatus status
    ) {
        if (!_hasOpenBuilder) { throw new InvalidOperationException("No open builder."); }
        if (!ReferenceEquals(writer, _activeChunkedWriter)) { throw new InvalidOperationException("Mismatched active writer."); }

        // Ensure payload-level reservations are all committed before finalizing frame.
        // The only allowed pending reservation at this point is the HeadLen reservation.
        if (writer.PendingReservationCount != 1) { throw new InvalidOperationException($"Uncommitted payload reservations exist: {writer.PendingReservationCount - 1}"); }

        // Layout so far: HeadLen(4 reserved) + FrameTag(4) + Payload(N)
        var payloadLenLong = writer.WrittenLength - 8;
        if (payloadLenLong < 0 || payloadLenLong > int.MaxValue) { throw new InvalidOperationException("Payload length out of range."); }

        int payloadLen = (int)payloadLenLong;
        int statusLen = RbfLayout.CalculateStatusLength(payloadLen);
        int frameLen = RbfLayout.CalculateFrameLength(payloadLen);

        // FrameStatus (covered by CRC)
        var actualStatus = status.IsTombstone
            ? FrameStatus.CreateTombstone(statusLen)
            : FrameStatus.CreateValid(statusLen);
        byte statusByte = actualStatus.Value;
        Span<byte> statusSpan = writer.GetSpan(statusLen);
        statusSpan.Slice(0, statusLen).Fill(statusByte);
        writer.Advance(statusLen);

        // TailLen (covered by CRC)
        Span<byte> tailSpan = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(tailSpan, (uint)frameLen);
        writer.Advance(4);

        // Backfill HeadLen and commit the reservation (this releases buffered bytes to the underlying writer).
        BinaryPrimitives.WriteUInt32LittleEndian(headLenSpan, (uint)frameLen);
        writer.Commit(headLenReservationToken);

        // At this point, the inner writer has observed and CRC'd FrameTag + Payload + FrameStatus + TailLen.
        uint crc = _activeInnerWriter!.CrcFinal;

        // Write CRC32C (NOT covered by CRC)
        var crcOut = _output.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(crcOut, crc);
        _output.Advance(4);
        _position += 4;

        // Trailing Fence
        var fenceOut = _output.GetSpan(RbfConstants.FenceLength);
        RbfConstants.FenceBytes.CopyTo(fenceOut);
        _output.Advance(RbfConstants.FenceLength);
        _position += RbfConstants.FenceLength;

        // Cleanup per-frame resources.
        _activeChunkedWriter?.Dispose();
        _activeChunkedWriter = null;
        _activeInnerWriter = null;

        return Address64.FromOffset(frameStart);
    }

    internal void AbortFrameStreaming(ChunkedReservableWriter writer) {
        if (_activeChunkedWriter is null) { return; }
        if (!ReferenceEquals(writer, _activeChunkedWriter)) { return; }

        // Drop buffered data without flushing to the underlying writer.
        _activeChunkedWriter.Dispose();
        _activeChunkedWriter = null;
        _activeInnerWriter = null;
    }

    /// <summary>
    /// 结束 Builder（释放锁）。
    /// </summary>
    internal void EndBuilder() {
        _hasOpenBuilder = false;
    }

    /// <summary>
    /// 写入完整的帧（含 Fence）。
    /// </summary>
    private void WriteFrameComplete(FrameTag tag, ReadOnlySpan<byte> payload, FrameStatus status) {
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
        for (int i = 0; i < statusLen; i++) {
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
    private void WriteFence() {
        var span = _output.GetSpan(RbfConstants.FenceLength);
        RbfConstants.FenceBytes.CopyTo(span);
        _output.Advance(RbfConstants.FenceLength);
        _position += RbfConstants.FenceLength;
    }

    private sealed class CrcPositionTrackingBufferWriter : IBufferWriter<byte> {
        private readonly RbfFramer _framer;
        private readonly IBufferWriter<byte> _inner;

        private Memory<byte> _lastMemory;
        private bool _hasLast;
        private long _frameRelativeOffset;
        private uint _crcState;

        public CrcPositionTrackingBufferWriter(RbfFramer framer, IBufferWriter<byte> inner) {
            _framer = framer;
            _inner = inner;
            _lastMemory = default;
            _hasLast = false;
            _frameRelativeOffset = 0;
            _crcState = RbfCrc.Begin();
        }

        public uint CrcFinal => RbfCrc.End(_crcState);

        public void Advance(int count) {
            if (!_hasLast) { throw new InvalidOperationException("Advance called without a prior GetSpan/GetMemory."); }
            if (count < 0 || count > _lastMemory.Length) { throw new ArgumentOutOfRangeException(nameof(count)); }

            if (count > 0) {
                // CRC covers FrameTag + Payload + FrameStatus + TailLen.
                // The first 4 bytes (HeadLen) are excluded.
                var written = _lastMemory.Span.Slice(0, count);

                if (_frameRelativeOffset < 4) {
                    int skip = (int)Math.Min(4 - _frameRelativeOffset, count);
                    written = written.Slice(skip);
                }

                if (!written.IsEmpty) {
                    _crcState = RbfCrc.Update(_crcState, written);
                }

                _frameRelativeOffset += count;
            }

            _inner.Advance(count);
            _framer._position += count;
            _hasLast = false;
            _lastMemory = default;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) {
            if (_hasLast) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() before requesting another buffer."); }
            var mem = _inner.GetMemory(sizeHint);
            _lastMemory = mem;
            _hasLast = true;
            return mem;
        }

        public Span<byte> GetSpan(int sizeHint = 0) {
            return GetMemory(sizeHint).Span;
        }
    }
}
