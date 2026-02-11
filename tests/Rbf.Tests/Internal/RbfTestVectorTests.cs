using Xunit;

namespace Atelia.Rbf.Internal.Tests;

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
        int expectedFrameLength
    ) {
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
        int expectedPaddingLen
    ) {
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
        int tailMetaLen
    ) {
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
        string failureReason
    ) {
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

    #region E2E_* 端到端集成测试（Task 9.4）

    /// <summary>E2E_WriteReadRoundtrip：写入多帧 → ScanReverse → ReadFrame → 验证内容。</summary>
    /// <remarks>
    /// 验证完整的写入-读取闭环：
    /// 1. 写入 3 帧（不同 payload 长度和内容）
    /// 2. ScanReverse 枚举所有帧
    /// 3. 对每个 RbfFrameInfo 调用 ReadFrame/ReadPooledFrame
    /// 4. 验证 payload 内容正确
    /// </remarks>
    [Fact]
    public void E2E_WriteReadRoundtrip() {
        // Arrange
        var path = GetTempFilePath();

        // 不同长度和内容的 payload
        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44];
        byte[] payload3 = [0xDE, 0xAD];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;
        uint tag3 = 0x33333333;

        // Act: 写入 3 帧
        using (var file = RbfFile.CreateNew(path)) {
            var result1 = file.Append(tag1, payload1);
            Assert.True(result1.IsSuccess, "Frame 1 append should succeed");

            var result2 = file.Append(tag2, payload2);
            Assert.True(result2.IsSuccess, "Frame 2 append should succeed");

            var result3 = file.Append(tag3, payload3);
            Assert.True(result3.IsSuccess, "Frame 3 append should succeed");
        }

        // Assert: 重新打开并验证
        using (var file = RbfFile.OpenExisting(path)) {
            // ScanReverse 枚举（逆序：Frame3, Frame2, Frame1）
            var frameInfos = new List<RbfFrameInfo>();
            var enumerator = file.ScanReverse().GetEnumerator();
            while (enumerator.MoveNext()) {
                frameInfos.Add(enumerator.Current);
            }
            Assert.Null(enumerator.TerminationError);
            Assert.Equal(3, frameInfos.Count);

            // 验证 Frame3（最新）
            Assert.Equal(tag3, frameInfos[0].Tag);
            Assert.Equal(payload3.Length, frameInfos[0].PayloadLength);
            var read3 = frameInfos[0].ReadPooledFrame();
            Assert.True(read3.IsSuccess);
            using (var frame3 = read3.Value!) {
                Assert.Equal(payload3, frame3.PayloadAndMeta.ToArray());
            }

            // 验证 Frame2
            Assert.Equal(tag2, frameInfos[1].Tag);
            Assert.Equal(payload2.Length, frameInfos[1].PayloadLength);
            var read2 = frameInfos[1].ReadPooledFrame();
            Assert.True(read2.IsSuccess);
            using (var frame2 = read2.Value!) {
                Assert.Equal(payload2, frame2.PayloadAndMeta.ToArray());
            }

            // 验证 Frame1（最早）
            Assert.Equal(tag1, frameInfos[2].Tag);
            Assert.Equal(payload1.Length, frameInfos[2].PayloadLength);
            var read1 = frameInfos[2].ReadPooledFrame();
            Assert.True(read1.IsSuccess);
            using (var frame1 = read1.Value!) {
                Assert.Equal(payload1, frame1.PayloadAndMeta.ToArray());
            }
        }
    }

    /// <summary>E2E_TailMetaRoundtrip：写入带 TailMeta 的帧 → ReadTailMeta → 验证内容。</summary>
    /// <remarks>
    /// 验证 TailMeta 的完整闭环：
    /// 1. 使用 BeginAppend/EndAppend 写入带 TailMeta 的帧
    /// 2. 使用 ReadTailMeta / ReadPooledTailMeta 读取
    /// 3. 验证 TailMeta 内容正确
    /// </remarks>
    [Fact]
    public void E2E_TailMetaRoundtrip() {
        // Arrange
        var path = GetTempFilePath();

        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];  // 5 bytes TailMeta
        uint tag = 0x12345678;
        Atelia.Data.SizedPtr framePtr;

        // Act: 使用 BeginAppend 写入带 TailMeta 的帧
        using (var file = RbfFile.CreateNew(path)) {
            using (var builder = file.BeginAppend()) {
                // 写入 Payload
                var payloadSpan = builder.PayloadAndMeta.GetSpan(payload.Length);
                payload.CopyTo(payloadSpan);
                builder.PayloadAndMeta.Advance(payload.Length);

                // 写入 TailMeta
                var tailMetaSpan = builder.PayloadAndMeta.GetSpan(tailMeta.Length);
                tailMeta.CopyTo(tailMetaSpan);
                builder.PayloadAndMeta.Advance(tailMeta.Length);

                // EndAppend 时指定 tailMetaLength
                var endResult = builder.EndAppend(tag, tailMetaLength: tailMeta.Length);
                Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
                framePtr = endResult.Value;
            }
        }

        // Assert: 重新打开并验证
        using (var file = RbfFile.OpenExisting(path)) {
            // 1. 使用 ReadPooledTailMeta 验证
            var pooledResult = file.ReadPooledTailMeta(framePtr);
            Assert.True(pooledResult.IsSuccess, "ReadPooledTailMeta should succeed");
            using (var pooledTailMeta = pooledResult.Value!) {
                Assert.Equal(tailMeta.Length, pooledTailMeta.TailMeta.Length);
                Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());
            }

            // 2. 使用 ReadTailMeta（手动 buffer）验证
            byte[] buffer = new byte[tailMeta.Length];
            var manualResult = file.ReadTailMeta(framePtr, buffer);
            Assert.True(manualResult.IsSuccess, "ReadTailMeta should succeed");
            Assert.Equal(tailMeta.Length, manualResult.Value.TailMeta.Length);
            Assert.Equal(tailMeta, manualResult.Value.TailMeta.ToArray());

            // 3. 完整读取帧验证 Payload + TailMeta
            var fullResult = file.ReadPooledFrame(framePtr);
            Assert.True(fullResult.IsSuccess, "ReadPooledFrame should succeed");
            using (var frame = fullResult.Value!) {
                Assert.Equal(tag, frame.Tag);
                // PayloadAndMeta = Payload + TailMeta
                var expectedPayloadAndMeta = payload.Concat(tailMeta).ToArray();
                Assert.Equal(expectedPayloadAndMeta, frame.PayloadAndMeta.ToArray());
            }
        }
    }

    /// <summary>E2E_TombstoneFilter：写入 Valid + Tombstone + Valid → 验证过滤行为。</summary>
    /// <remarks>
    /// 验证 ScanReverse 的 Tombstone 过滤功能：
    /// 1. 写入 3 帧：Valid(tag1) + Tombstone(tag2) + Valid(tag3)
    /// 2. ScanReverse() 默认过滤 Tombstone → 返回 2 帧
    /// 3. ScanReverse(showTombstone: true) → 返回 3 帧
    /// </remarks>
    [Fact]
    public void E2E_TombstoneFilter() {
        // Arrange
        var path = GetTempFilePath();

        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD];  // Tombstone payload
        byte[] payload3 = [0x11, 0x22, 0x33, 0x44];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;  // Tombstone
        uint tag3 = 0x33333333;

        // Act: 使用 internal RbfAppendImpl 写入帧（包括 Tombstone）
        using (var file = RbfFile.CreateNew(path)) {
            // Frame 1: Valid
            var result1 = file.Append(tag1, payload1);
            Assert.True(result1.IsSuccess);

            // Frame 2: Tombstone（使用 internal API 直接写入）
            // 注意：IRbfFile.Append 不暴露 isTombstone 参数，需要使用 internal 实现
            long tailOffset = file.TailOffset;
            var handle = GetFileHandle(file);
            var result2 = RbfAppendImpl.Append(handle, ref tailOffset, payload2, default, tag2, isTombstone: true);
            Assert.True(result2.IsSuccess);
            // 手动更新 TailOffset（通过写入一个空帧来同步状态）
            // 由于无法直接修改 TailOffset，我们需要关闭并重新打开文件
        }

        // 重新打开以让文件正确识别长度
        using (var file = RbfFile.OpenExisting(path)) {
            // Frame 3: Valid
            var result3 = file.Append(tag3, payload3);
            Assert.True(result3.IsSuccess);
        }

        // Assert: 验证过滤行为
        using (var file = RbfFile.OpenExisting(path)) {
            // 1. ScanReverse() 默认过滤 → 返回 2 帧（tag3, tag1）
            var filteredFrames = new List<(uint tag, bool isTombstone)>();
            var filteredEnum = file.ScanReverse().GetEnumerator();
            while (filteredEnum.MoveNext()) {
                filteredFrames.Add((filteredEnum.Current.Tag, filteredEnum.Current.IsTombstone));
            }
            Assert.Null(filteredEnum.TerminationError);
            Assert.Equal(2, filteredFrames.Count);
            Assert.Equal(tag3, filteredFrames[0].tag);
            Assert.False(filteredFrames[0].isTombstone);
            Assert.Equal(tag1, filteredFrames[1].tag);
            Assert.False(filteredFrames[1].isTombstone);

            // 2. ScanReverse(showTombstone: true) → 返回 3 帧（tag3, tag2, tag1）
            var allFrames = new List<(uint tag, bool isTombstone)>();
            var allEnum = file.ScanReverse(showTombstone: true).GetEnumerator();
            while (allEnum.MoveNext()) {
                allFrames.Add((allEnum.Current.Tag, allEnum.Current.IsTombstone));
            }
            Assert.Null(allEnum.TerminationError);
            Assert.Equal(3, allFrames.Count);
            Assert.Equal(tag3, allFrames[0].tag);
            Assert.False(allFrames[0].isTombstone);
            Assert.Equal(tag2, allFrames[1].tag);
            Assert.True(allFrames[1].isTombstone);  // Tombstone
            Assert.Equal(tag1, allFrames[2].tag);
            Assert.False(allFrames[2].isTombstone);
        }
    }

    /// <summary>从 IRbfFile 获取内部句柄（用于测试）。</summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle GetFileHandle(IRbfFile file) {
        // 使用反射获取内部句柄
        var field = typeof(RbfFileImpl).GetField("_handle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        return (Microsoft.Win32.SafeHandles.SafeFileHandle)field!.GetValue(file)!;
    }

    /// <summary>E2E_TruncateRecovery：写入 3 帧 → Truncate → 验证只剩前 N 帧。</summary>
    /// <remarks>
    /// 验证 Truncate 恢复场景：
    /// 1. 写入 3 帧
    /// 2. Truncate 到帧 2 末尾
    /// 3. ScanReverse 只返回前 2 帧
    /// </remarks>
    [Fact]
    public void E2E_TruncateRecovery() {
        // Arrange
        var path = GetTempFilePath();

        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        byte[] payload3 = [0x11, 0x22, 0x33];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;
        uint tag3 = 0x33333333;

        Atelia.Data.SizedPtr ptr2;

        // Act: 写入 3 帧
        using (var file = RbfFile.CreateNew(path)) {
            var result1 = file.Append(tag1, payload1);
            Assert.True(result1.IsSuccess);

            var result2 = file.Append(tag2, payload2);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;

            var result3 = file.Append(tag3, payload3);
            Assert.True(result3.IsSuccess);

            // 验证写入 3 帧
            Assert.Equal(3, CountFrames(file));

            // Truncate 到帧 2 末尾
            long frame2End = ptr2.Offset + ptr2.Length + RbfLayout.FenceSize;
            file.Truncate(frame2End);

            // Assert: 只剩 2 帧
            Assert.Equal(frame2End, file.TailOffset);
            Assert.Equal(2, CountFrames(file));
        }

        // 重新打开验证持久化
        using (var file = RbfFile.OpenExisting(path)) {
            var frames = new List<(uint tag, byte[] payload)>();
            foreach (var info in file.ScanReverse()) {
                var readResult = info.ReadPooledFrame();
                Assert.True(readResult.IsSuccess);
                using var frame = readResult.Value!;
                frames.Add((frame.Tag, frame.PayloadAndMeta.ToArray()));
            }

            // 逆序扫描：先 Frame2 后 Frame1
            Assert.Equal(2, frames.Count);
            Assert.Equal(tag2, frames[0].tag);
            Assert.Equal(payload2, frames[0].payload);
            Assert.Equal(tag1, frames[1].tag);
            Assert.Equal(payload1, frames[1].payload);
        }
    }

    /// <summary>E2E_DurableFlushReopen：写入 → DurableFlush → 重新打开 → 验证内容。</summary>
    /// <remarks>
    /// 验证 DurableFlush 持久化：
    /// 1. 写入帧
    /// 2. DurableFlush 落盘
    /// 3. 关闭并重新 OpenExisting
    /// 4. 验证内容正确
    /// </remarks>
    [Fact]
    public void E2E_DurableFlushReopen() {
        // Arrange
        var path = GetTempFilePath();

        byte[] payload1 = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD];
        byte[] tailMeta = [0xEE, 0xFF, 0x11];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;

        Atelia.Data.SizedPtr ptr1, ptr2;
        long expectedTailOffset;

        // Act: 写入帧并 DurableFlush
        using (var file = RbfFile.CreateNew(path)) {
            // 写入 Frame1（简单 Append）
            var result1 = file.Append(tag1, payload1);
            Assert.True(result1.IsSuccess);
            ptr1 = result1.Value!;

            // 写入 Frame2（带 TailMeta，使用 BeginAppend）
            using (var builder = file.BeginAppend()) {
                var span = builder.PayloadAndMeta.GetSpan(payload2.Length + tailMeta.Length);
                payload2.CopyTo(span);
                tailMeta.CopyTo(span[payload2.Length..]);
                builder.PayloadAndMeta.Advance(payload2.Length + tailMeta.Length);
                var endResult = builder.EndAppend(tag2, tailMetaLength: tailMeta.Length);
                Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
                ptr2 = endResult.Value;
            }

            expectedTailOffset = file.TailOffset;

            // DurableFlush 落盘
            file.DurableFlush();
        }

        // Assert: 重新打开并验证
        using (var file = RbfFile.OpenExisting(path)) {
            // 验证 TailOffset
            Assert.Equal(expectedTailOffset, file.TailOffset);

            // 验证帧数量
            Assert.Equal(2, CountFrames(file));

            // 验证 Frame1
            var read1 = file.ReadPooledFrame(ptr1);
            Assert.True(read1.IsSuccess);
            using (var frame1 = read1.Value!) {
                Assert.Equal(tag1, frame1.Tag);
                Assert.Equal(payload1, frame1.PayloadAndMeta.ToArray());
            }

            // 验证 Frame2（Payload + TailMeta）
            var read2 = file.ReadPooledFrame(ptr2);
            Assert.True(read2.IsSuccess);
            using (var frame2 = read2.Value!) {
                Assert.Equal(tag2, frame2.Tag);
                var expectedPayloadAndMeta = payload2.Concat(tailMeta).ToArray();
                Assert.Equal(expectedPayloadAndMeta, frame2.PayloadAndMeta.ToArray());
            }

            // 验证 TailMeta 单独读取
            var tailMetaResult = file.ReadPooledTailMeta(ptr2);
            Assert.True(tailMetaResult.IsSuccess);
            using (var tm = tailMetaResult.Value!) {
                Assert.Equal(tailMeta, tm.TailMeta.ToArray());
            }
        }
    }

    /// <summary>辅助方法：统计文件中的帧数量。</summary>
    private static int CountFrames(IRbfFile file) {
        int count = 0;
        foreach (var _ in file.ScanReverse()) {
            count++;
        }
        return count;
    }

    #endregion

    #region RBF_BAD_* 损坏帧检测测试（§2.2）

    /// <summary>
    /// RBF-BAD-* 测试向量覆盖映射（rbf-test-vectors.md §2.2）。
        /// | 用例 | 描述 | 覆盖位置 |
    /// |------|------|----------|
    /// | RBF-BAD-001 | TrailerCrc 不匹配 | ReadTrailerBeforeTests（多个测试）、RbfReadImplTests |
    /// | RBF-BAD-002 | PayloadCrc 不匹配 | 本文件 READFRAME_CRC_001、RbfReadImplTests |
    /// | RBF-BAD-003 | Frame 起点非 4B 对齐 | SizedPtr 类型系统保证 4B 对齐（隐式覆盖） |
    /// | RBF-BAD-004 | TailLen 超界/不足 | ReadTrailerBeforeTests（多个边界测试） |
    /// | RBF-BAD-005 | Reserved bits 非零 | 本文件 RBF_DESCRIPTOR_002、ReadTrailerBeforeTests、TrailerCodewordHelperTests |
    /// | RBF-BAD-006 | TailLen != HeadLen | RbfReadImplTests（HeadLenMismatch/TailLenMismatch） |
    /// | RBF-BAD-007 | PaddingLen 与实际不符 | 本文件 RBF_BAD_007（本测试） + ReadTrailerBeforeTests（负 PayloadLength） |
    /// </summary>
    private const string CoverageMapping = "见上方文档注释";

    /// <summary>RBF-BAD-007：PaddingLen 与实际计算值不符 → PayloadLength 计算错误导致 ReadFrame 失败。</summary>
    /// <remarks>
    /// 规范引用：@[F-PADDING-CALCULATION]
        /// 场景：FrameDescriptor 中声明的 PaddingLen 与 `(4 - ((PayloadLen + TailMetaLen) % 4)) % 4` 不符。
        /// 预期行为：
    /// - ScanReverse 只校验 TrailerCrc，不验证 PaddingLen 一致性，可以枚举帧
    /// - ReadFrame 时，由于 PayloadLength 计算不正确，解析出的数据会有偏差
    /// - 如果 PaddingLen 声明过大导致 PayloadLength 计算为负数，则返回 FramingError
        /// 本测试验证：
    /// 1. PaddingLen 过大 → PayloadLength 负数 → FramingError（通过篡改帧直接测试）
    /// 2. PaddingLen 不一致 → 通过 ScanReverse 后 ReadFrame 验证数据不匹配
    /// </remarks>
    [Fact]
    public void RBF_BAD_007_PaddingLenMismatch_LeadsToFramingOrDataError() {
        // Arrange: 写入正常帧
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE]; // 6 字节 → 正确 PaddingLen = 2
        uint tag = 0x12345678;
        Atelia.Data.SizedPtr framePtr;

        using (var rbf = RbfFile.CreateNew(path)) {
            var appendResult = rbf.Append(tag, payload);
            Assert.True(appendResult.IsSuccess, "Append should succeed");
            framePtr = appendResult.Value!;
        }

        // 验证正常帧的 PaddingLen
        var correctLayout = new FrameLayout(payload.Length);
        Assert.Equal(2, correctLayout.PaddingLength); // (6 + 0) % 4 = 2 → PaddingLen = 2

        // 篡改 FrameDescriptor 中的 PaddingLen（将 2 改为 3）
        // 这会导致 PayloadLength 计算为 6 - 1 = 5（错误），而非正确的 6
        // 但由于 TrailerCrc 会失败，需要重新计算 TrailerCrc
        TamperFrameDescriptorPaddingLen(path, framePtr, newPaddingLen: 3);

        // Act: 使用 RbfReadImpl 底层 API 测试
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // ScanReverse 应该成功（TrailerCrc 已重新计算）
        // 但 ReadFrame 时会发现计算出的 PayloadLength 与实际不匹配
        var trailerBeforeResult = RbfReadImpl.ReadTrailerBefore(handle, new FileInfo(path).Length);
        Assert.True(trailerBeforeResult.IsSuccess, "ReadTrailerBefore should succeed (TrailerCrc valid)");

        var frameInfo = trailerBeforeResult.Value;
        // PayloadLength 应该被错误计算为 5（因为 PaddingLen 被篡改为 3，多减了 1）
        Assert.Equal(payload.Length - 1, frameInfo.PayloadLength); // 6 - 1 = 5（错误值）

        // ReadPooledFrame 仍然会成功（因为 PayloadCrc 覆盖的是实际数据）
        // 但读取的 Payload 长度会是错误的 5 字节
        var readResult = RbfReadImpl.ReadPooledFrame(handle, in frameInfo);
        Assert.True(readResult.IsSuccess, "ReadPooledFrame should succeed (PayloadCrc covers actual data)");

        using var frame = readResult.Value!;
        // 验证读取的数据长度与预期的错误值匹配
        Assert.Equal(5, frame.PayloadAndMeta.Length); // 应该是 5（错误计算结果）
        // 前 5 字节应该正确
        Assert.Equal(payload[..5], frame.PayloadAndMeta.ToArray());
    }

    /// <summary>RBF-BAD-007 扩展：PaddingLen 过大导致 PayloadLength 负数。</summary>
    /// <remarks>
    /// 规范引用：@[F-PADDING-CALCULATION]
        /// 场景：最小帧（PayloadLen=0, TailMetaLen=0, 正确 PaddingLen=0）但 FrameDescriptor 声明 PaddingLen=3。
    /// 预期：PayloadLength = TailLen(24) - FixedOverhead(24) - TailMetaLen(0) - PaddingLen(3) = -3 → FramingError
    /// </remarks>
    [Fact]
    public void RBF_BAD_007_PaddingLenTooLarge_NegativePayloadLength_FramingError() {
        // Arrange: 写入最小帧（空 payload）
        var path = GetTempFilePath();
        byte[] payload = []; // 0 字节 → 正确 PaddingLen = 0
        uint tag = 0xDEADBEEF;
        Atelia.Data.SizedPtr framePtr;

        using (var rbf = RbfFile.CreateNew(path)) {
            var appendResult = rbf.Append(tag, payload);
            Assert.True(appendResult.IsSuccess, "Append should succeed");
            framePtr = appendResult.Value!;
        }

        // 验证最小帧的 PaddingLen
        var correctLayout = new FrameLayout(payload.Length);
        Assert.Equal(0, correctLayout.PaddingLength);
        Assert.Equal(FrameLayout.MinFrameLength, correctLayout.FrameLength); // 24 字节

        // 篡改 FrameDescriptor 的 PaddingLen 为 3（非法值，会导致 PayloadLength 计算为 -3）
        TamperFrameDescriptorPaddingLen(path, framePtr, newPaddingLen: 3);

        // Act: ReadTrailerBefore 应失败（PayloadLength 为负数）
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        var result = RbfReadImpl.ReadTrailerBefore(handle, new FileInfo(path).Length);

        // Assert: 应返回 FramingError
        Assert.False(result.IsSuccess, "ReadTrailerBefore should fail when PaddingLen causes negative PayloadLength");
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("PayloadLength", result.Error!.Message);
        Assert.Contains("negative", result.Error!.Message.ToLowerInvariant());
    }

    /// <summary>辅助方法：篡改帧的 FrameDescriptor.PaddingLen 并重新计算 TrailerCrc。</summary>
    private static void TamperFrameDescriptorPaddingLen(string path, Atelia.Data.SizedPtr framePtr, int newPaddingLen) {
        byte[] fileContent = File.ReadAllBytes(path);

        // 计算 TrailerCodeword 在文件中的偏移
        // TrailerCodeword 位于帧末尾 16 字节
        int trailerOffset = (int)framePtr.Offset + framePtr.Length - TrailerCodewordHelper.Size;

        // 读取当前 TrailerCodeword
        var trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);
        var currentTrailer = TrailerCodewordHelper.Parse(trailerSpan);

        // 构建新的 FrameDescriptor（只修改 PaddingLen）
        uint newDescriptor = TrailerCodewordHelper.BuildDescriptor(
            currentTrailer.IsTombstone,
            newPaddingLen,
            currentTrailer.TailMetaLen
        );

        // 重新序列化 TrailerCodeword（自动计算新的 TrailerCrc）
        TrailerCodewordHelper.Serialize(
            trailerSpan,
            newDescriptor,
            currentTrailer.FrameTag,
            currentTrailer.TailLen
        );

        File.WriteAllBytes(path, fileContent);
    }

    #endregion
}
