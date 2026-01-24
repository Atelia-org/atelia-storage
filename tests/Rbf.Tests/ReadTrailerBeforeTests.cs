using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfReadImpl.ReadTrailerBefore 测试（design-draft.md §4.2）。
/// </summary>
/// <remarks>
/// 职责：验证 ReadTrailerBefore 的正常路径和错误路径。
/// 规范引用：
/// - @[A-READ-TRAILER-BEFORE] - ReadTrailerBefore 算法
/// - @[F-TRAILERCRC-COVERAGE] - TrailerCrc 覆盖范围
/// - @[F-FRAMEDESCRIPTOR-LAYOUT] - FrameDescriptor 位布局
/// </remarks>
public class ReadTrailerBeforeTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 忽略清理错误 */ }
        }
    }

    #region 辅助方法

    /// <summary>
    /// 构造一个有效帧的字节数组（v0.40 格式）。
    /// </summary>
    private static byte[] CreateValidFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
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
        uint payloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize), payloadCrc);
        // TrailerCodeword
        layout.FillTrailer(span.Slice(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, isTombstone);

        return frame;
    }

    /// <summary>
    /// 构造 HeaderFence + Frame + Fence 的完整文件内容。
    /// </summary>
    private static byte[] CreateValidFileWithFrame(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        byte[] frameBytes = CreateValidFrameBytes(tag, payload, isTombstone);
        int totalLen = RbfLayout.FenceSize + frameBytes.Length + RbfLayout.FenceSize;
        byte[] file = new byte[totalLen];

        RbfLayout.Fence.CopyTo(file.AsSpan(0, RbfLayout.FenceSize));
        frameBytes.CopyTo(file.AsSpan(RbfLayout.FenceSize));
        RbfLayout.Fence.CopyTo(file.AsSpan(RbfLayout.FenceSize + frameBytes.Length, RbfLayout.FenceSize));

        return file;
    }

    #endregion

    #region 正常路径测试

    /// <summary>
    /// 验证正常帧的 ReadTrailerBefore：返回正确的 Tag、PayloadLength、IsTombstone。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_ValidFrame_ReturnsCorrectFrameInfo() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // fenceEndOffset = 文件末尾（Fence 的 EndOffsetExclusive）
        long fenceEndOffset = fileContent.Length;

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fenceEndOffset);

        // Assert
        Assert.True(result.IsSuccess, $"Expected success, got error: {result.Error?.Message}");
        var frameInfo = result.Value;

        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(payload.Length, frameInfo.PayloadLength);
        Assert.False(frameInfo.IsTombstone);
        Assert.Equal(0, frameInfo.UserMetaLength); // 无 UserMeta

        // Ticket 验证
        int expectedFrameLen = new FrameLayout(payload.Length).FrameLength;
        Assert.Equal(RbfLayout.FenceSize, frameInfo.Ticket.Offset); // 帧起始 = HeaderFence 之后
        Assert.Equal(expectedFrameLen, frameInfo.Ticket.Length);
    }

    /// <summary>
    /// 验证空 payload 帧（最小帧 24B）的 ReadTrailerBefore。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_EmptyPayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [];
        uint tag = 0xDEADBEEF;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        long fenceEndOffset = fileContent.Length;

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fenceEndOffset);

        // Assert
        Assert.True(result.IsSuccess);
        var frameInfo = result.Value;

        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(0, frameInfo.PayloadLength);
        Assert.False(frameInfo.IsTombstone);
        Assert.Equal(FrameLayout.MinFrameLength, frameInfo.Ticket.Length); // 最小帧 24B
    }

    /// <summary>
    /// 验证墓碑帧的 ReadTrailerBefore。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_TombstoneFrame_ReturnsIsTombstoneTrue() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xAA, 0xBB];
        uint tag = 0x11223344;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload, isTombstone: true);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        long fenceEndOffset = fileContent.Length;

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fenceEndOffset);

        // Assert
        Assert.True(result.IsSuccess);
        var frameInfo = result.Value;

        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(payload.Length, frameInfo.PayloadLength);
        Assert.True(frameInfo.IsTombstone);
    }

    /// <summary>
    /// 验证不同 payload 对齐的 ReadTrailerBefore（padding 测试）。
    /// </summary>
    [Theory]
    [InlineData(0)]   // paddingLen = 0
    [InlineData(1)]   // paddingLen = 3
    [InlineData(2)]   // paddingLen = 2
    [InlineData(3)]   // paddingLen = 1
    [InlineData(4)]   // paddingLen = 0
    [InlineData(7)]   // paddingLen = 1
    [InlineData(8)]   // paddingLen = 0
    public void ReadTrailerBefore_VariousPayloadAlignments_Succeeds(int payloadLen) {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0) new Random(payloadLen).NextBytes(payload);
        uint tag = (uint)(0x10000000 + payloadLen);

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        long fenceEndOffset = fileContent.Length;

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fenceEndOffset);

        // Assert
        Assert.True(result.IsSuccess, $"Failed for payloadLen={payloadLen}: {result.Error?.Message}");
        var frameInfo = result.Value;

        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(payloadLen, frameInfo.PayloadLength);
    }

    /// <summary>
    /// 验证多帧文件中 ReadTrailerBefore 的逆向迭代。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_MultipleFrames_IteratesBackward() {
        // Arrange: 构建 HeaderFence + Frame1 + Fence + Frame2 + Fence
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD];
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;

        byte[] frame1Bytes = CreateValidFrameBytes(tag1, payload1);
        byte[] frame2Bytes = CreateValidFrameBytes(tag2, payload2);

        int totalLen = RbfLayout.FenceSize + frame1Bytes.Length + RbfLayout.FenceSize + frame2Bytes.Length + RbfLayout.FenceSize;
        byte[] file = new byte[totalLen];
        int offset = 0;

        // HeaderFence
        RbfLayout.Fence.CopyTo(file.AsSpan(offset, RbfLayout.FenceSize));
        offset += RbfLayout.FenceSize;
        // Frame1
        frame1Bytes.CopyTo(file.AsSpan(offset));
        offset += frame1Bytes.Length;
        // Fence
        RbfLayout.Fence.CopyTo(file.AsSpan(offset, RbfLayout.FenceSize));
        offset += RbfLayout.FenceSize;
        // Frame2
        frame2Bytes.CopyTo(file.AsSpan(offset));
        offset += frame2Bytes.Length;
        // Fence
        RbfLayout.Fence.CopyTo(file.AsSpan(offset, RbfLayout.FenceSize));

        File.WriteAllBytes(path, file);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act & Assert: 第一次读取（从文件末尾）应返回 Frame2
        long fenceEnd = file.Length;
        var result1 = RbfReadImpl.ReadTrailerBefore(handle, fenceEnd);
        Assert.True(result1.IsSuccess);
        Assert.Equal(tag2, result1.Value.Tag);
        Assert.Equal(payload2.Length, result1.Value.PayloadLength);

        // 第二次读取（从 Frame2 起始位置）应返回 Frame1
        long nextFenceEnd = result1.Value.Ticket.Offset;
        var result2 = RbfReadImpl.ReadTrailerBefore(handle, nextFenceEnd);
        Assert.True(result2.IsSuccess);
        Assert.Equal(tag1, result2.Value.Tag);
        Assert.Equal(payload1.Length, result2.Value.PayloadLength);
    }

    #endregion

    #region Fence 损坏测试

    /// <summary>
    /// 验证 Fence 损坏时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_CorruptedFence_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 损坏尾部 Fence
        int fenceOffset = fileContent.Length - RbfLayout.FenceSize;
        fileContent[fenceOffset] = 0xFF; // 破坏 'R'

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("Fence", result.Error!.Message);
    }

    #endregion

    #region CRC 损坏测试

    /// <summary>
    /// 验证 TrailerCrc32C 损坏时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_CorruptedTrailerCrc_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 损坏 TrailerCrc（TrailerCodeword 的前 4 字节，位于 Fence 之前 16 字节处）
        var layout = new FrameLayout(payload.Length);
        int trailerCrcOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        BinaryPrimitives.WriteUInt32BigEndian(fileContent.AsSpan(trailerCrcOffset, 4), 0xDEADBEEF);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("TrailerCrc32C", result.Error!.Message);
    }

    /// <summary>
    /// 验证 TrailerCodeword 数据损坏（非 CRC 字段）导致 CRC 校验失败。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_CorruptedTrailerData_ReturnsCrcError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 损坏 FrameTag（TrailerCodeword offset 8-12）
        var layout = new FrameLayout(payload.Length);
        int tagOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(fileContent.AsSpan(tagOffset, 4), 0x99999999);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        // 数据损坏会导致 TrailerCrc 校验失败
        Assert.IsType<RbfFramingError>(result.Error);
    }

    #endregion

    #region TailLen 越界测试

    /// <summary>
    /// 验证 TailLen 小于 MinFrameLength 时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_TailLenTooSmall_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 手动构造一个 TailLen 太小的 TrailerCodeword（绕过正常写入流程）
        var layout = new FrameLayout(payload.Length);
        int trailerOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        Span<byte> trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);

        // 构建无效的 TrailerCodeword（TailLen = 20，小于 MinFrameLength = 24）
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, layout.PaddingLength, 0);
        TrailerCodewordHelper.SerializeWithoutCrc(trailerSpan, descriptor, tag, 20); // 无效 TailLen
        TrailerCodewordHelper.SealTrailerCrc(trailerSpan);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("TailLen", result.Error!.Message);
    }

    /// <summary>
    /// 验证 TailLen 导致 frameStart 越过 HeaderFence 时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_TailLenExceedsBounds_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 手动构造 TailLen 过大的 TrailerCodeword
        var layout = new FrameLayout(payload.Length);
        int trailerOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        Span<byte> trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);

        // TailLen 设置为超大值，使 frameStart 越过 HeaderFence
        uint hugeTailLen = (uint)(fileContent.Length); // 等于整个文件长度
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, layout.PaddingLength, 0);
        TrailerCodewordHelper.SerializeWithoutCrc(trailerSpan, descriptor, tag, hugeTailLen);
        TrailerCodewordHelper.SealTrailerCrc(trailerSpan);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("HeaderFence", result.Error!.Message);
    }

    /// <summary>
    /// 验证 TailLen 非 4B 对齐时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_TailLenNotAligned_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 手动构造 TailLen 非对齐的 TrailerCodeword
        var layout = new FrameLayout(payload.Length);
        int trailerOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        Span<byte> trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);

        // TailLen = 25（非 4B 对齐）
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, layout.PaddingLength, 0);
        TrailerCodewordHelper.SerializeWithoutCrc(trailerSpan, descriptor, tag, 25);
        TrailerCodewordHelper.SealTrailerCrc(trailerSpan);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("TailLen", result.Error!.Message);
    }

    #endregion

    #region 边界测试

    /// <summary>
    /// 验证 fenceEndOffset 太小（无法容纳任何帧）时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_OffsetTooSmall_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // fenceEndOffset = HeaderFence 之后一点点，不足以容纳 MinFrame + Fence
        long tooSmallOffset = RbfLayout.HeaderOnlyLength + RbfLayout.MinFrameLength;

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, tooSmallOffset);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("No frame before", result.Error!.Message);
    }

    /// <summary>
    /// 验证文件被截断（短读）时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_TruncatedFile_ReturnsFramingError() {
        // Arrange: 创建一个截断的文件（只有 HeaderFence + 几个字节）
        var path = GetTempFilePath();
        byte[] truncatedFile = new byte[RbfLayout.FenceSize + 10]; // 太短
        RbfLayout.Fence.CopyTo(truncatedFile.AsSpan(0, RbfLayout.FenceSize));
        File.WriteAllBytes(path, truncatedFile);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // 尝试从超出文件长度的位置读取
        long fakeOffset = RbfLayout.HeaderOnlyLength + RbfLayout.MinFrameLength + RbfLayout.FenceSize;

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fakeOffset);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("Short read", result.Error!.Message);
    }

    #endregion

    #region FrameDescriptor 保留位测试

    /// <summary>
    /// 验证 FrameDescriptor 保留位（bit 28-16）非零时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_DescriptorReservedBitsNonZero_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 手动构造保留位非零的 FrameDescriptor
        var layout = new FrameLayout(payload.Length);
        int trailerOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        Span<byte> trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);

        // 设置保留位（bit 28-16）
        uint invalidDescriptor = 0x0010_0000; // bit 20 = 1（保留位）
        TrailerCodewordHelper.SerializeWithoutCrc(trailerSpan, invalidDescriptor, tag, (uint)layout.FrameLength);
        TrailerCodewordHelper.SealTrailerCrc(trailerSpan);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("reserved bits", result.Error!.Message);
    }

    /// <summary>
    /// 验证 TailLen > int.MaxValue 时返回 FramingError。
    /// </summary>
    /// <remarks>
    /// Task 6.7 验收标准：TailLen > int.MaxValue 必须失败。
    /// </remarks>
    [Fact]
    public void ReadTrailerBefore_TailLenExceedsIntMax_ReturnsFramingError() {
        // Arrange: 构造一个大文件模拟 TailLen > int.MaxValue
        // 由于无法真正创建 2GB+ 文件，我们手动构造 TrailerCodeword
        var path = GetTempFilePath();

        // 创建一个最小有效帧，然后篡改 TailLen
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        uint tag = 0x12345678;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        var layout = new FrameLayout(payload.Length);
        int trailerOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        Span<byte> trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);

        // TailLen = uint.MaxValue - 3 (> int.MaxValue，且 4B 对齐)
        uint hugeTailLen = uint.MaxValue - 3; // 0xFFFFFFFC
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, layout.PaddingLength, 0);
        TrailerCodewordHelper.SerializeWithoutCrc(trailerSpan, descriptor, tag, hugeTailLen);
        TrailerCodewordHelper.SealTrailerCrc(trailerSpan);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        // 应该因为 TailLen > int.MaxValue 或 frameStart 越界而失败
        Assert.True(
            result.Error!.Message.Contains("TailLen") || result.Error!.Message.Contains("HeaderFence"),
            $"Expected error about TailLen or HeaderFence, got: {result.Error!.Message}"
        );
    }

    /// <summary>
    /// 验证 PayloadLength 计算为负数时返回 FramingError。
    /// </summary>
    /// <remarks>
    /// 当 UserMetaLen 过大导致 PayloadLength = TailLen - FixedOverhead - UserMetaLen - PaddingLen < 0 时应失败。
    /// </remarks>
    [Fact]
    public void ReadTrailerBefore_NegativePayloadLength_ReturnsFramingError() {
        // Arrange: 构造一个 UserMetaLen 过大的帧
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04]; // 4 字节
        uint tag = 0x12345678;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        var layout = new FrameLayout(payload.Length);
        int trailerOffset = RbfLayout.FenceSize + layout.TrailerCodewordOffset;
        Span<byte> trailerSpan = fileContent.AsSpan(trailerOffset, TrailerCodewordHelper.Size);

        // 保持 TailLen 不变，但把 UserMetaLen 设为最大值 (65535)
        // PayloadLength = TailLen - 24 - 65535 - PaddingLen << 0
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, layout.PaddingLength, 65535);
        TrailerCodewordHelper.SerializeWithoutCrc(trailerSpan, descriptor, tag, (uint)layout.FrameLength);
        TrailerCodewordHelper.SealTrailerCrc(trailerSpan);

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("PayloadLength", result.Error!.Message);
    }

    /// <summary>
    /// 验证 PayloadCrc 损坏但 TrailerCrc 正确时仍返回成功（ReadTrailerBefore 不校验 PayloadCrc）。
    /// </summary>
    /// <remarks>
    /// 锁定 Decision 6.C：ScanReverse 只校验 TrailerCrc，不校验 PayloadCrc。
    /// </remarks>
    [Fact]
    public void ReadTrailerBefore_CorruptedPayloadCrc_StillSucceeds() {
        // Arrange: 创建有效帧，然后篡改 PayloadCrc
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        uint tag = 0x12345678;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload);

        // 篡改 PayloadCrc（位于 TrailerCodeword 之前 4 字节）
        var layout = new FrameLayout(payload.Length);
        int payloadCrcOffset = RbfLayout.FenceSize + layout.PayloadCrcOffset;
        fileContent[payloadCrcOffset] ^= 0xFF; // 翻转第一个字节

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Act
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileContent.Length);

        // Assert: 应该成功（ReadTrailerBefore 不校验 PayloadCrc）
        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error?.Message}");
        Assert.Equal(tag, result.Value.Tag);
        Assert.Equal(payload.Length, result.Value.PayloadLength);
    }

    #endregion

    #region 与 RbfFile 集成测试

    /// <summary>
    /// 验证 ReadTrailerBefore 与 RbfFile.Append 的闭环正确性。
    /// </summary>
    [Fact]
    public void ReadTrailerBefore_IntegrationWithAppend_ReturnsMatchingInfo() {
        // Arrange: 使用 RbfFile.Append 写入帧
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        uint tag = 0xABCDEF01;

        SizedPtr appendedPtr;
        long fileLength;
        using (var rbfFile = RbfFile.CreateNew(path)) {
            appendedPtr = rbfFile.Append(tag, payload);
            fileLength = new FileInfo(path).Length;
        }

        // 使用底层 handle 调用 ReadTrailerBefore（RbfFile 已关闭）
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        var result = RbfReadImpl.ReadTrailerBefore(handle, fileLength);

        // Assert
        Assert.True(result.IsSuccess);
        var frameInfo = result.Value;

        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(payload.Length, frameInfo.PayloadLength);
        Assert.False(frameInfo.IsTombstone);
        Assert.Equal(appendedPtr.Offset, frameInfo.Ticket.Offset);
        Assert.Equal(appendedPtr.Length, frameInfo.Ticket.Length);
    }

    #endregion
}
