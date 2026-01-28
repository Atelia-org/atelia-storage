using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>RbfFileImpl.ReadTailMeta / ReadPooledTailMeta 集成测试。</summary>
/// <remarks>
/// 职责：验证 TailMeta 预览读取的正确性，包括：   正常路径：带 TailMeta 的帧读取
/// - 空 TailMeta 场景
/// - Buffer 大小校验
/// - 与 ScanReverse 的工作流集成
/// - 预览 → 完整读取工作流
/// 规范引用：
/// - IRbfFile.ReadTailMeta / ReadPooledTailMeta
/// - L2 信任级别（仅保证 TrailerCrc）
/// </remarks>
public class RbfReadTailMetaTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } }
            catch { /* 忽略清理错误 */ }
        }
    }

    #region 辅助方法

    /// <summary>构造一个有效帧的字节数组（v0.40 格式，带 TailMeta）。</summary>
    private static byte[] CreateFrameBytes(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta, bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length, tailMeta.Length);
        int frameLen = layout.FrameLength;

        byte[] frame = new byte[frameLen];
        Span<byte> span = frame;

        // HeadLen
        BinaryPrimitives.WriteUInt32LittleEndian(span[..FrameLayout.HeadLenSize], (uint)frameLen);
        // Payload
        payload.CopyTo(span.Slice(FrameLayout.PayloadOffset, payload.Length));
        // TailMeta
        if (tailMeta.Length > 0) {
            tailMeta.CopyTo(span.Slice(layout.TailMetaOffset, tailMeta.Length));
        }
        // Padding（清零）
        if (layout.PaddingLength > 0) {
            span.Slice(layout.PaddingOffset, layout.PaddingLength).Clear();
        }
        // PayloadCrc
        var payloadCrcCoverage = span.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint payloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize), payloadCrc);
        // TrailerCodeword
        layout.FillTrailer(span.Slice(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, isTombstone);

        return frame;
    }

    /// <summary>创建一个带有多种帧类型的测试文件（支持 TailMeta）。</summary>
    private string CreateTestFileWithFrames(params (uint tag, byte[] payload, byte[] tailMeta, bool isTombstone)[] frames) {
        var path = GetTempFilePath();

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        // 写入 HeaderFence
        stream.Write(RbfLayout.Fence);

        foreach (var (tag, payload, tailMeta, isTombstone) in frames) {
            byte[] frameBytes = CreateFrameBytes(tag, payload, tailMeta, isTombstone);
            stream.Write(frameBytes);
            stream.Write(RbfLayout.Fence);
        }

        return path;
    }

    #endregion

    #region ReadTailMeta 正常路径测试

    /// <summary>验证 ReadTailMeta 能正确读取带 TailMeta 的帧。</summary>
    [Fact]
    public void ReadTailMeta_WithTailMeta_ReturnsCorrectData() {
        // Arrange: 创建带 TailMeta 的帧
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        uint tag = 0x12345678;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload, tailMeta).IsSuccess);
        }

        // Act: ScanReverse 获取 info，然后 ReadTailMeta
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;

        // 验证 ScanReverse 返回的 TailMetaLength 正确
        Assert.Equal(tailMeta.Length, scanInfo.TailMetaLength);

        // ReadTailMeta
        byte[] buffer = new byte[scanInfo.TailMetaLength];
        var result = scanInfo.ReadTailMeta(buffer);

        // Assert
        Assert.True(result.IsSuccess);
        var tailMetaFrame = result.Value;
        Assert.Equal(tag, tailMetaFrame.Tag);
        Assert.Equal(tailMeta, tailMetaFrame.TailMeta.ToArray());
        Assert.False(tailMetaFrame.IsTombstone);
        Assert.Equal(scanInfo.Ticket.Offset, tailMetaFrame.Ticket.Offset);
        Assert.Equal(scanInfo.Ticket.Length, tailMetaFrame.Ticket.Length);
    }

    /// <summary>验证 ReadTailMeta 处理空 TailMeta（TailMetaLength = 0）。</summary>
    [Fact]
    public void ReadTailMeta_EmptyTailMeta_ReturnsEmptySpan() {
        // Arrange: 创建不带 TailMeta 的帧
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        uint tag = 0xCAFEBABE;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload).IsSuccess); // 无 TailMeta
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        Assert.Equal(0, scanInfo.TailMetaLength);

        // ReadTailMeta 应该返回成功 + 空 Span
        byte[] buffer = new byte[16]; // 提供一个 buffer（虽然不会用到）
        var result = scanInfo.ReadTailMeta(buffer);

        // Assert
        Assert.True(result.IsSuccess);
        var tailMetaFrame = result.Value;
        Assert.Equal(tag, tailMetaFrame.Tag);
        Assert.True(tailMetaFrame.TailMeta.IsEmpty);
        Assert.False(tailMetaFrame.IsTombstone);
    }

    #endregion

    #region ReadPooledTailMeta 测试

    /// <summary>验证 ReadPooledTailMeta 能正确读取带 TailMeta 的帧。</summary>
    [Fact]
    public void ReadPooledTailMeta_WithTailMeta_ReturnsCorrectData() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x11, 0x22, 0x33];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD];
        uint tag = 0xDEADBEEF;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload, tailMeta).IsSuccess);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        var result = scanInfo.ReadPooledTailMeta();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var pooledTailMeta = result.Value;

        Assert.Equal(tag, pooledTailMeta.Tag);
        Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());
        Assert.False(pooledTailMeta.IsTombstone);
        Assert.Equal(scanInfo.Ticket.Offset, pooledTailMeta.Ticket.Offset);
        Assert.Equal(scanInfo.Ticket.Length, pooledTailMeta.Ticket.Length);
    }

    /// <summary>验证 ReadPooledTailMeta Dispose 后访问 TailMeta 抛异常。</summary>
    [Fact]
    public void ReadPooledTailMeta_AfterDispose_ThrowsObjectDisposedException() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01];
        byte[] tailMeta = [0xAA, 0xBB];
        uint tag = 0x11111111;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload, tailMeta).IsSuccess);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        var result = scanInfo.ReadPooledTailMeta();

        Assert.True(result.IsSuccess);
        var pooledTailMeta = result.Value!;

        // 先验证可以访问
        Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());

        // Dispose
        pooledTailMeta.Dispose();

        // Assert: Dispose 后访问抛 ObjectDisposedException
        Assert.Throws<ObjectDisposedException>(
            () => { _ = pooledTailMeta.TailMeta; }
        );
    }

    /// <summary>验证 ReadPooledTailMeta 处理空 TailMeta。</summary>
    [Fact]
    public void ReadPooledTailMeta_EmptyTailMeta_ReturnsSuccessWithEmptySpan() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        uint tag = 0xABCDEF00;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload).IsSuccess); // 无 TailMeta
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        Assert.Equal(0, scanInfo.TailMetaLength);

        var result = scanInfo.ReadPooledTailMeta();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var pooledTailMeta = result.Value;

        Assert.Equal(tag, pooledTailMeta.Tag);
        Assert.True(pooledTailMeta.TailMeta.IsEmpty);

        // TailMetaLength = 0 时不租 buffer，Dispose 后访问应仍返回空 Span（不抛异常）
        pooledTailMeta.Dispose();
        Assert.True(pooledTailMeta.TailMeta.IsEmpty); // 空 TailMeta 始终返回空 Span
    }

    #endregion

    #region Buffer 校验测试

    /// <summary>验证 Buffer 太小时返回 RbfBufferTooSmallError。</summary>
    [Fact]
    public void ReadTailMeta_BufferTooSmall_ReturnsError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]; // 6 字节
        uint tag = 0x12345678;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload, tailMeta).IsSuccess);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        Assert.Equal(tailMeta.Length, scanInfo.TailMetaLength);

        // 提供一个太小的 buffer
        byte[] smallBuffer = new byte[3]; // 小于 TailMetaLength (6)
        var result = scanInfo.ReadTailMeta(smallBuffer);

        // Assert
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.IsType<RbfBufferTooSmallError>(result.Error);

        var error = (RbfBufferTooSmallError)result.Error;
        Assert.Equal(tailMeta.Length, error.RequiredBytes);
        Assert.Equal(smallBuffer.Length, error.ProvidedBytes);
    }

    #endregion

    #region 工作流集成测试

    /// <summary>验证完整工作流：写入多帧 → ScanReverse → ReadTailMeta。</summary>
    [Fact]
    public void ReadTailMeta_WorkflowWithScanReverse() {
        // Arrange: 写入 3 帧，各有不同的 TailMeta
        var path = GetTempFilePath();
        byte[] payload1 = [0x01];
        byte[] tailMeta1 = [0xA1];
        byte[] payload2 = [0x02, 0x03];
        byte[] tailMeta2 = [0xB1, 0xB2, 0xB3];
        byte[] payload3 = [0x04, 0x05, 0x06];
        byte[] tailMeta3 = [0xC1, 0xC2];

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, payload1, tailMeta1).IsSuccess);
            Assert.True(rbf.Append(0x22222222, payload2, tailMeta2).IsSuccess);
            Assert.True(rbf.Append(0x33333333, payload3, tailMeta3).IsSuccess);
        }

        // Act: ScanReverse 并逐帧读取 TailMeta
        using var rbfRead = RbfFile.OpenExisting(path);
        var results = new List<(uint tag, byte[] tailMeta)>();

        foreach (var info in rbfRead.ScanReverse()) {
            byte[] buffer = new byte[info.TailMetaLength];
            var result = info.ReadTailMeta(buffer);
            Assert.True(result.IsSuccess);
            results.Add((result.Value.Tag, result.Value.TailMeta.ToArray()));
        }

        // Assert: 逆序读取
        Assert.Equal(3, results.Count);

        Assert.Equal(0x33333333u, results[0].tag);
        Assert.Equal(tailMeta3, results[0].tailMeta);

        Assert.Equal(0x22222222u, results[1].tag);
        Assert.Equal(tailMeta2, results[1].tailMeta);

        Assert.Equal(0x11111111u, results[2].tag);
        Assert.Equal(tailMeta1, results[2].tailMeta);
    }

    /// <summary>验证预览 → 完整读取工作流：ReadTailMeta → ReadFrame。</summary>
    [Fact]
    public void ReadTailMeta_PreviewThenReadFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] tailMeta = [0x11, 0x22, 0x33, 0x44, 0x55];
        uint tag = 0xCAFEBABE;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload, tailMeta).IsSuccess);
        }

        // Act: 先 ReadTailMeta 预览，再 ReadFrame 读取完整帧
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;

        // Step 1: ReadTailMeta 预览
        byte[] tailMetaBuffer = new byte[scanInfo.TailMetaLength];
        var previewResult = scanInfo.ReadTailMeta(tailMetaBuffer);
        Assert.True(previewResult.IsSuccess);
        var previewFrame = previewResult.Value;

        // Step 2: 使用 Ticket 调用 ReadFrame 读取完整帧
        byte[] frameBuffer = new byte[previewFrame.Ticket.Length];
        var fullResult = rbfRead.ReadFrame(previewFrame.Ticket, frameBuffer);
        Assert.True(fullResult.IsSuccess);
        var fullFrame = fullResult.Value;

        // Assert: 两者的 TailMeta 应一致
        Assert.Equal(tag, previewFrame.Tag);
        Assert.Equal(tag, fullFrame.Tag);
        Assert.Equal(previewFrame.IsTombstone, fullFrame.IsTombstone);

        // 提取完整帧的 TailMeta
        var fullPayloadAndMeta = fullFrame.PayloadAndMeta;
        var fullTailMeta = fullPayloadAndMeta.Slice(payload.Length, tailMeta.Length);

        Assert.Equal(previewFrame.TailMeta.ToArray(), fullTailMeta.ToArray());
        Assert.Equal(tailMeta, fullTailMeta.ToArray());
    }

    #endregion

    #region 边界值测试

    /// <summary>验证各种 TailMeta 大小的正确处理。</summary>
    [Theory]
    [InlineData(0)]     // 空 TailMeta
    [InlineData(1)]     // 最小非空
    [InlineData(3)]     // 触发 1 字节 padding
    [InlineData(4)]     // 4B 对齐
    [InlineData(100)]   // 中等大小
    [InlineData(1000)]  // 较大
    public void ReadTailMeta_VariousTailMetaSizes(int tailMetaLen) {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] tailMeta = new byte[tailMetaLen];
        if (tailMetaLen > 0) {
            new Random(tailMetaLen).NextBytes(tailMeta);
        }
        uint tag = (uint)(0x10000000 + tailMetaLen);

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload, tailMeta).IsSuccess);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        Assert.Equal(tailMetaLen, scanInfo.TailMetaLength);

        var result = scanInfo.ReadPooledTailMeta();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var pooledTailMeta = result.Value;

        Assert.Equal(tag, pooledTailMeta.Tag);
        Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());
    }

    /// <summary>验证带 TailMeta 的墓碑帧。</summary>
    [Fact]
    public void ReadTailMeta_TombstoneFrame_ReturnsIsTombstoneTrue() {
        // Arrange: 手动创建带 TailMeta 的墓碑帧文件
        byte[] payload = [0x01, 0x02];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC];
        uint tag = 0xDEADBEEF;

        var path = CreateTestFileWithFrames(
            (tag, payload, tailMeta, isTombstone: true)
        );

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        RbfFrameInfo? frameInfo = null;
        foreach (var info in rbfRead.ScanReverse(showTombstone: true)) {
            frameInfo = info;
            break;
        }

        Assert.NotNull(frameInfo);
        var scanInfo = frameInfo.Value;
        Assert.True(scanInfo.IsTombstone);

        var result = scanInfo.ReadPooledTailMeta();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var pooledTailMeta = result.Value;

        Assert.Equal(tag, pooledTailMeta.Tag);
        Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());
        Assert.True(pooledTailMeta.IsTombstone);
    }

    /// <summary>验证混合帧场景：部分有 TailMeta，部分没有。</summary>
    [Fact]
    public void ReadTailMeta_MixedFrames_CorrectlyHandles() {
        // Arrange
        var path = GetTempFilePath();

        using (var rbf = RbfFile.CreateNew(path)) {
            // Frame 1: 无 TailMeta
            Assert.True(rbf.Append(0x11111111, [0x01]).IsSuccess);
            // Frame 2: 有 TailMeta
            Assert.True(rbf.Append(0x22222222, [0x02], [0xAA, 0xBB]).IsSuccess);
            // Frame 3: 无 TailMeta
            Assert.True(rbf.Append(0x33333333, [0x03]).IsSuccess);
            // Frame 4: 有 TailMeta
            Assert.True(rbf.Append(0x44444444, [0x04], [0xCC]).IsSuccess);
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var results = new List<(uint tag, int tailMetaLen, byte[] tailMeta)>();

        foreach (var info in rbfRead.ScanReverse()) {
            var result = info.ReadPooledTailMeta();
            Assert.True(result.IsSuccess);
            using var pooled = result.Value!;
            results.Add((pooled.Tag, pooled.TailMeta.Length, pooled.TailMeta.ToArray()));
        }

        // Assert: 逆序
        Assert.Equal(4, results.Count);

        Assert.Equal(0x44444444u, results[0].tag);
        Assert.Equal(1, results[0].tailMetaLen);
        Assert.Equal(new byte[] { 0xCC }, results[0].tailMeta);

        Assert.Equal(0x33333333u, results[1].tag);
        Assert.Equal(0, results[1].tailMetaLen);
        Assert.Empty(results[1].tailMeta);

        Assert.Equal(0x22222222u, results[2].tag);
        Assert.Equal(2, results[2].tailMetaLen);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, results[2].tailMeta);

        Assert.Equal(0x11111111u, results[3].tag);
        Assert.Equal(0, results[3].tailMetaLen);
        Assert.Empty(results[3].tailMeta);
    }

    #endregion

    #region ReadFrameInfo / ReadPooledTailMeta(SizedPtr) 测试

    /// <summary>验证 ReadFrameInfo 从 ticket 正确获取帧元信息。</summary>
    [Fact]
    public void ReadFrameInfo_FromTicket_ReturnsCorrectInfo() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC];
        uint tag = 0x12345678;

        SizedPtr ticket;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload, tailMeta);
            Assert.True(result.IsSuccess);
            ticket = result.Value;
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var infoResult = rbfRead.ReadFrameInfo(ticket);

        // Assert
        Assert.True(infoResult.IsSuccess);
        var info = infoResult.Value;
        Assert.Equal(tag, info.Tag);
        Assert.Equal(payload.Length, info.PayloadLength);
        Assert.Equal(tailMeta.Length, info.TailMetaLength);
        Assert.False(info.IsTombstone);
        Assert.Equal(ticket.Offset, info.Ticket.Offset);
        Assert.Equal(ticket.Length, info.Ticket.Length);
    }

    /// <summary>验证 ReadFrameInfo 与 ScanReverse 返回的信息完全一致。</summary>
    [Fact]
    public void ReadFrameInfo_MatchesScanReverse() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] tailMeta = [0x11, 0x22, 0x33, 0x44, 0x55];
        uint tag = 0xCAFEBABE;

        SizedPtr ticket;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload, tailMeta);
            Assert.True(result.IsSuccess);
            ticket = result.Value;
        }

        // Act: 分别通过 ScanReverse 和 ReadFrameInfo 获取 info
        using var rbfRead = RbfFile.OpenExisting(path);

        RbfFrameInfo? scanInfo = null;
        foreach (var info in rbfRead.ScanReverse()) {
            scanInfo = info;
            break;
        }
        Assert.NotNull(scanInfo);

        var readInfoResult = rbfRead.ReadFrameInfo(ticket);
        Assert.True(readInfoResult.IsSuccess);
        var readInfo = readInfoResult.Value;

        // Assert: 两者完全一致
        Assert.Equal(scanInfo.Value.Tag, readInfo.Tag);
        Assert.Equal(scanInfo.Value.PayloadLength, readInfo.PayloadLength);
        Assert.Equal(scanInfo.Value.TailMetaLength, readInfo.TailMetaLength);
        Assert.Equal(scanInfo.Value.IsTombstone, readInfo.IsTombstone);
        Assert.Equal(scanInfo.Value.Ticket.Offset, readInfo.Ticket.Offset);
        Assert.Equal(scanInfo.Value.Ticket.Length, readInfo.Ticket.Length);
    }

    /// <summary>验证 ReadPooledTailMeta(SizedPtr) 从 ticket 正确读取 TailMeta。</summary>
    [Fact]
    public void ReadPooledTailMeta_FromTicket_ReturnsCorrectData() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD];
        uint tag = 0xDEADBEEF;

        SizedPtr ticket;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload, tailMeta);
            Assert.True(result.IsSuccess);
            ticket = result.Value;
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var result2 = rbfRead.ReadPooledTailMeta(ticket);

        // Assert
        Assert.True(result2.IsSuccess);
        Assert.NotNull(result2.Value);
        using var pooledTailMeta = result2.Value;

        Assert.Equal(tag, pooledTailMeta.Tag);
        Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());
        Assert.False(pooledTailMeta.IsTombstone);
        Assert.Equal(ticket.Offset, pooledTailMeta.Ticket.Offset);
        Assert.Equal(ticket.Length, pooledTailMeta.Ticket.Length);
    }

    /// <summary>验证 ReadPooledTailMeta(SizedPtr) 处理空 TailMeta 的帧。</summary>
    [Fact]
    public void ReadPooledTailMeta_FromTicket_EmptyTailMeta_ReturnsEmpty() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        uint tag = 0xABCDEF00;

        SizedPtr ticket;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload); // 无 TailMeta
            Assert.True(result.IsSuccess);
            ticket = result.Value;
        }

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var result2 = rbfRead.ReadPooledTailMeta(ticket);

        // Assert
        Assert.True(result2.IsSuccess);
        Assert.NotNull(result2.Value);
        using var pooledTailMeta = result2.Value;

        Assert.Equal(tag, pooledTailMeta.Tag);
        Assert.True(pooledTailMeta.TailMeta.IsEmpty);
        Assert.False(pooledTailMeta.IsTombstone);
    }

    /// <summary>验证 ReadFrameInfo 对非法 ticket（太短）返回错误。</summary>
    [Fact]
    public void ReadFrameInfo_InvalidTicket_ReturnsError() {
        // Arrange: 创建一个有效文件
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02];
        uint tag = 0x11111111;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(tag, payload).IsSuccess);
        }

        // 构造一个非法的 SizedPtr（长度太短，无法容纳 TrailerCodeword）
        // 使用 SizedPtr.Create(offset, length)，长度只有 8 字节，小于 TrailerCodeword 的 16 字节
        var invalidTicket = SizedPtr.Create(8, 8);

        // Act
        using var rbfRead = RbfFile.OpenExisting(path);
        var result = rbfRead.ReadFrameInfo(invalidTicket);

        // Assert
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
    }

    #endregion
}
