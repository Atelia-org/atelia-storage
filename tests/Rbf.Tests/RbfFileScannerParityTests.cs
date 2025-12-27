using System.Buffers;
using System.Buffers.Binary;
using Atelia.Rbf.Tests.TestHelpers;
using FluentAssertions;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// T-M1-12e: RbfFileScanner 与 RbfScanner 语义对齐验收测试。
/// </summary>
/// <remarks>
/// <para>确保 file-backed <see cref="RbfFileScanner.ScanReverse"/> 在截断/损坏场景下
/// 与 memory-based <see cref="RbfScanner.ScanReverse"/> 行为一致。</para>
/// </remarks>
public class RbfFileScannerParityTests {
    #region Helper Methods

    /// <summary>
    /// 使用 RbfFramer 创建测试数据（内存版）。
    /// </summary>
    private static byte[] CreateRbfData(Action<RbfFramer> writeAction) {
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        writeAction(framer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 手动构建 RBF 帧数据（用于测试损坏场景）。
    /// </summary>
    private static byte[] BuildRawFrame(uint tag, byte[] payload, FrameStatus status, bool corruptCrc = false) {
        int payloadLen = payload.Length;
        int statusLen = RbfLayout.CalculateStatusLength(payloadLen);
        int frameLen = RbfLayout.CalculateFrameLength(payloadLen);

        var frame = new byte[frameLen];
        int offset = 0;

        // HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)frameLen);
        offset += 4;

        // FrameTag
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), tag);
        offset += 4;

        // Payload
        payload.CopyTo(frame.AsSpan(offset));
        offset += payloadLen;

        // FrameStatus
        var actualStatus = status.IsTombstone
            ? FrameStatus.CreateTombstone(statusLen)
            : FrameStatus.CreateValid(statusLen);
        for (int i = 0; i < statusLen; i++) {
            frame[offset + i] = actualStatus.Value;
        }
        offset += statusLen;

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)frameLen);
        offset += 4;

        // CRC32C
        int crcStart = 4;
        int crcLen = 4 + payloadLen + statusLen + 4;
        var crcData = frame.AsSpan(crcStart, crcLen);
        uint crc = RbfCrc.Compute(crcData);
        if (corruptCrc) {
            crc ^= 0xDEADBEEF; // 篡改 CRC
        }
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), crc);

        return frame;
    }

    /// <summary>
    /// 构建完整的 RBF 文件数据（手动方式）。
    /// </summary>
    private static byte[] BuildRbfFile(params byte[][] frames) {
        int totalLen = RbfConstants.FenceLength;
        foreach (var frame in frames) {
            totalLen += frame.Length + RbfConstants.FenceLength;
        }

        var data = new byte[totalLen];
        int offset = 0;

        // Genesis Fence
        RbfConstants.FenceBytes.CopyTo(data.AsSpan(offset));
        offset += RbfConstants.FenceLength;

        // Frames
        foreach (var frame in frames) {
            frame.CopyTo(data.AsSpan(offset));
            offset += frame.Length;
            RbfConstants.FenceBytes.CopyTo(data.AsSpan(offset));
            offset += RbfConstants.FenceLength;
        }

        return data;
    }

    /// <summary>
    /// 比较两个帧列表的语义等价性（FrameTag + PayloadLength + Status）。
    /// </summary>
    private static void AssertFramesParity(
        IReadOnlyList<RbfFrame> expected,
        IReadOnlyList<RbfFrame> actual,
        string because = ""
    ) {
        actual.Count.Should().Be(expected.Count, $"frame count should match{(string.IsNullOrEmpty(because) ? "" : $" ({because})")}");

        for (int i = 0; i < expected.Count; i++) {
            actual[i].FrameTag.Should().Be(expected[i].FrameTag, $"frame {i} FrameTag should match");
            actual[i].PayloadLength.Should().Be(expected[i].PayloadLength, $"frame {i} PayloadLength should match");
            actual[i].Status.Should().Be(expected[i].Status, $"frame {i} Status should match");
        }
    }

    #endregion

    #region Truncate Parity Tests

    /// <summary>
    /// [Truncate-Parity-1] 截断到第二帧结尾（包含尾部 Fence）。
    /// </summary>
    /// <remarks>
    /// <para>验证：截断后恰好包含完整的帧 1 和帧 2，两个扫描器应返回 [Frame2, Frame1]。</para>
    /// </remarks>
    [Fact]
    public void ScanReverse_TruncateAtFrame2End_ParityWithMemoryScanner() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("truncate-at-frame2-end.rbf");

        // 写入 3 帧
        var fullData = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x11, 0x22, 0x33]);       // Frame 1
                framer.Append(new FrameTag(2), [0x44, 0x55, 0x66, 0x77]); // Frame 2
                framer.Append(new FrameTag(3), [0x88, 0x99]);             // Frame 3
            }
        );

        // 计算截断位置：Genesis(4) + Frame1(20+4) + Frame2(20+4) = 52
        // Frame1: HeadLen(4)+Tag(4)+Payload(3)+Status(1)+TailLen(4)+CRC(4)=20, then Fence(4)
        // Frame2: HeadLen(4)+Tag(4)+Payload(4)+Status(4)+TailLen(4)+CRC(4)=24, then Fence(4)
        // Genesis(4) + Frame1(20) + Fence(4) + Frame2(24) + Fence(4) = 56
        // 为了找准确位置，我们先用 memory scanner 扫描全文件
        var memScannerFull = new RbfScanner(fullData);
        var allFrames = memScannerFull.ScanReverse().ToList();
        allFrames.Should().HaveCount(3, "should have 3 frames in full file");

        // Frame2 的结束位置 = Frame2.FileOffset + Frame2.FrameLength + FenceLength
        var frame2 = allFrames.Single(f => f.FrameTag == 2);
        int truncatePos = (int)(frame2.FileOffset + frame2.FrameLength + RbfConstants.FenceLength);

        // 截断数据
        var truncatedData = fullData.AsSpan(0, truncatePos).ToArray();

        // Memory scanner 结果（expected）
        var memoryScanner = new RbfScanner(truncatedData);
        var expectedFrames = memoryScanner.ScanReverse().ToList();

        // 写入截断数据到文件
        File.WriteAllBytes(path, truncatedData);

        // File scanner 结果（actual）
        using var fileScanner = new RbfFileScanner(path);
        var actualFrames = fileScanner.ScanReverse().ToList();

        // 验证
        expectedFrames.Should().HaveCount(2, "memory scanner should find Frame2 and Frame1");
        AssertFramesParity(expectedFrames, actualFrames, "truncated at Frame2 end");
    }

    /// <summary>
    /// [Truncate-Parity-2] 截断到第二帧中间（破坏帧结构）。
    /// </summary>
    /// <remarks>
    /// <para>验证：截断后帧 2 不完整，扫描器应只返回 [Frame1]。</para>
    /// </remarks>
    [Fact]
    public void ScanReverse_TruncateInMiddleOfFrame2_ParityWithMemoryScanner() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("truncate-mid-frame2.rbf");

        // 写入 3 帧
        var fullData = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x11, 0x22, 0x33]);       // Frame 1
                framer.Append(new FrameTag(2), new byte[50]);             // Frame 2 (较大的 payload)
                framer.Append(new FrameTag(3), [0x88, 0x99]);             // Frame 3
            }
        );

        // 找到 Frame2 的位置
        var memScannerFull = new RbfScanner(fullData);
        var allFrames = memScannerFull.ScanReverse().ToList();
        allFrames.Should().HaveCount(3);

        var frame2 = allFrames.Single(f => f.FrameTag == 2);

        // 截断到 Frame2 的中间（PayloadOffset + 10 字节，破坏帧结构）
        int truncatePos = (int)(frame2.PayloadOffset + 10);

        var truncatedData = fullData.AsSpan(0, truncatePos).ToArray();

        // Memory scanner 结果（expected）
        var memoryScanner = new RbfScanner(truncatedData);
        var expectedFrames = memoryScanner.ScanReverse().ToList();

        // 写入截断数据到文件
        File.WriteAllBytes(path, truncatedData);

        // File scanner 结果（actual）
        using var fileScanner = new RbfFileScanner(path);
        var actualFrames = fileScanner.ScanReverse().ToList();

        // 验证
        expectedFrames.Should().HaveCount(1, "memory scanner should only find Frame1");
        expectedFrames[0].FrameTag.Should().Be(1);
        AssertFramesParity(expectedFrames, actualFrames, "truncated in middle of Frame2");
    }

    /// <summary>
    /// [Truncate-Parity-3] 截断到仅剩 Genesis Fence。
    /// </summary>
    [Fact]
    public void ScanReverse_TruncateToGenesisOnly_ParityWithMemoryScanner() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("truncate-genesis-only.rbf");

        // 写入 2 帧
        var fullData = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x11]);
                framer.Append(new FrameTag(2), [0x22]);
            }
        );

        // 截断到仅剩 Genesis Fence
        var truncatedData = fullData.AsSpan(0, RbfConstants.FenceLength).ToArray();

        // Memory scanner 结果（expected）
        var memoryScanner = new RbfScanner(truncatedData);
        var expectedFrames = memoryScanner.ScanReverse().ToList();

        // 写入截断数据到文件
        File.WriteAllBytes(path, truncatedData);

        // File scanner 结果（actual）
        using var fileScanner = new RbfFileScanner(path);
        var actualFrames = fileScanner.ScanReverse().ToList();

        // 验证
        expectedFrames.Should().BeEmpty("memory scanner should find no frames with only Genesis");
        AssertFramesParity(expectedFrames, actualFrames, "truncated to Genesis only");
    }

    #endregion

    #region CRC Corruption Parity Tests

    /// <summary>
    /// [CRC-Parity-1] 单帧 CRC 损坏应被跳过。
    /// </summary>
    [Fact]
    public void ScanReverse_SingleFrameCrcCorrupted_ParityWithMemoryScanner() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("crc-corrupted-single.rbf");

        // 构建一个 CRC 损坏的帧
        var corruptFrame = BuildRawFrame(0xBADBAD, [0x01, 0x02, 0x03],
            FrameStatus.CreateValid(1), corruptCrc: true
        );
        var data = BuildRbfFile(corruptFrame);

        // Memory scanner 结果（expected）
        var memoryScanner = new RbfScanner(data);
        var expectedFrames = memoryScanner.ScanReverse().ToList();

        // 写入数据到文件
        File.WriteAllBytes(path, data);

        // File scanner 结果（actual）
        using var fileScanner = new RbfFileScanner(path);
        var actualFrames = fileScanner.ScanReverse().ToList();

        // 验证
        expectedFrames.Should().BeEmpty("memory scanner should reject CRC-corrupted frame");
        AssertFramesParity(expectedFrames, actualFrames, "single CRC-corrupted frame");
    }

    /// <summary>
    /// [CRC-Parity-2] 中间帧 CRC 损坏，应跳过该帧但找到其他有效帧。
    /// </summary>
    [Fact]
    public void ScanReverse_MiddleFrameCrcCorrupted_ParityWithMemoryScanner() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("crc-corrupted-middle.rbf");

        // 构建 [Valid1] [Corrupt] [Valid3] 结构
        var frame1 = BuildRawFrame(1, [0x11], FrameStatus.CreateValid(RbfLayout.CalculateStatusLength(1)));
        var frame2 = BuildRawFrame(2, [0x22, 0x33], FrameStatus.CreateValid(RbfLayout.CalculateStatusLength(2)), corruptCrc: true);
        var frame3 = BuildRawFrame(3, [0x44, 0x55, 0x66], FrameStatus.CreateValid(RbfLayout.CalculateStatusLength(3)));

        var data = BuildRbfFile(frame1, frame2, frame3);

        // Memory scanner 结果（expected）
        var memoryScanner = new RbfScanner(data);
        var expectedFrames = memoryScanner.ScanReverse().ToList();

        // 写入数据到文件
        File.WriteAllBytes(path, data);

        // File scanner 结果（actual）
        using var fileScanner = new RbfFileScanner(path);
        var actualFrames = fileScanner.ScanReverse().ToList();

        // 验证
        expectedFrames.Should().HaveCount(2, "memory scanner should find Frame3 and Frame1");
        expectedFrames[0].FrameTag.Should().Be(3);
        expectedFrames[1].FrameTag.Should().Be(1);
        AssertFramesParity(expectedFrames, actualFrames, "middle CRC-corrupted frame");
    }

    /// <summary>
    /// [CRC-Parity-3] Payload 字节翻转导致 CRC 失败。
    /// </summary>
    [Fact]
    public void ScanReverse_PayloadBitFlip_ParityWithMemoryScanner() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("payload-bitflip.rbf");

        // 先创建有效帧
        var validData = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0xAA, 0xBB, 0xCC]);
            }
        );

        // 翻转 Payload 中的一个比特
        var corruptedData = validData.ToArray();
        // Payload 位于 Genesis(4) + HeadLen(4) + FrameTag(4) = offset 12
        int payloadOffset = RbfConstants.FenceLength + RbfLayout.PayloadOffset;
        corruptedData[payloadOffset] ^= 0x01; // Flip one bit

        // Memory scanner 结果（expected）
        var memoryScanner = new RbfScanner(corruptedData);
        var expectedFrames = memoryScanner.ScanReverse().ToList();

        // 写入数据到文件
        File.WriteAllBytes(path, corruptedData);

        // File scanner 结果（actual）
        using var fileScanner = new RbfFileScanner(path);
        var actualFrames = fileScanner.ScanReverse().ToList();

        // 验证
        expectedFrames.Should().BeEmpty("memory scanner should reject payload-corrupted frame");
        AssertFramesParity(expectedFrames, actualFrames, "payload bit flip");
    }

    #endregion
}
