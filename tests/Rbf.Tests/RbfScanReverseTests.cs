using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfFileImpl.ScanReverse 集成测试。
/// </summary>
/// <remarks>
/// 职责：验证 ScanReverse 迭代器的正确性，包括：
/// <list type="bullet">
///   <item>正常路径：多帧逆向遍历</item>
///   <item>空文件：只有 HeaderFence 时返回空序列</item>
///   <item>Tombstone 过滤行为</item>
///   <item>损坏停止语义（硬停止，不 Resync）</item>
///   <item>边界值：单帧、最小帧、最大帧</item>
/// </list>
/// 规范引用：
/// - Task 6.11 验收标准
/// - @[S-RBF-SCANREVERSE-NO-PAYLOADCRC]
/// </remarks>
public class RbfScanReverseTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 忽略清理错误 */ }
        }
    }

    #region 辅助方法

    /// <summary>
    /// 创建一个新的 RBF 文件并写入指定帧数据。
    /// </summary>
    private IRbfFile CreateRbfWithFrames(string path, params (uint tag, byte[] payload, bool isTombstone)[] frames) {
        var rbf = RbfFile.CreateNew(path);
        foreach (var (tag, payload, isTombstone) in frames) {
            if (isTombstone) {
                // 目前没有直接写 Tombstone 的 API，需要手动构造
                AppendTombstoneFrame(rbf, tag, payload);
            }
            else {
                rbf.Append(tag, payload);
            }
        }
        return rbf;
    }

    /// <summary>
    /// 手动构造并追加墓碑帧（绕过标准 API）。
    /// </summary>
    private void AppendTombstoneFrame(IRbfFile rbf, uint tag, ReadOnlySpan<byte> payload) {
        // 关闭当前文件，手动追加墓碑帧，再重新打开
        var path = GetFilePath(rbf);
        var tailOffset = rbf.TailOffset;
        rbf.Dispose();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.Seek(tailOffset, SeekOrigin.Begin);

        // 构造墓碑帧
        byte[] frameBytes = CreateFrameBytes(tag, payload, isTombstone: true);
        stream.Write(frameBytes);
        stream.Write(RbfLayout.Fence);
        stream.Flush();
    }

    /// <summary>
    /// 获取 RbfFile 对应的文件路径（通过反射或重新打开）。
    /// </summary>
    private static string GetFilePath(IRbfFile rbf) {
        // 这是一个测试辅助方法，依赖内部实现
        // 实际测试中，我们直接用 path 变量
        throw new NotImplementedException("Use path variable directly");
    }

    /// <summary>
    /// 构造一个有效帧的字节数组（v0.40 格式）。
    /// </summary>
    private static byte[] CreateFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;

        byte[] frame = new byte[frameLen];
        Span<byte> span = frame;

        // HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(span[..FrameLayout.HeadLenSize], (uint)frameLen);
        // Payload
        payload.CopyTo(span.Slice(FrameLayout.PayloadOffset, payload.Length));
        // Padding（清零）
        if (layout.PaddingLength > 0) {
            span.Slice(layout.PaddingOffset, layout.PaddingLength).Clear();
        }
        // PayloadCrc
        var payloadCrcCoverage = span.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint payloadCrc = Crc32CHelper.Compute(payloadCrcCoverage);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize), payloadCrc);
        // TrailerCodeword
        layout.FillTrailer(span.Slice(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, isTombstone);

        return frame;
    }

    /// <summary>
    /// 在文件指定位置手动写入帧（用于构造带墓碑帧的测试文件）。
    /// </summary>
    private static void WriteFrameAt(FileStream stream, long offset, uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] frameBytes = CreateFrameBytes(tag, payload, isTombstone);
        stream.Write(frameBytes);
        stream.Write(RbfLayout.Fence);
    }

    /// <summary>
    /// 创建一个带有多种帧类型的测试文件（包括正常帧和墓碑帧）。
    /// </summary>
    private string CreateTestFileWithMixedFrames(params (uint tag, byte[] payload, bool isTombstone)[] frames) {
        var path = GetTempFilePath();

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        // 写入 HeaderFence
        stream.Write(RbfLayout.Fence);

        foreach (var (tag, payload, isTombstone) in frames) {
            byte[] frameBytes = CreateFrameBytes(tag, payload, isTombstone);
            stream.Write(frameBytes);
            stream.Write(RbfLayout.Fence);
        }

        return path;
    }

    /// <summary>
    /// 损坏文件中指定帧的 TrailerCrc。
    /// </summary>
    private static void CorruptTrailerCrcAt(string path, long frameOffset, int payloadLen) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var layout = new FrameLayout(payloadLen);
        long trailerCrcOffset = frameOffset + layout.TrailerCodewordOffset;
        stream.Seek(trailerCrcOffset, SeekOrigin.Begin);

        // 翻转 TrailerCrc 的第一个字节
        int originalByte = stream.ReadByte();
        stream.Seek(-1, SeekOrigin.Current);
        stream.WriteByte((byte)(originalByte ^ 0xFF));
    }

    #endregion

    #region 正常路径测试

    /// <summary>
    /// 验证多帧文件逆向遍历顺序正确（从尾到头）。
    /// </summary>
    [Fact]
    public void ScanReverse_MultipleFrames_ReturnsInReverseOrder() {
        // Arrange: 写入 3 帧
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02];
        byte[] payload2 = [0xAA, 0xBB, 0xCC];
        byte[] payload3 = [0x11, 0x22, 0x33, 0x44];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;
        uint tag3 = 0x33333333;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag1, payload1);
            rbf.Append(tag2, payload2);
            rbf.Append(tag3, payload3);
        }

        // Act: 重新打开并逆向扫描
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, int payloadLen)>();

        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add((info.Tag, info.PayloadLength));
        }

        // Assert: 顺序应为 Frame3 → Frame2 → Frame1
        Assert.Equal(3, frames.Count);
        Assert.Equal((tag3, payload3.Length), frames[0]);
        Assert.Equal((tag2, payload2.Length), frames[1]);
        Assert.Equal((tag1, payload1.Length), frames[2]);
    }

    /// <summary>
    /// 验证 ScanReverse 返回的 Ticket 可用于 ReadFrame。
    /// </summary>
    [Fact]
    public void ScanReverse_TicketUsableForReadFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE];
        uint tag1 = 0xAAAAAAAA;
        uint tag2 = 0xBBBBBBBB;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag1, payload1);
            rbf.Append(tag2, payload2);
        }

        // Act & Assert
        using var rbfRead = RbfFile.OpenExisting(path);
        var infos = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse()) {
            infos.Add(info);
        }

        Assert.Equal(2, infos.Count);

        // 使用 Ticket 读取帧内容
        var result2 = rbfRead.ReadPooledFrame(infos[0]); // Frame2
        Assert.True(result2.IsSuccess);
        using var frame2 = result2.Value!;
        Assert.Equal(tag2, frame2.Tag);
        Assert.Equal(payload2, frame2.Payload.ToArray());

        var result1 = rbfRead.ReadPooledFrame(infos[1]); // Frame1
        Assert.True(result1.IsSuccess);
        using var frame1 = result1.Value!;
        Assert.Equal(tag1, frame1.Tag);
        Assert.Equal(payload1, frame1.Payload.ToArray());
    }

    /// <summary>
    /// 验证 ScanReverse 正常结束时 TerminationError 为 null。
    /// </summary>
    [Fact]
    public void ScanReverse_NormalCompletion_TerminationErrorIsNull() {
        // Arrange
        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(0x12345678, [0x01, 0x02, 0x03]);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var enumerator = rbfRead.ScanReverse().GetEnumerator();
        while (enumerator.MoveNext()) {
            // 消费所有帧
        }

        // Assert
        Assert.Null(enumerator.TerminationError);
    }

    #endregion

    #region 空文件测试

    /// <summary>
    /// 验证空文件（只有 HeaderFence）返回空序列。
    /// </summary>
    [Fact]
    public void ScanReverse_EmptyFile_ReturnsEmptySequence() {
        // Arrange: 创建只有 HeaderFence 的空文件
        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            // 不写入任何帧
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add(info);
        }

        // Assert
        Assert.Empty(frames);
    }

    /// <summary>
    /// 验证空文件扫描后 TerminationError 为 null（正常到达文件头）。
    /// </summary>
    [Fact]
    public void ScanReverse_EmptyFile_TerminationErrorIsNull() {
        // Arrange
        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            // 不写入任何帧
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var enumerator = rbfRead.ScanReverse().GetEnumerator();
        bool hasAny = enumerator.MoveNext();

        // Assert
        Assert.False(hasAny);
        Assert.Null(enumerator.TerminationError);
    }

    #endregion

    #region Tombstone 过滤测试

    /// <summary>
    /// 验证 showTombstone=false 时跳过 Tombstone 帧。
    /// </summary>
    [Fact]
    public void ScanReverse_ShowTombstoneFalse_SkipsTombstones() {
        // Arrange: 创建混合帧文件（正常帧 + 墓碑帧）
        var path = CreateTestFileWithMixedFrames(
            (0x11111111, [0x01, 0x02], false),       // Frame1: 正常
            (0x22222222, [0xAA, 0xBB, 0xCC], true),  // Frame2: 墓碑
            (0x33333333, [0x11, 0x22], false)        // Frame3: 正常
        );

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, bool isTombstone)>();
        foreach (var info in rbfRead.ScanReverse(showTombstone: false)) {
            frames.Add((info.Tag, info.IsTombstone));
        }

        // Assert: 应只返回 Frame3 和 Frame1（跳过 Frame2 墓碑）
        Assert.Equal(2, frames.Count);
        Assert.Equal((0x33333333u, false), frames[0]); // Frame3
        Assert.Equal((0x11111111u, false), frames[1]); // Frame1
    }

    /// <summary>
    /// 验证 showTombstone=true 时包含 Tombstone 帧。
    /// </summary>
    [Fact]
    public void ScanReverse_ShowTombstoneTrue_IncludesTombstones() {
        // Arrange: 创建混合帧文件
        var path = CreateTestFileWithMixedFrames(
            (0x11111111, [0x01, 0x02], false),       // Frame1: 正常
            (0x22222222, [0xAA, 0xBB, 0xCC], true),  // Frame2: 墓碑
            (0x33333333, [0x11, 0x22], false)        // Frame3: 正常
        );

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, bool isTombstone)>();
        foreach (var info in rbfRead.ScanReverse(showTombstone: true)) {
            frames.Add((info.Tag, info.IsTombstone));
        }

        // Assert: 应返回所有 3 帧（逆序）
        Assert.Equal(3, frames.Count);
        Assert.Equal((0x33333333u, false), frames[0]); // Frame3
        Assert.Equal((0x22222222u, true), frames[1]);  // Frame2 (墓碑)
        Assert.Equal((0x11111111u, false), frames[2]); // Frame1
    }

    /// <summary>
    /// 验证全部是 Tombstone 时 showTombstone=false 返回空序列。
    /// </summary>
    [Fact]
    public void ScanReverse_AllTombstones_ShowFalse_ReturnsEmpty() {
        // Arrange: 创建全墓碑文件
        var path = CreateTestFileWithMixedFrames(
            (0x11111111, [0x01], true),
            (0x22222222, [0x02], true)
        );

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse(showTombstone: false)) {
            frames.Add(info);
        }

        // Assert
        Assert.Empty(frames);
    }

    #endregion

    #region 损坏停止测试

    /// <summary>
    /// 验证 TrailerCrc 损坏时硬停止，TerminationError 非空。
    /// </summary>
    [Fact]
    public void ScanReverse_CorruptedTrailerCrc_HardStopsWithError() {
        // Arrange: 创建 3 帧文件，损坏第 2 帧的 TrailerCrc
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02];
        byte[] payload2 = [0xAA, 0xBB, 0xCC];
        byte[] payload3 = [0x11, 0x22, 0x33, 0x44];

        SizedPtr ptr2;
        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(0x11111111, payload1);
            ptr2 = rbf.Append(0x22222222, payload2);
            rbf.Append(0x33333333, payload3);
        }

        // 损坏 Frame2 的 TrailerCrc
        CorruptTrailerCrcAt(path, ptr2.Offset, payload2.Length);

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<uint>();
        var enumerator = rbfRead.ScanReverse().GetEnumerator();
        while (enumerator.MoveNext()) {
            frames.Add(enumerator.Current.Tag);
        }

        // Assert:
        // - Frame3 应正常返回
        // - Frame2 损坏 → 硬停止
        // - Frame1 不可达
        Assert.Single(frames);
        Assert.Equal(0x33333333u, frames[0]); // 只有 Frame3

        Assert.NotNull(enumerator.TerminationError);
        Assert.IsType<RbfFramingError>(enumerator.TerminationError);
    }

    /// <summary>
    /// 验证硬停止语义：损坏帧之前的帧仍正确产出（不 Resync）。
    /// </summary>
    [Fact]
    public void ScanReverse_CorruptedMiddleFrame_PrecedingFramesStillCorrect() {
        // Arrange: 创建 5 帧文件，损坏第 3 帧
        var path = GetTempFilePath();
        byte[] payload1 = [0x01];
        byte[] payload2 = [0x02, 0x03];
        byte[] payload3 = [0x04, 0x05, 0x06];
        byte[] payload4 = [0x07, 0x08, 0x09, 0x0A];
        byte[] payload5 = [0x0B, 0x0C];

        SizedPtr ptr3;
        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(0x11111111, payload1);
            rbf.Append(0x22222222, payload2);
            ptr3 = rbf.Append(0x33333333, payload3);
            rbf.Append(0x44444444, payload4);
            rbf.Append(0x55555555, payload5);
        }

        // 损坏 Frame3
        CorruptTrailerCrcAt(path, ptr3.Offset, payload3.Length);

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, int payloadLen)>();
        var enumerator = rbfRead.ScanReverse().GetEnumerator();
        while (enumerator.MoveNext()) {
            frames.Add((enumerator.Current.Tag, enumerator.Current.PayloadLength));
        }

        // Assert:
        // - Frame5, Frame4 正常返回（按逆序）
        // - Frame3 损坏 → 硬停止
        // - Frame2, Frame1 不可达
        Assert.Equal(2, frames.Count);
        Assert.Equal((0x55555555u, payload5.Length), frames[0]); // Frame5
        Assert.Equal((0x44444444u, payload4.Length), frames[1]); // Frame4

        // 已产出的帧数据完全正确
        Assert.NotNull(enumerator.TerminationError);
    }

    /// <summary>
    /// 验证第一帧（最新帧）损坏时立即停止，返回空序列但有错误。
    /// </summary>
    [Fact]
    public void ScanReverse_FirstFrameCorrupted_ImmediateStopWithError() {
        // Arrange: 创建 2 帧文件，损坏最后一帧（逆向扫描的第一帧）
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD];

        SizedPtr ptr2;
        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(0x11111111, payload1);
            ptr2 = rbf.Append(0x22222222, payload2);
        }

        // 损坏 Frame2（逆向扫描的第一帧）
        CorruptTrailerCrcAt(path, ptr2.Offset, payload2.Length);

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        var enumerator = rbfRead.ScanReverse().GetEnumerator();
        while (enumerator.MoveNext()) {
            frames.Add(enumerator.Current);
        }

        // Assert: 没有帧返回，但有错误
        Assert.Empty(frames);
        Assert.NotNull(enumerator.TerminationError);
        Assert.IsType<RbfFramingError>(enumerator.TerminationError);
    }

    #endregion

    #region 边界值测试

    /// <summary>
    /// 验证单帧文件的逆向扫描。
    /// </summary>
    [Fact]
    public void ScanReverse_SingleFrame_ReturnsOneFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        uint tag = 0x12345678;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag, payload);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add(info);
        }

        // Assert
        Assert.Single(frames);
        Assert.Equal(tag, frames[0].Tag);
        Assert.Equal(payload.Length, frames[0].PayloadLength);
        Assert.False(frames[0].IsTombstone);
    }

    /// <summary>
    /// 验证最小帧（空 payload，24B）的逆向扫描。
    /// </summary>
    [Fact]
    public void ScanReverse_MinimalFrame_EmptyPayload() {
        // Arrange: 空 payload 帧
        var path = GetTempFilePath();
        byte[] payload = [];
        uint tag = 0xDEADBEEF;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag, payload);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add(info);
        }

        // Assert
        Assert.Single(frames);
        Assert.Equal(tag, frames[0].Tag);
        Assert.Equal(0, frames[0].PayloadLength);
        Assert.Equal(FrameLayout.MinFrameLength, frames[0].Ticket.Length); // 24B
    }

    /// <summary>
    /// 验证大 payload 帧的逆向扫描。
    /// </summary>
    [Fact]
    public void ScanReverse_LargePayloadFrame() {
        // Arrange: 64KB payload
        var path = GetTempFilePath();
        byte[] payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        uint tag = 0xCAFEBABE;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag, payload);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add(info);
        }

        // Assert
        Assert.Single(frames);
        Assert.Equal(tag, frames[0].Tag);
        Assert.Equal(payload.Length, frames[0].PayloadLength);
    }

    /// <summary>
    /// 验证不同 payload 对齐的帧（padding 边界测试）。
    /// </summary>
    [Theory]
    [InlineData(0)]   // paddingLen = 0
    [InlineData(1)]   // paddingLen = 3
    [InlineData(2)]   // paddingLen = 2
    [InlineData(3)]   // paddingLen = 1
    [InlineData(4)]   // paddingLen = 0
    [InlineData(7)]   // paddingLen = 1
    [InlineData(100)] // 较大 payload
    public void ScanReverse_VariousPayloadSizes_ReturnsCorrectLength(int payloadLen) {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0) new Random(payloadLen).NextBytes(payload);
        uint tag = (uint)(0x10000000 + payloadLen);

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag, payload);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<RbfFrameInfo>();
        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add(info);
        }

        // Assert
        Assert.Single(frames);
        Assert.Equal(tag, frames[0].Tag);
        Assert.Equal(payloadLen, frames[0].PayloadLength);
    }

    /// <summary>
    /// 验证多帧逆向扫描时 Ticket.Offset 正确（每帧偏移递减）。
    /// </summary>
    [Fact]
    public void ScanReverse_MultipleFrames_TicketOffsetsAreDecreasing() {
        // Arrange
        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(0x11111111, [0x01, 0x02]);
            rbf.Append(0x22222222, [0x03, 0x04, 0x05]);
            rbf.Append(0x33333333, [0x06, 0x07, 0x08, 0x09]);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var offsets = new List<long>();
        foreach (var info in rbfRead.ScanReverse()) {
            offsets.Add(info.Ticket.Offset);
        }

        // Assert: 偏移应递减
        Assert.Equal(3, offsets.Count);
        Assert.True(offsets[0] > offsets[1], $"Expected {offsets[0]} > {offsets[1]}");
        Assert.True(offsets[1] > offsets[2], $"Expected {offsets[1]} > {offsets[2]}");
    }

    #endregion

    #region ReadFrame(in RbfFrameInfo, ...) 便捷重载测试

    /// <summary>
    /// 验证 ReadFrame(in RbfFrameInfo, Span) 重载正确工作。
    /// </summary>
    [Fact]
    public void ReadFrame_WithFrameInfo_ReturnsCorrectFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];
        uint tag = 0x12345678;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag, payload);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var scanInfo in rbfRead.ScanReverse()) {
            frameInfo = scanInfo;
            break; // 只取第一帧
        }

        Assert.NotNull(frameInfo);
        var info = frameInfo.Value;
        byte[] buffer = new byte[info.Ticket.Length];
        var result = rbfRead.ReadFrame(in info, buffer);

        // Assert
        Assert.True(result.IsSuccess);
        var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    /// <summary>
    /// 验证 ReadPooledFrame(in RbfFrameInfo) 重载正确工作。
    /// </summary>
    [Fact]
    public void ReadPooledFrame_WithFrameInfo_ReturnsCorrectFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x11, 0x22, 0x33, 0x44, 0x55];
        uint tag = 0xABCDEF00;

        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(tag, payload);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var scanInfo in rbfRead.ScanReverse()) {
            frameInfo = scanInfo;
            break;
        }

        Assert.NotNull(frameInfo);
        var info = frameInfo.Value;
        var result = rbfRead.ReadPooledFrame(in info);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    #endregion

    #region PayloadCrc 不校验测试

    /// <summary>
    /// 验证 ScanReverse 不校验 PayloadCrc（@[S-RBF-SCANREVERSE-NO-PAYLOADCRC]）。
    /// </summary>
    /// <remarks>
    /// 当 Payload 被破坏但 TrailerCodeword 完好时，ScanReverse 仍应成功返回帧信息。
    /// </remarks>
    [Fact]
    public void ScanReverse_CorruptedPayload_StillSucceeds() {
        // Arrange: 创建多帧文件，然后篡改某帧的 payload
        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            rbf.Append(1, [0x01, 0x02, 0x03, 0x04]); // Frame 1
            rbf.Append(2, [0x11, 0x12, 0x13, 0x14]); // Frame 2
            rbf.Append(3, [0x21, 0x22, 0x23, 0x24]); // Frame 3
        }

        // 篡改 Frame 2 的 payload（不动 trailer）
        byte[] fileContent = File.ReadAllBytes(path);
        // Frame 1 at offset 4, Frame 2 somewhere after
        // 找到 Frame 2 的位置：Header(4) + Frame1(24+4payload) = 32, Frame2 starts at 32
        // Payload offset within frame = 4 (HeadLen)
        int frame2PayloadOffset = 4 + (24 + 4) + 4; // Header + Frame1 + HeadLen
        fileContent[frame2PayloadOffset] ^= 0xFF; // 翻转一个字节
        File.WriteAllBytes(path, fileContent);

        // Act: ScanReverse 应该仍然成功
        using var rbfRead = RbfFile.OpenExisting(path);
        var tags = new List<uint>();
        AteliaError? error = null;

        foreach (var info in rbfRead.ScanReverse()) {
            tags.Add(info.Tag);
        }

        // 获取 TerminationError（需要手动遍历一次）
        var enumerator = rbfRead.ScanReverse().GetEnumerator();
        while (enumerator.MoveNext()) { }
        error = enumerator.TerminationError;

        // Assert: 应该成功扫描所有帧
        Assert.Equal(3, tags.Count);
        Assert.Equal([3u, 2u, 1u], tags); // 逆序
        Assert.Null(error); // 没有错误
    }

    #endregion

    #region 写入边界测试

    /// <summary>
    /// 验证 ScanReverse 能处理跨缓冲区边界的帧（使用 RbfAppendImpl 边界向量）。
    /// </summary>
    [Fact]
    public void ScanReverse_AcrossBufferBoundaries_WorksCorrectly() {
        // Arrange: 使用 RbfAppendImpl 的边界向量
        var edgeCases = RbfAppendImpl.GetPayloadEdgeCase();
        // edgeCases: [OneMax, TwoMin, TwoMax, ThreeMin]
        // OneMax = 4KB - 24 = 4072 (单次写入最大)
        // TwoMin = OneMax + 1 (触发两次写入)

        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            // 写入边界大小的帧
            rbf.Append(1, new byte[edgeCases[0]]); // OneMax: 单次写入最大
            rbf.Append(2, new byte[edgeCases[1]]); // TwoMin: 触发两次写入
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, int payloadLen)>();
        foreach (var info in rbfRead.ScanReverse()) {
            frames.Add((info.Tag, info.PayloadLength));
        }

        // Assert
        Assert.Equal(2, frames.Count);
        Assert.Equal((2u, edgeCases[1]), frames[0]); // TwoMin
        Assert.Equal((1u, edgeCases[0]), frames[1]); // OneMax
    }

    #endregion
}
