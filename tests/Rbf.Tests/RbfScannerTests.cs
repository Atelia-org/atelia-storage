using System.Buffers;
using System.Buffers.Binary;
using Atelia.Rbf;
using FluentAssertions;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfScanner 测试。
/// </summary>
/// <remarks>
/// 覆盖：[A-RBF-SCANNER-INTERFACE], [R-REVERSE-SCAN-ALGORITHM], [R-RESYNC-BEHAVIOR],
/// [S-RBF-TOMBSTONE-VISIBLE], [F-FRAMING-FAIL-REJECT], [F-CRC-FAIL-REJECT]
/// </remarks>
public class RbfScannerTests {
    #region Helper Methods

    /// <summary>
    /// 使用 RbfFramer 创建测试数据。
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
    private static byte[] BuildRawFrame(uint tag, byte[] payload, FrameStatus status, bool corruptCrc = false,
        uint? customHeadLen = null, uint? customTailLen = null, byte[]? customStatusBytes = null
    ) {
        int payloadLen = payload.Length;
        int statusLen = RbfLayout.CalculateStatusLength(payloadLen);
        int frameLen = RbfLayout.CalculateFrameLength(payloadLen);

        // FrameBytes: HeadLen + FrameTag + Payload + FrameStatus + TailLen + CRC
        var frame = new byte[frameLen];
        int offset = 0;

        // HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), customHeadLen ?? (uint)frameLen);
        offset += 4;

        // FrameTag
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), tag);
        offset += 4;

        // Payload
        payload.CopyTo(frame.AsSpan(offset));
        offset += payloadLen;

        // FrameStatus - 使用新的位域格式
        if (customStatusBytes != null) {
            customStatusBytes.AsSpan(0, statusLen).CopyTo(frame.AsSpan(offset));
        }
        else {
            // 创建正确的位域格式 FrameStatus
            var actualStatus = status.IsTombstone
                ? FrameStatus.CreateTombstone(statusLen)
                : FrameStatus.CreateValid(statusLen);
            for (int i = 0; i < statusLen; i++) {
                frame[offset + i] = actualStatus.Value;
            }
        }
        offset += statusLen;

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), customTailLen ?? (uint)frameLen);
        offset += 4;

        // CRC32C (覆盖 FrameTag + Payload + FrameStatus + TailLen)
        int crcStart = 4; // FrameTag 起点
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
        // 计算总长度: Genesis Fence + sum(frame + fence)
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

    #endregion

    #region RBF-EMPTY-001: 空文件

    /// <summary>
    /// 测试 RBF-EMPTY-001: 空文件（仅 Genesis）→ 0 帧。
    /// </summary>
    [Fact]
    public void ScanReverse_EmptyFile_ReturnsNoFrames() {
        // Arrange: 仅 Genesis Fence
        var data = RbfConstants.FenceBytes.ToArray();
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().BeEmpty();
    }

    /// <summary>
    /// 测试文件长度小于 Genesis。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ScanReverse_FileShorterThanGenesis_ReturnsNoFrames(int length) {
        // Arrange
        var data = new byte[length];
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().BeEmpty();
    }

    #endregion

    #region RBF-SINGLE-001: 单帧

    /// <summary>
    /// 测试 RBF-SINGLE-001: 单帧 → 1 帧，起始位置 = 4。
    /// </summary>
    [Fact]
    public void ScanReverse_SingleFrame_ReturnsOneFrame() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0x12345678), [0x01, 0x02, 0x03]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].FileOffset.Should().Be(4); // Genesis Fence 之后
        frames[0].FrameTag.Should().Be(0x12345678);
        frames[0].PayloadLength.Should().Be(3);
        frames[0].Status.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 测试 TryReadAt 读取单帧。
    /// </summary>
    [Fact]
    public void TryReadAt_ValidAddress_ReturnsTrue() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0xABCD1234), [0xDE, 0xAD, 0xBE, 0xEF]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        bool success = scanner.TryReadAt(Address64.FromOffset(4), out var frame);

        // Assert
        success.Should().BeTrue();
        frame.FileOffset.Should().Be(4);
        frame.FrameTag.Should().Be(0xABCD1234);
        frame.PayloadLength.Should().Be(4);
        frame.Status.IsValid.Should().BeTrue();
    }

    #endregion

    #region RBF-DOUBLE-001: 双帧

    /// <summary>
    /// 测试 RBF-DOUBLE-001: 双帧 → 按逆序返回 Frame2, Frame1。
    /// </summary>
    [Fact]
    public void ScanReverse_TwoFrames_ReturnsInReverseOrder() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0x11111111), [0x01]);
                framer.Append(new FrameTag(0x22222222), [0x02, 0x03]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(2);

        // Frame2 先返回（逆序）
        frames[0].FrameTag.Should().Be(0x22222222);
        frames[0].PayloadLength.Should().Be(2);

        // Frame1 后返回
        frames[1].FrameTag.Should().Be(0x11111111);
        frames[1].PayloadLength.Should().Be(1);
    }

    /// <summary>
    /// 测试多帧连续扫描。
    /// </summary>
    [Fact]
    public void ScanReverse_MultipleFrames_ReturnsAllInReverseOrder() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                for (uint i = 1; i <= 5; i++) {
                    framer.Append(new FrameTag(i), new byte[i]);
                }
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(5);
        frames.Select(f => f.FrameTag).Should().Equal(5u, 4u, 3u, 2u, 1u);
    }

    #endregion

    #region RBF-OK-001/002: Valid 和 Tombstone

    /// <summary>
    /// 测试 RBF-OK-001: 空 payload Valid 帧（PayloadLen=0 → StatusLen=4）。
    /// </summary>
    [Fact]
    public void ScanReverse_EmptyPayloadValidFrame_Succeeds() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0x41414141), ReadOnlySpan<byte>.Empty);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].PayloadLength.Should().Be(0);
        frames[0].Status.IsValid.Should().BeTrue();
        frames[0].StatusLength.Should().Be(4);
    }

    /// <summary>
    /// 测试 RBF-OK-002: Tombstone 帧可见。
    /// </summary>
    [Fact]
    public void ScanReverse_TombstoneFrame_IsVisible() {
        // Arrange: 使用 BeginFrame + Dispose（不 Commit）生成 Tombstone
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);

        using (var builder = framer.BeginFrame(new FrameTag(0x54534E54))) // "TNST"
        {
            var span = builder.Payload.GetSpan(3);
            span[0] = 0xAA;
            span[1] = 0xBB;
            span[2] = 0xCC;
            builder.Payload.Advance(3);
            // 不调用 Commit，触发 Auto-Abort → Tombstone
        }

        var scanner = new RbfScanner(buffer.WrittenMemory);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].Status.IsTombstone.Should().BeTrue();
        frames[0].PayloadLength.Should().Be(3);
        frames[0].FrameTag.Should().Be(0x54534E54);
    }

    /// <summary>
    /// 测试 [S-RBF-TOMBSTONE-VISIBLE]: Scanner 产出 Valid 和 Tombstone 帧。
    /// </summary>
    [Fact]
    public void ScanReverse_MixedValidAndTombstone_ReturnsAll() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);

        // Frame 1: Valid
        framer.Append(new FrameTag(1), [0x01]);

        // Frame 2: Tombstone
        using (var builder = framer.BeginFrame(new FrameTag(2))) {
            builder.Payload.GetSpan(1)[0] = 0x02;
            builder.Payload.Advance(1);
            // 不 Commit → Tombstone
        }

        // Frame 3: Valid
        framer.Append(new FrameTag(3), [0x03]);

        var scanner = new RbfScanner(buffer.WrittenMemory);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(3);
        frames[0].FrameTag.Should().Be(3);
        frames[0].Status.IsValid.Should().BeTrue();
        frames[1].FrameTag.Should().Be(2);
        frames[1].Status.IsTombstone.Should().BeTrue();
        frames[2].FrameTag.Should().Be(1);
        frames[2].Status.IsValid.Should().BeTrue();
    }

    #endregion

    #region RBF-OK-003: StatusLen 覆盖

    /// <summary>
    /// 测试 RBF-OK-003: StatusLen 覆盖（1/2/3/4）。
    /// </summary>
    [Theory]
    [InlineData(3, 1)]  // PayloadLen=3 → StatusLen=1
    [InlineData(2, 2)]  // PayloadLen=2 → StatusLen=2
    [InlineData(1, 3)]  // PayloadLen=1 → StatusLen=3
    [InlineData(0, 4)]  // PayloadLen=0 → StatusLen=4
    [InlineData(4, 4)]  // PayloadLen=4 → StatusLen=4
    [InlineData(5, 3)]  // PayloadLen=5 → StatusLen=3
    public void ScanReverse_VariousPayloadLengths_CorrectStatusLen(int payloadLen, int expectedStatusLen) {
        // Arrange: 使用非零 Payload 避免与 FrameStatus (0x00) 混淆
        var payload = new byte[payloadLen];
        for (int i = 0; i < payloadLen; i++) {
            payload[i] = (byte)(0x10 + i); // 非零值，避免歧义
        }

        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0xDEADBEEF), payload);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].PayloadLength.Should().Be(payloadLen);
        frames[0].StatusLength.Should().Be(expectedStatusLen);
    }

    #endregion

    #region RBF-BAD-001: HeadLen != TailLen

    /// <summary>
    /// 测试 RBF-BAD-001: HeadLen != TailLen → 跳过。
    /// </summary>
    [Fact]
    public void ScanReverse_HeadLenMismatch_SkipsFrame() {
        // Arrange: 手动构建一个 HeadLen != TailLen 的帧
        var badFrame = BuildRawFrame(0x11111111, [0x01, 0x02, 0x03], FrameStatus.CreateValid(1),
            customHeadLen: 20, customTailLen: 24
        ); // 故意不一致

        var data = BuildRbfFile(badFrame);
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().BeEmpty();
    }

    #endregion

    #region RBF-BAD-002: CRC 不匹配

    /// <summary>
    /// 测试 RBF-BAD-002: CRC32C 不匹配 → 跳过。
    /// </summary>
    [Fact]
    public void ScanReverse_CrcMismatch_SkipsFrame() {
        // Arrange: 篡改 CRC
        var badFrame = BuildRawFrame(0x22222222, [0xAA, 0xBB], FrameStatus.CreateValid(2), corruptCrc: true);
        var data = BuildRbfFile(badFrame);
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().BeEmpty();
    }

    /// <summary>
    /// 测试 TryReadAt 遇到 CRC 错误返回 false。
    /// </summary>
    [Fact]
    public void TryReadAt_CrcMismatch_ReturnsFalse() {
        // Arrange
        var badFrame = BuildRawFrame(0x33333333, [0xCC], FrameStatus.CreateValid(3), corruptCrc: true);
        var data = BuildRbfFile(badFrame);
        var scanner = new RbfScanner(data);

        // Act
        bool success = scanner.TryReadAt(Address64.FromOffset(4), out var frame);

        // Assert
        success.Should().BeFalse();
    }

    #endregion

    #region RBF-BAD-003/004: 对齐和边界

    /// <summary>
    /// 测试 RBF-BAD-003: Frame 起点非 4B 对齐 → 跳过。
    /// </summary>
    [Fact]
    public void TryReadAt_NonAlignedAddress_ReturnsFalse() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x01]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act: 尝试读取非对齐地址
        bool success = scanner.TryReadAt(Address64.FromOffset(5), out _);

        // Assert
        success.Should().BeFalse();
    }

    /// <summary>
    /// 测试无效地址返回 false。
    /// </summary>
    [Fact]
    public void TryReadAt_NullAddress_ReturnsFalse() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x01]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        bool success = scanner.TryReadAt(Address64.Null, out _);

        // Assert
        success.Should().BeFalse();
    }

    /// <summary>
    /// 测试地址越界返回 false。
    /// </summary>
    [Fact]
    public void TryReadAt_AddressBeyondFile_ReturnsFalse() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x01]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        bool success = scanner.TryReadAt(Address64.FromOffset(1000), out _);

        // Assert
        success.Should().BeFalse();
    }

    #endregion

    #region RBF-BAD-005/006: FrameStatus 非法值和不一致

    /// <summary>
    /// 测试 RBF-BAD-005: FrameStatus 非法值 → 拒绝。
    /// </summary>
    /// <remarks>
    /// 新的位域格式下，非法值是指 Reserved bits (6-2) 不为零的值。
    /// Valid: 0x00-0x03, 0x80-0x83
    /// Invalid: 0x04, 0x7F, 0xFE, 0xFF 等
    /// </remarks>
    [Theory]
    [InlineData(0x04)]  // Reserved bit set
    [InlineData(0x7F)]  // Multiple reserved bits set
    [InlineData(0xFE)]  // Tombstone bit + all reserved bits + invalid statusLen bits
    [InlineData(0xFF)]  // Old Tombstone value - now invalid
    public void ScanReverse_InvalidFrameStatus_SkipsFrame(byte invalidStatus) {
        // Arrange: 构建带有非法 FrameStatus 值的帧
        // PayloadLen=0 → StatusLen=4
        byte[] payload = [];
        int statusLen = 4;
        int frameLen = 20;

        var frame = new byte[frameLen];
        int offset = 0;

        // HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)frameLen);
        offset += 4;

        // FrameTag
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), 0x44444444);
        offset += 4;

        // FrameStatus (填充非法值)
        for (int i = 0; i < statusLen; i++) {
            frame[offset++] = invalidStatus;
        }

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)frameLen);
        offset += 4;

        // CRC32C
        int crcStart = 4;
        int crcLen = 4 + statusLen + 4;
        var crcData = frame.AsSpan(crcStart, crcLen);
        uint crc = RbfCrc.Compute(crcData);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), crc);

        var data = BuildRbfFile(frame);
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().BeEmpty();
    }

    /// <summary>
    /// 测试 RBF-BAD-006: FrameStatus 填充不一致 → 拒绝。
    /// </summary>
    [Fact]
    public void ScanReverse_InconsistentFrameStatus_SkipsFrame() {
        // Arrange: 构建 FrameStatus 非法值的帧
        // 注意：由于 RBF 格式的特性，当多个 (PayloadLen, StatusLen) 组合都满足公式时，
        // 扫描器会尝试所有组合直到找到一个有效的解释。
        // 因此，我们需要确保所有可能的 StatusLen 解释都会失败。

        // 使用 PayloadLen=3, StatusLen=1，但 FrameStatus=0x77（非法值）
        // 同时确保其他可能的 (PayloadLen, StatusLen) 解释也会失败
        byte[] payload = [0x77, 0x77, 0x77]; // 填充非法的 FrameStatus 值
        int statusLen = 1;
        int frameLen = RbfLayout.CalculateFrameLength(payload.Length);

        var frame = new byte[frameLen];
        int offset = 0;

        // HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)frameLen);
        offset += 4;

        // FrameTag
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), 0x55555555);
        offset += 4;

        // Payload
        payload.CopyTo(frame.AsSpan(offset));
        offset += payload.Length;

        // FrameStatus - 非法值
        frame[offset++] = 0x77; // 非法！不是 0x00 或 0xFF

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)frameLen);
        offset += 4;

        // CRC32C
        int crcStart = 4;
        int crcLen = 4 + payload.Length + statusLen + 4;
        var crcData = frame.AsSpan(crcStart, crcLen);
        uint crc = RbfCrc.Compute(crcData);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), crc);

        var data = BuildRbfFile(frame);
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert: 
        // 对于 HeadLen=20, payloadPlusStatus=4，所有可能的解释：
        // (3, 1): Status=[0x77] 非法 → 失败
        // (2, 2): Status=[0x77, 0x77] 非法 → 失败
        // (1, 3): Status=[0x77, 0x77, 0x77] 非法 → 失败
        // (0, 4): Status=[0x77, 0x77, 0x77, 0x77] 非法 → 失败
        frames.Should().BeEmpty();
    }

    #endregion

    #region RBF-TRUNCATE-001/002: 截断测试

    /// <summary>
    /// 测试 RBF-TRUNCATE-001: 截断文件（缺少尾部 Fence）→ Resync。
    /// </summary>
    [Fact]
    public void ScanReverse_TruncatedFile_MissingTrailingFence_ResyncSucceeds() {
        // Arrange: 创建正常文件然后截断尾部 Fence
        var fullData = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x01]);
                framer.Append(new FrameTag(2), [0x02]);
            }
        );

        // 截断最后 4 字节（尾部 Fence）
        var truncatedData = fullData.AsSpan(0, fullData.Length - RbfConstants.FenceLength).ToArray();
        var scanner = new RbfScanner(truncatedData);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert: 只能找到第一帧（第二帧因缺少尾部 Fence 而无法验证）
        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(1);
    }

    /// <summary>
    /// 测试 RBF-TRUNCATE-002: 截断在帧中间 → Resync 找到更早的有效帧。
    /// </summary>
    [Fact]
    public void ScanReverse_TruncatedInMiddleOfFrame_ResyncFindsEarlierFrame() {
        // Arrange: 创建两帧，截断第二帧中间
        var fullData = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), [0x01]);
                framer.Append(new FrameTag(2), new byte[20]); // 较长的第二帧
            }
        );

        // 截断到第二帧中间（保留第一帧完整）
        // Genesis(4) + Frame1(20+4=24) + Frame2(partial)
        // Frame1 end = 4 + 24 = 28
        var truncatedData = fullData.AsSpan(0, 40).ToArray(); // 截断部分第二帧
        var scanner = new RbfScanner(truncatedData);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert: 只能找到第一帧
        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(1);
    }

    #endregion

    #region Resync 行为测试

    /// <summary>
    /// 测试 Resync: 损坏帧后的有效帧仍可被扫描到。
    /// </summary>
    [Fact]
    public void ScanReverse_CorruptFrameFollowedByValid_FindsValidFrame() {
        // Arrange: 创建 [Valid1][Corrupt][Valid2] 结构
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);

        // Frame 1: Valid
        framer.Append(new FrameTag(1), [0x01]);

        // 手动插入损坏帧（直接写入垃圾数据 + Fence）
        var corruptData = new byte[20];
        corruptData.AsSpan().Fill(0xEE); // 垃圾数据
        buffer.Write(corruptData);
        buffer.Write(RbfConstants.FenceBytes); // 尾部 Fence（使 Resync 能找到）

        // 继续用正常流程写入下一帧（需要新的 Framer 或手动写入）
        // 由于 Framer 状态已改变，我们手动构建第三帧
        var frame3 = BuildRawFrame(3, [0x03], FrameStatus.CreateValid(1));
        buffer.Write(frame3);
        buffer.Write(RbfConstants.FenceBytes);

        var scanner = new RbfScanner(buffer.WrittenMemory);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert: 应该找到 Frame3 和 Frame1（跳过损坏的中间部分）
        frames.Should().HaveCount(2);
        frames[0].FrameTag.Should().Be(3);
        frames[1].FrameTag.Should().Be(1);
    }

    /// <summary>
    /// 测试 Payload 中包含 Fence 字节时仍能正确解析。
    /// </summary>
    [Fact]
    public void ScanReverse_PayloadContainsFenceBytes_StillParsesCorrectly() {
        // Arrange: Payload 包含 "RBF1"
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0xFE11CE01), RbfConstants.FenceBytes.ToArray());
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].PayloadLength.Should().Be(4);

        var payload = scanner.ReadPayload(frames[0]);
        payload.Should().Equal(RbfConstants.FenceBytes.ToArray());
    }

    #endregion

    #region ReadPayload 测试

    /// <summary>
    /// 测试 ReadPayload 返回正确的数据。
    /// </summary>
    [Fact]
    public void ReadPayload_ReturnsCorrectData() {
        // Arrange
        byte[] expectedPayload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE];
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0x12345678), expectedPayload);
            }
        );
        var scanner = new RbfScanner(data);
        var frames = scanner.ScanReverse().ToList();

        // Act
        var payload = scanner.ReadPayload(frames[0]);

        // Assert
        payload.Should().Equal(expectedPayload);
    }

    /// <summary>
    /// 测试 ReadPayload 空 Payload 返回空数组。
    /// </summary>
    [Fact]
    public void ReadPayload_EmptyPayload_ReturnsEmptyArray() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(1), ReadOnlySpan<byte>.Empty);
            }
        );
        var scanner = new RbfScanner(data);
        var frames = scanner.ScanReverse().ToList();

        // Act
        var payload = scanner.ReadPayload(frames[0]);

        // Assert
        payload.Should().BeEmpty();
    }

    #endregion

    #region Address64 验证测试

    /// <summary>
    /// 测试 PTR-OK-001: 有效地址可解析。
    /// </summary>
    [Fact]
    public void TryReadAt_ValidPointer_Succeeds() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0x11111111), [0x01, 0x02]);
                framer.Append(new FrameTag(0x22222222), [0x03, 0x04, 0x05]);
            }
        );
        var scanner = new RbfScanner(data);

        // 获取所有帧地址
        var frames = scanner.ScanReverse().ToList();

        // Act & Assert: 每个帧地址都可解析
        foreach (var frame in frames) {
            bool success = scanner.TryReadAt(Address64.FromOffset(frame.FileOffset), out var readFrame);
            success.Should().BeTrue();
            readFrame.FrameTag.Should().Be(frame.FrameTag);
            readFrame.PayloadLength.Should().Be(frame.PayloadLength);
        }
    }

    #endregion

    #region 边界条件测试

    /// <summary>
    /// 测试最小有效帧（PayloadLen=0）。
    /// </summary>
    [Fact]
    public void ScanReverse_MinimalFrame_Succeeds() {
        // Arrange: 最小帧 = HeadLen(20) = 16 + 0 + 4
        var frame = BuildRawFrame(0x4D494E49, [], FrameStatus.CreateValid(4)); // "MINI"
        var data = BuildRbfFile(frame);
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].FrameLength.Should().Be(20);
        frames[0].PayloadLength.Should().Be(0);
    }

    /// <summary>
    /// 测试大 Payload 帧。
    /// </summary>
    [Fact]
    public void ScanReverse_LargePayload_Succeeds() {
        // Arrange
        var largePayload = new byte[4096];
        for (int i = 0; i < largePayload.Length; i++) {
            largePayload[i] = (byte)(i & 0xFF);
        }

        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0x4C415247), largePayload); // "LARG"
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].PayloadLength.Should().Be(4096);

        var payload = scanner.ReadPayload(frames[0]);
        payload.Should().Equal(largePayload);
    }

    /// <summary>
    /// 测试 FrameTag 为 0 的帧（RBF 层不保留任何值）。
    /// </summary>
    [Fact]
    public void ScanReverse_ZeroFrameTag_Succeeds() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0), [0x01]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(0);
    }

    /// <summary>
    /// 测试 FrameTag 最大值。
    /// </summary>
    [Fact]
    public void ScanReverse_MaxFrameTag_Succeeds() {
        // Arrange
        var data = CreateRbfData(
            framer => {
                framer.Append(new FrameTag(0xFFFFFFFF), [0x01]);
            }
        );
        var scanner = new RbfScanner(data);

        // Act
        var frames = scanner.ScanReverse().ToList();

        // Assert
        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(0xFFFFFFFF);
    }

    #endregion
}
