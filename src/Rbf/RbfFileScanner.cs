using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>
/// RBF 文件扫描器（file-backed 实现）。
/// </summary>
/// <remarks>
/// <para><b>M1 阶段</b>: 所有操作都使用 RandomAccess 直接从文件读取，不读取整个文件到内存。</para>
/// <para>ScanReverse 使用分块 CRC 计算支持大 payload。</para>
/// </remarks>
public sealed class RbfFileScanner : IRbfScanner, IDisposable {
    /// <summary>
    /// CRC 分块计算时每次读取的缓冲区大小（64KB）。
    /// </summary>
    private const int CrcChunkSize = 64 * 1024;
    private readonly string _filePath;
    private readonly FileStream _fileStream;
    private readonly SafeFileHandle _handle;
    private readonly long _fileLength;
    private bool _disposed;

    public RbfFileScanner(string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;

        if (!File.Exists(filePath)) {
            // 空文件情况：创建一个空的内存流模拟
            _fileStream = null!;
            _handle = null!;
            _fileLength = 0;
            return;
        }

        _fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 0, // 无缓冲，我们用 RandomAccess
            FileOptions.RandomAccess
        );
        _handle = _fileStream.SafeFileHandle;
        _fileLength = _fileStream.Length;
    }

    /// <summary>
    /// 文件长度。
    /// </summary>
    public long FileLength => _fileLength;

    /// <inheritdoc/>
    public bool TryReadAt(Address64 address, out RbfFrame frame) {
        frame = default;

        // 空文件或无效地址
        if (_fileLength == 0 || address.IsNull) { return false; }

        long frameStart = (long)address.Value;

        // [F-ADDRESS64-ALIGNMENT]: 必须 4B 对齐
        if (!RbfLayout.Is4ByteAligned(frameStart)) { return false; }

        // 验证边界：frameStart 必须在 Genesis Fence 之后
        if (frameStart < RbfConstants.FenceLength) { return false; }

        return TryReadFrameAt(frameStart, out frame);
    }

    /// <summary>
    /// 在指定位置尝试读取帧（file-backed 实现，无整帧分配）。
    /// </summary>
    /// <remarks>
    /// <para>复用 <see cref="TryValidateFrameFileBacked"/> 进行分块 CRC 校验，避免大帧整体分配。</para>
    /// </remarks>
    private bool TryReadFrameAt(long frameStart, out RbfFrame frame) {
        frame = default;

        // 1) 检查前置 Fence
        long prevFencePos = frameStart - RbfConstants.FenceLength;
        if (prevFencePos < 0) { return false; }

        if (!IsFenceAt(prevFencePos)) { return false; }

        // 2) 读取 HeadLen (4 bytes)
        if (!TryReadUInt32(frameStart, out uint headLen)) { return false; }

        // 验证 HeadLen
        if ((headLen % 4) != 0 || headLen < RbfLayout.MinFrameLength) { return false; }

        // 3) 检查帧是否在边界内
        long recordEnd = frameStart + headLen;
        if (recordEnd + RbfConstants.FenceLength > _fileLength) { return false; }

        // 4) 检查尾部 Fence
        if (!IsFenceAt(recordEnd)) { return false; }

        // 5) 读取 TailLen 和 CRC
        if (!TryReadUInt32(recordEnd - 8, out uint tailLen)) { return false; }
        if (!TryReadUInt32(recordEnd - 4, out uint storedCrc)) { return false; }

        // 验证 HeadLen == TailLen
        if (headLen != tailLen) { return false; }

        // 6) 使用 file-backed 验证（分块 CRC，无整帧分配）
        return TryValidateFrameFileBacked(frameStart, headLen, storedCrc, out frame);
    }

    /// <inheritdoc/>
    public IEnumerable<RbfFrame> ScanReverse() {
        // File-backed 逆向扫描：从尾部向前扫描 Fence，验证帧完整性
        var results = new List<RbfFrame>();
        ScanReverseInternal(results);
        return results;
    }

    /// <summary>
    /// 内部逆向扫描实现（file-backed）。
    /// </summary>
    /// <remarks>
    /// <para><b>[R-REVERSE-SCAN-ALGORITHM]</b>: 从文件尾部向前扫描 Fence，验证帧完整性。</para>
    /// <para><b>[R-RESYNC-BEHAVIOR]</b>: 校验失败时按 4B 步长向前搜索。</para>
    /// </remarks>
    private void ScanReverseInternal(List<RbfFrame> results) {
        // 1) 若 fileLength < GenesisLen: 返回空
        if (_fileLength < RbfConstants.FenceLength) { return; }

        // 2) fencePos = alignDown4(fileLength - FenceLen)
        long fencePos = RbfLayout.AlignDown4(_fileLength - RbfConstants.FenceLength);

        // 3) while fencePos >= 0
        while (fencePos >= 0) {
            // a) 若 fencePos == 0: 停止（到达 Genesis Fence）
            if (fencePos == 0) { break; }

            // b) 若 bytes[fencePos..fencePos+4] != FenceValue: Resync
            if (!IsFenceAt(fencePos)) {
                fencePos -= 4;
                continue;
            }

            // c) 现在 fencePos 指向一个 Fence
            long recordEnd = fencePos;

            // 若 recordEnd < GenesisLen + MinFrameBytes (20): 不足以容纳最小帧
            if (recordEnd < RbfConstants.FenceLength + RbfLayout.MinFrameLength) {
                fencePos -= 4;
                continue;
            }

            // 读取 tailLen @ (recordEnd - 8)
            if (!TryReadUInt32(recordEnd - 8, out uint tailLen)) {
                fencePos -= 4;
                continue;
            }

            // 读取 storedCrc @ (recordEnd - 4)
            if (!TryReadUInt32(recordEnd - 4, out uint storedCrc)) {
                fencePos -= 4;
                continue;
            }

            // frameStart = recordEnd - tailLen
            long frameStart = recordEnd - tailLen;

            // 若 frameStart < GenesisLen 或 frameStart % 4 != 0
            if (frameStart < RbfConstants.FenceLength || !RbfLayout.Is4ByteAligned(frameStart)) {
                fencePos -= 4;
                continue;
            }

            // prevFencePos = frameStart - FenceLen
            long prevFencePos = frameStart - RbfConstants.FenceLength;

            // 若 prevFencePos < 0 或 bytes[prevFencePos..prevFencePos+4] != FenceValue
            if (prevFencePos < 0 || !IsFenceAt(prevFencePos)) {
                fencePos -= 4;
                continue;
            }

            // 读取 headLen @ frameStart
            if (!TryReadUInt32(frameStart, out uint headLen)) {
                fencePos -= 4;
                continue;
            }

            // 若 headLen != tailLen 或 headLen % 4 != 0 或 headLen < 20
            if (headLen != tailLen || (headLen % 4) != 0 || headLen < RbfLayout.MinFrameLength) {
                fencePos -= 4;
                continue;
            }

            // 验证帧并计算 CRC
            if (!TryValidateFrameFileBacked(frameStart, headLen, storedCrc, out var frame)) {
                fencePos -= 4;
                continue;
            }

            // 输出有效帧
            results.Add(frame);

            // fencePos = prevFencePos
            fencePos = prevFencePos;
        }
    }

    /// <summary>
    /// 验证帧的完整性（file-backed 实现，支持大 payload 分块 CRC）。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-CRC32C-COVERAGE]</b>: CRC 覆盖范围 = FrameTag + Payload + FrameStatus + TailLen</para>
    /// <para>使用 <see cref="RbfCrc.Begin"/>/<see cref="RbfCrc.Update"/>/<see cref="RbfCrc.End"/> 分块计算 CRC。</para>
    /// </remarks>
    private bool TryValidateFrameFileBacked(long frameStart, uint headLen, uint storedCrc, out RbfFrame frame) {
        frame = default;

        // HeadLen = 16 + PayloadLen + StatusLen
        // PayloadLen + StatusLen = HeadLen - 16
        int payloadPlusStatus = (int)headLen - 16;
        if (payloadPlusStatus % 4 != 0 || payloadPlusStatus < 1) { return false; }

        long recordEnd = frameStart + headLen;

        // 读取 FrameTag @ frameStart + 4
        if (!TryReadUInt32(frameStart + RbfLayout.FrameTagOffset, out uint frameTag)) { return false; }

        // 读取 FrameStatus 的最后一个字节（TailLen 之前，即 recordEnd - 9）
        if (recordEnd - 9 < frameStart + RbfLayout.PayloadOffset) { return false; }
        if (!TryReadByte(recordEnd - 9, out byte statusByte)) { return false; }

        var status = FrameStatus.FromByte(statusByte);

        // [F-FRAMESTATUS-VALUES]: 检查是否为 MVP 合法值（保留位为 0）
        if (!status.IsMvpValid) { return false; }

        int statusLen = status.StatusLen;
        int payloadLen = payloadPlusStatus - statusLen;

        // 验证 PayloadLen >= 0
        if (payloadLen < 0) { return false; }

        // 验证 StatusLen 与公式一致
        int expectedStatusLen = RbfLayout.CalculateStatusLength(payloadLen);
        if (expectedStatusLen != statusLen) { return false; }

        // [F-FRAMESTATUS-FILL]: 验证所有 FrameStatus 字节相同
        long statusOffset = frameStart + RbfLayout.PayloadOffset + payloadLen;
        if (!ValidateStatusFill(statusOffset, statusLen, statusByte)) { return false; }

        // 验证 CRC32C（分块计算）
        // CRC 覆盖范围: [frameStart+4, recordEnd-4) = FrameTag + Payload + FrameStatus + TailLen
        int crcLen = 4 + payloadLen + statusLen + 4; // FrameTag + Payload + FrameStatus + TailLen
        if (!VerifyCrcChunked(frameStart + 4, crcLen, storedCrc)) { return false; }

        // 构建 RbfFrame
        frame = new RbfFrame(
            FileOffset: frameStart,
            FrameTag: frameTag,
            PayloadOffset: frameStart + RbfLayout.PayloadOffset,
            PayloadLength: payloadLen,
            Status: status
        );

        return true;
    }

    /// <summary>
    /// 验证 FrameStatus 填充字节是否全部相同。
    /// </summary>
    private bool ValidateStatusFill(long statusOffset, int statusLen, byte expectedByte) {
        // 对于小的 statusLen (1-4)，直接读取验证
        Span<byte> statusBuf = stackalloc byte[4]; // statusLen 最大为 4
        var slice = statusBuf.Slice(0, statusLen);
        if (!TryReadExact(statusOffset, slice)) { return false; }

        for (int i = 0; i < statusLen; i++) {
            if (slice[i] != expectedByte) { return false; }
        }

        return true;
    }

    /// <summary>
    /// 分块计算并验证 CRC32C。
    /// </summary>
    /// <remarks>
    /// 对于大 payload，使用 64KB 分块读取并增量计算 CRC，避免一次性分配大数组。
    /// </remarks>
    private bool VerifyCrcChunked(long offset, int length, uint expectedCrc) {
        if (length <= 0) { return false; }

        // 对于小数据（<= 64KB），直接读取
        if (length <= CrcChunkSize) {
            Span<byte> buf = length <= 1024 ? stackalloc byte[length] : new byte[length];
            if (!TryReadExact(offset, buf)) { return false; }
            return RbfCrc.Compute(buf) == expectedCrc;
        }

        // 大数据：分块增量计算
        byte[] chunkBuf = new byte[CrcChunkSize];
        uint crcState = RbfCrc.Begin();
        long currentOffset = offset;
        int remaining = length;

        while (remaining > 0) {
            int chunkLen = Math.Min(remaining, CrcChunkSize);
            var chunk = chunkBuf.AsSpan(0, chunkLen);

            if (!TryReadExact(currentOffset, chunk)) { return false; }

            crcState = RbfCrc.Update(crcState, chunk);
            currentOffset += chunkLen;
            remaining -= chunkLen;
        }

        return RbfCrc.End(crcState) == expectedCrc;
    }

    /// <summary>
    /// 检查指定位置是否为 Fence。
    /// </summary>
    private bool IsFenceAt(long offset) {
        Span<byte> buf = stackalloc byte[RbfConstants.FenceLength];
        if (!TryReadExact(offset, buf)) { return false; }
        return buf.SequenceEqual(RbfConstants.FenceBytes);
    }

    /// <summary>
    /// 从文件的指定偏移读取一个 UInt32（little-endian）。
    /// </summary>
    private bool TryReadUInt32(long offset, out uint value) {
        value = 0;
        Span<byte> buf = stackalloc byte[4];
        if (!TryReadExact(offset, buf)) { return false; }
        value = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        return true;
    }

    /// <summary>
    /// 从文件的指定偏移读取一个字节。
    /// </summary>
    private bool TryReadByte(long offset, out byte b) {
        b = 0;
        Span<byte> buf = stackalloc byte[1];
        if (!TryReadExact(offset, buf)) { return false; }
        b = buf[0];
        return true;
    }

    /// <inheritdoc/>
    public byte[] ReadPayload(in RbfFrame frame) {
        if (frame.PayloadLength == 0) { return []; }
        if (_fileLength == 0) { return []; }

        byte[] payload = new byte[frame.PayloadLength];
        if (!TryReadExact(frame.PayloadOffset, payload)) { throw new InvalidOperationException($"Failed to read payload at offset {frame.PayloadOffset}, length {frame.PayloadLength}"); }

        return payload;
    }

    /// <summary>
    /// 从文件的指定偏移读取精确数量的字节。
    /// </summary>
    /// <param name="offset">文件偏移。</param>
    /// <param name="buffer">目标缓冲区。</param>
    /// <returns>是否成功读取了 buffer.Length 字节。</returns>
    private bool TryReadExact(long offset, Span<byte> buffer) {
        if (_handle == null) { return false; }
        if (offset < 0 || offset + buffer.Length > _fileLength) { return false; }

        int bytesRead = RandomAccess.Read(_handle, buffer, offset);
        return bytesRead == buffer.Length;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;

        _fileStream?.Dispose();
    }
}
