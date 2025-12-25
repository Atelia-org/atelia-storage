using System.Buffers.Binary;

namespace Atelia.Rbf;

/// <summary>
/// RBF 帧扫描器实现。
/// </summary>
/// <remarks>
/// <para>基于 <see cref="ReadOnlyMemory{T}"/> 实现帧扫描和逆向遍历。</para>
/// <para><b>[R-REVERSE-SCAN-ALGORITHM]</b>: 从文件尾部向前扫描 Fence，验证帧完整性。</para>
/// <para><b>[R-RESYNC-BEHAVIOR]</b>: 校验失败时按 4B 步长向前搜索。</para>
/// </remarks>
public sealed class RbfScanner : IRbfScanner
{
    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>
    /// 创建 RbfScanner。
    /// </summary>
    /// <param name="data">RBF 文件数据。</param>
    public RbfScanner(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <summary>
    /// 文件长度。
    /// </summary>
    public int Length => _data.Length;

    /// <inheritdoc/>
    public bool TryReadAt(Address64 address, out RbfFrame frame)
    {
        frame = default;

        // 验证地址有效性
        if (address.IsNull)
            return false;

        long frameStart = (long)address.Value;

        // [F-ADDRESS64-ALIGNMENT]: 必须 4B 对齐
        if (!RbfLayout.Is4ByteAligned(frameStart))
            return false;

        // 验证边界
        if (frameStart < RbfConstants.FenceLength)
            return false;

        // 尝试读取帧
        return TryReadFrameAt(frameStart, out frame);
    }

    /// <inheritdoc/>
    public IEnumerable<RbfFrame> ScanReverse()
    {
        // 使用内部迭代器避免 span 跨越 yield 边界
        var results = new List<RbfFrame>();
        ScanReverseInternal(results);
        return results;
    }

    /// <summary>
    /// 内部逆向扫描实现。
    /// </summary>
    private void ScanReverseInternal(List<RbfFrame> results)
    {
        var span = _data.Span;
        int fileLength = span.Length;

        // 1) 若 fileLength < GenesisLen: 返回空
        if (fileLength < RbfConstants.FenceLength)
            return;

        // 2) fencePos = alignDown4(fileLength - FenceLen)
        long fencePos = RbfLayout.AlignDown4(fileLength - RbfConstants.FenceLength);

        // 3) while fencePos >= 0
        while (fencePos >= 0)
        {
            // a) 若 fencePos == 0: 停止（到达 Genesis Fence）
            if (fencePos == 0)
                break;

            // b) 若 bytes[fencePos..fencePos+4] != FenceValue: Resync
            if (!IsFenceAt(span, fencePos))
            {
                fencePos -= 4;
                continue;
            }

            // c) 现在 fencePos 指向一个 Fence
            long recordEnd = fencePos;

            // 若 recordEnd < GenesisLen + MinFrameBytes (20): 不足以容纳最小帧
            if (recordEnd < RbfConstants.FenceLength + RbfLayout.MinFrameLength)
            {
                fencePos -= 4;
                continue;
            }

            // 读取 tailLen @ (recordEnd - 8)
            // 读取 storedCrc @ (recordEnd - 4)
            if (recordEnd < 8)
            {
                fencePos -= 4;
                continue;
            }

            uint tailLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)(recordEnd - 8), 4));
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)(recordEnd - 4), 4));

            // frameStart = recordEnd - tailLen
            long frameStart = recordEnd - tailLen;

            // 若 frameStart < GenesisLen 或 frameStart % 4 != 0
            if (frameStart < RbfConstants.FenceLength || !RbfLayout.Is4ByteAligned(frameStart))
            {
                fencePos -= 4;
                continue;
            }

            // prevFencePos = frameStart - FenceLen
            long prevFencePos = frameStart - RbfConstants.FenceLength;

            // 若 prevFencePos < 0 或 bytes[prevFencePos..prevFencePos+4] != FenceValue
            if (prevFencePos < 0 || !IsFenceAt(span, prevFencePos))
            {
                fencePos -= 4;
                continue;
            }

            // 读取 headLen @ frameStart
            uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)frameStart, 4));

            // 若 headLen != tailLen 或 headLen % 4 != 0 或 headLen < 20
            if (headLen != tailLen || (headLen % 4) != 0 || headLen < RbfLayout.MinFrameLength)
            {
                fencePos -= 4;
                continue;
            }

            // 计算 PayloadLen = HeadLen - 16 - StatusLen
            // 由于我们不知道 StatusLen，需要反推
            // HeadLen = 16 + PayloadLen + StatusLen
            // StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)
            // 尝试验证 FrameStatus
            if (!TryValidateFrame(span, frameStart, headLen, storedCrc, out var frame))
            {
                fencePos -= 4;
                continue;
            }

            // 输出有效帧
            results.Add(frame);

            // fencePos = prevFencePos
            fencePos = prevFencePos;
        }
    }

    /// <inheritdoc/>
    public byte[] ReadPayload(in RbfFrame frame)
    {
        if (frame.PayloadLength == 0)
            return [];

        return _data.Span.Slice((int)frame.PayloadOffset, frame.PayloadLength).ToArray();
    }

    /// <summary>
    /// 检查指定位置是否为 Fence。
    /// </summary>
    private static bool IsFenceAt(ReadOnlySpan<byte> span, long offset)
    {
        if (offset < 0 || offset + RbfConstants.FenceLength > span.Length)
            return false;

        return span.Slice((int)offset, RbfConstants.FenceLength).SequenceEqual(RbfConstants.FenceBytes);
    }

    /// <summary>
    /// 在指定位置尝试读取帧。
    /// </summary>
    private bool TryReadFrameAt(long frameStart, out RbfFrame frame)
    {
        frame = default;
        var span = _data.Span;

        // 检查前置 Fence
        long prevFencePos = frameStart - RbfConstants.FenceLength;
        if (prevFencePos < 0 || !IsFenceAt(span, prevFencePos))
            return false;

        // 读取 HeadLen
        if (frameStart + 4 > span.Length)
            return false;

        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)frameStart, 4));

        // 验证 HeadLen
        if ((headLen % 4) != 0 || headLen < RbfLayout.MinFrameLength)
            return false;

        // 检查帧是否在边界内
        long recordEnd = frameStart + headLen;
        if (recordEnd + RbfConstants.FenceLength > span.Length)
            return false;

        // 检查尾部 Fence
        if (!IsFenceAt(span, recordEnd))
            return false;

        // 读取 TailLen 和 CRC
        uint tailLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)(recordEnd - 8), 4));
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)(recordEnd - 4), 4));

        // 验证 HeadLen == TailLen
        if (headLen != tailLen)
            return false;

        // 验证帧
        return TryValidateFrame(span, frameStart, headLen, storedCrc, out frame);
    }

    /// <summary>
    /// 验证帧的完整性并提取元数据。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-FRAMESTATUS-VALUES]</b>: 使用位域格式直接从 FrameStatus 读取 StatusLen。</para>
    /// </remarks>
    private static bool TryValidateFrame(ReadOnlySpan<byte> span, long frameStart, uint headLen, uint storedCrc, out RbfFrame frame)
    {
        frame = default;

        // HeadLen = 16 + PayloadLen + StatusLen
        // PayloadLen + StatusLen = HeadLen - 16
        int payloadPlusStatus = (int)headLen - 16;

        // (PayloadLen + StatusLen) % 4 == 0
        if (payloadPlusStatus % 4 != 0 || payloadPlusStatus < 1)
            return false;

        // 读取 FrameTag
        long tagOffset = frameStart + 4;
        uint frameTag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)tagOffset, 4));

        // [F-FRAMESTATUS-VALUES]: 从 FrameStatus 的第一个字节读取 StatusLen
        // FrameStatus 位于 [frameStart + 8 + PayloadLen, frameStart + 8 + PayloadLen + StatusLen)
        // 先读取 TailLen 前面最后一个 FrameStatus 字节的位置
        // recordEnd = frameStart + headLen, TailLen 位于 recordEnd - 8
        // FrameStatus 的最后一个字节位于 TailLen 之前 = recordEnd - 8 - 1 = recordEnd - 9
        // 但我们不知道确切位置...

        // 使用新的位域格式：FrameStatus 所有字节相同，且包含 StatusLen 信息
        // 我们可以从 recordEnd - 9（TailLen 之前的最后一个字节）读取 FrameStatus
        long recordEnd = frameStart + headLen;

        // 确保有足够的空间读取
        if (recordEnd - 9 < frameStart + 8)
            return false;

        // 读取 FrameStatus 的最后一个字节（TailLen 之前）
        // TailLen 位于 recordEnd - 8，所以 FrameStatus 最后一个字节在 recordEnd - 9
        byte statusByte = span[(int)(recordEnd - 9)];
        var status = FrameStatus.FromByte(statusByte);

        // [F-FRAMESTATUS-VALUES]: 检查是否为 MVP 合法值（保留位为 0）
        if (!status.IsMvpValid)
            return false;

        int statusLen = status.StatusLen;
        int payloadLen = payloadPlusStatus - statusLen;

        // 验证 PayloadLen >= 0
        if (payloadLen < 0)
            return false;

        // 验证 StatusLen 与公式一致
        int expectedStatusLen = RbfLayout.CalculateStatusLength(payloadLen);
        if (expectedStatusLen != statusLen)
            return false;

        // [F-FRAMESTATUS-FILL]: 验证所有 FrameStatus 字节相同
        long statusOffset = frameStart + RbfLayout.PayloadOffset + payloadLen;
        var statusBytes = span.Slice((int)statusOffset, statusLen);

        for (int i = 0; i < statusLen; i++)
        {
            if (statusBytes[i] != statusByte)
                return false;
        }

        // 验证 CRC32C
        // CRC 覆盖范围: [frameStart+4, recordEnd-4) = FrameTag + Payload + FrameStatus + TailLen
        long crcStart = frameStart + 4; // FrameTag 起点
        int crcLen = 4 + payloadLen + statusLen + 4; // FrameTag + Payload + FrameStatus + TailLen
        var crcData = span.Slice((int)crcStart, crcLen);

        if (!RbfCrc.Verify(crcData, storedCrc))
            return false;

        // 构建 RbfFrame
        frame = new RbfFrame(
            FileOffset: frameStart,
            FrameTag: frameTag,
            PayloadOffset: frameStart + RbfLayout.PayloadOffset,
            PayloadLength: payloadLen,
            Status: status);

        return true;
    }
}
