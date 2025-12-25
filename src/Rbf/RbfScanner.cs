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
    private static bool TryValidateFrame(ReadOnlySpan<byte> span, long frameStart, uint headLen, uint storedCrc, out RbfFrame frame)
    {
        frame = default;

        // HeadLen = 16 + PayloadLen + StatusLen
        // 我们需要找到 PayloadLen 和 StatusLen
        // StatusLen ∈ {1, 2, 3, 4}，所以我们可以通过 (headLen - 16) 和验证来确定

        // 读取 FrameTag
        long tagOffset = frameStart + 4;
        uint frameTag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)tagOffset, 4));

        // PayloadLen + StatusLen = HeadLen - 16
        int payloadPlusStatus = (int)headLen - 16;

        // StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)
        // 这意味着 (PayloadLen + StatusLen) % 4 == 0
        // 所以 payloadPlusStatus % 4 == 0
        if (payloadPlusStatus % 4 != 0)
            return false;

        // 尝试确定 PayloadLen 和 StatusLen
        // payloadPlusStatus = PayloadLen + StatusLen
        // 由于 StatusLen ∈ {1,2,3,4}，PayloadLen 可以是 payloadPlusStatus - 1, -2, -3, -4
        // 我们需要找到一个使 StatusLen 公式成立且 FrameStatus 有效的组合
        // 从 StatusLen=4 开始尝试，以正确处理空 Payload 帧（StatusLen=4）

        int payloadLen = -1;
        int statusLen = -1;
        byte validStatusByte = 0;

        for (int tryStatusLen = 4; tryStatusLen >= 1; tryStatusLen--)
        {
            int tryPayloadLen = payloadPlusStatus - tryStatusLen;
            if (tryPayloadLen < 0)
                continue;

            int expectedStatusLen = RbfLayout.CalculateStatusLength(tryPayloadLen);
            if (expectedStatusLen != tryStatusLen)
                continue;

            // 找到公式匹配的候选，验证 FrameStatus
            long statusOffset = frameStart + RbfLayout.PayloadOffset + tryPayloadLen;
            var statusBytes = span.Slice((int)statusOffset, tryStatusLen);

            byte firstStatusByte = statusBytes[0];

            // [F-FRAMESTATUS-VALUES]: 值必须为 0x00 (Valid) 或 0xFF (Tombstone)
            if (firstStatusByte != (byte)FrameStatus.Valid && firstStatusByte != (byte)FrameStatus.Tombstone)
                continue;

            // [F-FRAMESTATUS-FILL]: 所有字节必须相同
            bool allSame = true;
            for (int i = 1; i < tryStatusLen; i++)
            {
                if (statusBytes[i] != firstStatusByte)
                {
                    allSame = false;
                    break;
                }
            }

            if (!allSame)
                continue;

            // 验证 CRC32C
            // CRC 覆盖范围: [frameStart+4, recordEnd-4) = FrameTag + Payload + FrameStatus + TailLen
            long crcStart = frameStart + 4; // FrameTag 起点
            int crcLen = 4 + tryPayloadLen + tryStatusLen + 4; // FrameTag + Payload + FrameStatus + TailLen
            var crcData = span.Slice((int)crcStart, crcLen);

            if (!RbfCrc.Verify(crcData, storedCrc))
                continue;

            // 全部验证通过，找到有效组合
            payloadLen = tryPayloadLen;
            statusLen = tryStatusLen;
            validStatusByte = firstStatusByte;
            break;
        }

        if (payloadLen < 0)
            return false;

        // 构建 RbfFrame
        frame = new RbfFrame(
            FileOffset: frameStart,
            FrameTag: frameTag,
            PayloadOffset: frameStart + RbfLayout.PayloadOffset,
            PayloadLength: payloadLen,
            Status: (FrameStatus)validStatusByte);

        return true;
    }
}
