using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>测试向量验证（对应 rbf-test-vectors.md v0.40）。</summary>
/// <remarks>
/// 本文件专注于"规范验证"，与 rbf-test-vectors.md 形成 1:1 对应。
/// </remarks>
public class RbfTestVectorTests : IDisposable {
    #region RBF_LEN_* 帧长度计算测试（§1.4）

    /// <summary>RBF_LEN_001：PayloadLen = 0,1,2,3,4 时，验证 PaddingLen 和 FrameLength。</summary>
    /// <remarks>
    /// 公式：
    /// - PaddingLen = (4 - ((PayloadLen + TailMetaLen) % 4)) % 4
    /// - FrameLength = 24 + PayloadLen + TailMetaLen + PaddingLen
    /// 其中 24 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16)
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 24)]  // PayloadLen=0 → PaddingLen=0, FrameLength=24
    [InlineData(1, 3, 28)]  // PayloadLen=1 → PaddingLen=3, FrameLength=28
    [InlineData(2, 2, 28)]  // PayloadLen=2 → PaddingLen=2, FrameLength=28
    [InlineData(3, 1, 28)]  // PayloadLen=3 → PaddingLen=1, FrameLength=28
    [InlineData(4, 0, 28)]  // PayloadLen=4 → PaddingLen=0, FrameLength=28
    public void RBF_LEN_001_PayloadLen_PaddingLen_FrameLength(
        int payloadLen,
        int expectedPaddingLen,
        int expectedFrameLength) {
        // Arrange & Act
        var layout = new FrameLayout(payloadLen);

        // Assert
        Assert.Equal(expectedPaddingLen, layout.PaddingLength);
        Assert.Equal(expectedFrameLength, layout.FrameLength);
    }

    /// <summary>RBF_LEN_002：验证 PaddingLen 取值 0,3,2,1 与 (PayloadLen + TailMetaLen) % 4 的关系。</summary>
    /// <remarks>
    /// 规范：@[F-PADDING-CALCULATION]
    /// | (PayloadLen + TailMetaLen) % 4 | PaddingLen |
    /// |--------------------------------|------------|
    /// | 0                              | 0          |
    /// | 1                              | 3          |
    /// | 2                              | 2          |
    /// | 3                              | 1          |
    /// </remarks>
    [Theory]
    // mod 4 == 0 → PaddingLen = 0
    [InlineData(0, 0, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(0, 4, 0)]
    [InlineData(8, 8, 0)]
    // mod 4 == 1 → PaddingLen = 3
    [InlineData(1, 0, 3)]
    [InlineData(5, 0, 3)]
    [InlineData(0, 1, 3)]
    [InlineData(3, 2, 3)]
    // mod 4 == 2 → PaddingLen = 2
    [InlineData(2, 0, 2)]
    [InlineData(6, 0, 2)]
    [InlineData(0, 2, 2)]
    [InlineData(1, 1, 2)]
    // mod 4 == 3 → PaddingLen = 1
    [InlineData(3, 0, 1)]
    [InlineData(7, 0, 1)]
    [InlineData(0, 3, 1)]
    [InlineData(2, 1, 1)]
    public void RBF_LEN_002_PaddingLen_ModuloRelation(
        int payloadLen,
        int tailMetaLen,
        int expectedPaddingLen) {
        // Arrange & Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);

        // Assert
        Assert.Equal(expectedPaddingLen, layout.PaddingLength);
        // 验证不变量：(PayloadLen + TailMetaLen + PaddingLen) % 4 == 0
        Assert.Equal(0, (payloadLen + tailMetaLen + layout.PaddingLength) % 4);
    }

    /// <summary>RBF_LEN_003：PayloadLen=10, TailMetaLen=5 → FrameLength=40。</summary>
    /// <remarks>
    /// 计算过程：
    /// - 10 + 5 = 15
    /// - 15 % 4 = 3 → PaddingLen = 1
    /// - FrameLength = 24 + 10 + 5 + 1 = 40
    /// </remarks>
    [Fact]
    public void RBF_LEN_003_PayloadLen10_TailMetaLen5_FrameLength40() {
        // Arrange
        const int payloadLen = 10;
        const int tailMetaLen = 5;

        // Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);

        // Assert
        Assert.Equal(1, layout.PaddingLength);  // (10+5)%4=3 → padding=1
        Assert.Equal(40, layout.FrameLength);   // 24 + 10 + 5 + 1 = 40
    }

    /// <summary>验证 FrameLength 始终是 4B 对齐。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(10, 5)]
    [InlineData(17, 3)]
    [InlineData(100, 50)]
    public void FrameLength_Always4ByteAligned(int payloadLen, int tailMetaLen) {
        // Arrange & Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);

        // Assert
        Assert.Equal(0, layout.FrameLength % 4);
    }

    #endregion

    #region RBF_DESCRIPTOR_* FrameDescriptor 位域测试（§1.6）

    /// <summary>RBF_DESCRIPTOR_001：MVP 有效值枚举（8 个值）。</summary>
    /// <remarks>
    /// 规范引用：@[F-FRAME-DESCRIPTOR-LAYOUT]
    /// - bit 31: IsTombstone
    /// - bit 30-29: PaddingLen (0-3)
    /// - bit 28-16: Reserved (MUST=0)
    /// - bit 15-0: TailMetaLen (0-65535)
    /// </remarks>
    [Theory]
    // 基础组合
    [InlineData(0x00000000u, false, 0, 0)]      // 全零
    [InlineData(0x20000000u, false, 1, 0)]      // PaddingLen=1
    [InlineData(0x40000000u, false, 2, 0)]      // PaddingLen=2
    [InlineData(0x60000000u, false, 3, 0)]      // PaddingLen=3
    // Tombstone 组合
    [InlineData(0x80000000u, true, 0, 0)]       // IsTombstone=true
    [InlineData(0x80000001u, true, 0, 1)]       // IsTombstone + TailMetaLen=1
    // 边界值
    [InlineData(0x0000FFFFu, false, 0, 65535)]  // TailMetaLen 最大值
    [InlineData(0xE000FFFFu, true, 3, 65535)]   // 所有有效位满载
    public void RBF_DESCRIPTOR_001_ValidValues(
        uint expectedDescriptor,
        bool isTombstone,
        int paddingLen,
        int tailMetaLen) {
        // Act: 使用 BuildDescriptor 构建
        uint actualDescriptor = TrailerCodewordHelper.BuildDescriptor(isTombstone, paddingLen, tailMetaLen);

        // Assert: 验证构建结果与预期一致
        Assert.Equal(expectedDescriptor, actualDescriptor);

        // Assert: 验证 Reserved bits 为 0（有效值）
        Assert.True(TrailerCodewordHelper.ValidateReservedBits(actualDescriptor));

        // Act: 解析构建的 Descriptor
        byte[] buffer = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), actualDescriptor);
        var data = TrailerCodewordHelper.Parse(buffer);

        // Assert: 验证解析后各字段正确
        Assert.Equal(isTombstone, data.IsTombstone);
        Assert.Equal(paddingLen, data.PaddingLen);
        Assert.Equal(tailMetaLen, data.TailMetaLen);
    }

    /// <summary>RBF_DESCRIPTOR_002：无效值（Reserved bits 非零）。</summary>
    /// <remarks>
    /// 规范引用：@[F-FRAME-DESCRIPTOR-LAYOUT]
    /// bit 28-16 为 Reserved，MUST 为 0。
    /// </remarks>
    [Theory]
    [InlineData(0x00010000u, "Reserved bit 16 非零")]
    [InlineData(0x10000000u, "Reserved bit 28 非零")]
    [InlineData(0x1FFF0000u, "Reserved bits 28-16 全部非零")]
    public void RBF_DESCRIPTOR_002_InvalidValues_ReservedBitsNonZero(
        uint invalidDescriptor,
        string failureReason) {
        // Act & Assert: ValidateReservedBits 应返回 false
        Assert.False(
            TrailerCodewordHelper.ValidateReservedBits(invalidDescriptor),
            $"Expected false for descriptor 0x{invalidDescriptor:X8}: {failureReason}"
        );
    }

    #endregion

    #region READFRAME_CRC_* CRC 职责分离测试（§3.3 和 §4.3）

    /// <summary>READFRAME_CRC_001：TrailerCrc 正确 + PayloadCrc 被篡改 → ReadFrame/ReadPooledFrame 失败。</summary>
    /// <remarks>
    /// 规范引用：
    /// - @[F-PAYLOAD-CRC-COVERAGE] - PayloadCrc 覆盖 Payload + TailMeta + Padding
    /// - @[R-REVERSE-SCAN-USES-TRAILER-CRC] - ScanReverse 只校验 TrailerCrc
    /// 帧布局：[HeadLen(4)][Payload(N)][TailMeta(M)][Padding(P)][PayloadCrc32C(4)][TrailerCodeword(16)]
    /// PayloadCrc32C 位于帧末尾倒数 20-17 字节处（TrailerCodeword 16 字节 + PayloadCrc 4 字节）。
    /// </remarks>
    [Fact]
    public void READFRAME_CRC_001_TrailerCrcOk_PayloadCrcCorrupted_ReadFrameFails() {
        // Arrange: 写入正常帧
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];
        uint tag = 0x12345678;
        Atelia.Data.SizedPtr framePtr;

        using (var rbf = RbfFile.CreateNew(path)) {
            var appendResult = rbf.Append(tag, payload);
            Assert.True(appendResult.IsSuccess, "Append should succeed");
            framePtr = appendResult.Value!;
        }

        // 篡改 PayloadCrc（位于 TrailerCodeword 前 4 字节）
        // 帧布局：[HeadLen(4)][Payload(8)][Padding(0)][PayloadCrc(4)][TrailerCodeword(16)]
        // PayloadCrc 偏移 = framePtr.Offset + framePtr.Length - 16 - 4 = framePtr.Offset + framePtr.Length - 20
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            var layout = new FrameLayout(payload.Length);
            long payloadCrcAbsOffset = framePtr.Offset + layout.PayloadCrcOffset;

            stream.Seek(payloadCrcAbsOffset, SeekOrigin.Begin);
            // 翻转 PayloadCrc 的一个字节
            int originalByte = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.Current);
            stream.WriteByte((byte)(originalByte ^ 0xFF));
        }

        // Act & Assert: ReadFrame/ReadPooledFrame 应失败
        using var rbfRead = RbfFile.OpenExisting(path);

        // 测试 ReadPooledFrame
        var readPooledResult = rbfRead.ReadPooledFrame(framePtr);
        Assert.False(readPooledResult.IsSuccess, "ReadPooledFrame should fail when PayloadCrc is corrupted");
        Assert.IsType<RbfCrcMismatchError>(readPooledResult.Error);

        // 测试 ReadFrame
        byte[] buffer = new byte[framePtr.Length];
        var readFrameResult = rbfRead.ReadFrame(framePtr, buffer);
        Assert.False(readFrameResult.IsSuccess, "ReadFrame should fail when PayloadCrc is corrupted");
        Assert.IsType<RbfCrcMismatchError>(readFrameResult.Error);
    }

    /// <summary>READFRAME_CRC_002：TrailerCrc 正确 + PayloadCrc 被篡改 → ScanReverse 仍能枚举帧。</summary>
    /// <remarks>
    /// 规范引用：
    /// - @[R-REVERSE-SCAN-USES-TRAILER-CRC] - ScanReverse 只校验 TrailerCrc
    /// - @[S-RBF-SCANREVERSE-NO-PAYLOADCRC] - ScanReverse 不校验 PayloadCrc
    /// 验证：ScanReverse 使用 TrailerCrc（L2 信任），可以发现帧但不保证 Payload 完整性。
    /// </remarks>
    [Fact]
    public void READFRAME_CRC_002_TrailerCrcOk_PayloadCrcCorrupted_ScanReverseStillEnumerates() {
        // Arrange: 写入多帧
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        byte[] payload3 = [0x11, 0x22, 0x33];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;
        uint tag3 = 0x33333333;
        Atelia.Data.SizedPtr ptr2;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag1, payload1).IsSuccess);
            var result2 = rbf.Append(tag2, payload2);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;
            Assert.True(rbf.Append(tag3, payload3).IsSuccess);
        }

        // 篡改 Frame2 的 PayloadCrc
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            var layout = new FrameLayout(payload2.Length);
            long payloadCrcAbsOffset = ptr2.Offset + layout.PayloadCrcOffset;

            stream.Seek(payloadCrcAbsOffset, SeekOrigin.Begin);
            int originalByte = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.Current);
            stream.WriteByte((byte)(originalByte ^ 0xFF));
        }

        // Act: ScanReverse 应能成功枚举所有帧（包括 PayloadCrc 损坏的帧）
        using var rbfRead = RbfFile.OpenExisting(path);
        var scannedFrames = new List<(uint tag, int payloadLen)>();
        var enumerator = rbfRead.ScanReverse().GetEnumerator();

        while (enumerator.MoveNext()) {
            scannedFrames.Add((enumerator.Current.Tag, enumerator.Current.PayloadLength));
        }

        // Assert: 所有帧都应被枚举（逆序：Frame3, Frame2, Frame1）
        Assert.Equal(3, scannedFrames.Count);
        Assert.Equal((tag3, payload3.Length), scannedFrames[0]);
        Assert.Equal((tag2, payload2.Length), scannedFrames[1]); // PayloadCrc 损坏但仍被枚举
        Assert.Equal((tag1, payload1.Length), scannedFrames[2]);

        // TerminationError 应为 null（正常结束）
        Assert.Null(enumerator.TerminationError);

        // 进一步验证：使用已知的 ptr2 读取损坏的 Frame2 应失败
        var readResult = rbfRead.ReadPooledFrame(ptr2);
        Assert.False(readResult.IsSuccess, "ReadPooledFrame should fail for corrupted frame");
        Assert.IsType<RbfCrcMismatchError>(readResult.Error);
    }

    /// <summary>辅助方法：获取临时文件路径。</summary>
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>清理临时文件。</summary>
    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } }
            catch { /* 忽略清理错误 */ }
        }
    }

    #endregion
}
