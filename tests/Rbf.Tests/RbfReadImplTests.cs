using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>RbfReadImpl.ReadFrame 格式验证测试（v0.40 格式）。</summary>
/// <remarks>
/// 职责：验证 RawOps 层 ReadFrame 的帧解码符合规范。
/// 规范引用：
/// - @[A-RBF-FRAME-STRUCT] - RbfFrame 结构定义
/// - @[F-FRAMEBYTES-LAYOUT] - FrameBytes 布局
/// - @[F-PAYLOAD-CRC-COVERAGE] - PayloadCrc 覆盖范围
/// - @[F-TRAILER-CRC-COVERAGE] - TrailerCrc 覆盖范围
/// - @[F-FRAME-DESCRIPTOR-LAYOUT] - FrameDescriptor 位布局
/// </remarks>
public class RbfReadImplTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    /// <summary>生成一个不存在的临时文件路径。</summary>
    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            }
            catch {
                // 忽略清理错误
            }
        }
    }

    // ========== 辅助方法 ==========

    /// <summary>辅助方法：分配 buffer 并调用 ReadFrameInto。</summary>
    private static AteliaResult<RbfFrame> ReadFrameIntoHelper(SafeFileHandle handle, SizedPtr ptr) {
        byte[] buffer = new byte[ptr.Length]; // 用堆数组避免 ref struct 生命周期问题
        return RbfReadImpl.ReadFrame(handle, ptr, buffer);
    }

    /// <summary>构造一个有效帧的字节数组（v0.40 格式）。</summary>
    /// <param name="tag">帧 Tag。</param>
    /// <param name="payload">Payload 数据。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <returns>完整帧字节数组（不含 HeaderFence/Fence）。</returns>
    /// <remarks>
    /// v0.40 布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
    /// </remarks>
    private static byte[] CreateValidFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;

        byte[] frame = new byte[frameLen];
        Span<byte> span = frame;

        // 1. HeadLen (offset 0)
        BinaryPrimitives.WriteUInt32LittleEndian(span[..FrameLayout.HeadLenSize], (uint)frameLen);

        // 2. Payload (offset 4)
        payload.CopyTo(span.Slice(FrameLayout.PayloadOffset, payload.Length));

        // 3. Padding（清零，FrameLayout 已计算好）
        if (layout.PaddingLength > 0) {
            span.Slice(layout.PaddingOffset, layout.PaddingLength).Clear();
        }

        // 4. PayloadCrc（覆盖 Payload + TailMeta + Padding）
        var payloadCrcCoverage = span.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint payloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize), payloadCrc);

        // 5. TrailerCodeword
        layout.FillTrailer(span.Slice(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, isTombstone);

        return frame;
    }

    /// <summary>构造带 HeaderFence + Frame + Fence 的完整文件内容。</summary>
    private static byte[] CreateValidFileWithFrame(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        byte[] frameBytes = CreateValidFrameBytes(tag, payload, isTombstone);
        int totalLen = RbfLayout.FenceSize + frameBytes.Length + RbfLayout.FenceSize;
        byte[] file = new byte[totalLen];

        // HeaderFence
        RbfLayout.Fence.CopyTo(file.AsSpan(0, RbfLayout.FenceSize));
        // Frame
        frameBytes.CopyTo(file.AsSpan(RbfLayout.FenceSize));
        // Fence
        RbfLayout.Fence.CopyTo(file.AsSpan(RbfLayout.FenceSize + frameBytes.Length, RbfLayout.FenceSize));

        return file;
    }

    // ========== 正常路径测试 ==========

    /// <summary>验证墓碑帧的正确解码。</summary>
    [Fact]
    public void ReadFrame_Tombstone_DecodesCorrectly() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xAA, 0xBB, 0xCC];
        uint tag = 0x11223344;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload, isTombstone: true);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.True(result.IsSuccess);
        var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
        Assert.True(frame.IsTombstone);
    }

    // ========== 参数错误测试 ==========

    // 注：MisalignedOffset/MisalignedLength 测试已移除
    // 原因：SizedPtr 类型系统使用 << 2 编码，天然保证 4B 对齐，无法构造非对齐值

    /// <summary>验证 Length &lt; 24（最小帧长度）时返回 ArgumentError（v0.40 最小帧长度为 24）。</summary>
    [Fact]
    public void ReadFrame_FrameTooShort_ReturnsArgumentError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Length = 20，小于最小帧长度 24（v0.40）
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, 20);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfArgumentError>(result.Error);
        Assert.Contains("minimum frame length", result.Error!.Message);
    }

    /// <summary>验证越界读取（Offset 超出文件尾）返回 ArgumentError。</summary>
    [Fact]
    public void ReadFrame_OutOfRange_ReturnsArgumentError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // 尝试从超出文件尾的位置读取（v0.40 最小帧长度为 24）
        var ptr = SizedPtr.Create(1000, 24);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfArgumentError>(result.Error);
        Assert.Contains("Short read", result.Error!.Message);
    }

    // ========== Framing 错误测试 ==========

    /// <summary>验证 HeadLen 字段与 ptr.Length 不匹配时返回 FramingError。</summary>
    [Fact]
    public void ReadFrame_HeadLenMismatch_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 HeadLen 字段为错误值
        int frameOffset = RbfLayout.FenceSize;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(frameOffset, FrameLayout.HeadLenSize),
            999 // 错误的 HeadLen
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("HeadLen mismatch", result.Error!.Message);
    }

    /// <summary>验证 TailLen 与 HeadLen 不匹配时返回 FramingError（v0.40 从 TrailerCodeword 读取 TailLen）。</summary>
    [Fact]
    public void ReadFrame_TailLenMismatch_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 TrailerCodeword 中的 TailLen 字段为错误值
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        // TrailerCodeword 内 TailLen 偏移 = TrailerCodewordOffset + 12
        int tailLenOffset = frameOffset + layout.TrailerCodewordOffset + 12;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(tailLenOffset, 4),
            999 // 错误的 TailLen
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        // v0.40 会先校验 TrailerCrc，TailLen 被篡改后 CRC 校验会失败
        Assert.True(
            result.Error is RbfCrcMismatchError || result.Error is RbfFramingError,
            $"Expected RbfCrcMismatchError or RbfFramingError, got {result.Error?.GetType().Name}"
        );
    }

    /// <summary>验证 FrameDescriptor 保留位非零时返回 FramingError（v0.40）。</summary>
    [Fact]
    public void ReadFrame_DescriptorReservedBitsNonZero_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 FrameDescriptor 的保留位（bit 28-16）
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        // FrameDescriptor 偏移 = TrailerCodewordOffset + 4
        int descriptorOffset = frameOffset + layout.TrailerCodewordOffset + 4;

        // 设置保留位为非零（bit 28-16）
        uint invalidDescriptor = 0x0001_0000; // bit 16 = 1（保留位）
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(descriptorOffset, 4),
            invalidDescriptor
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        // 修改 FrameDescriptor 会导致 TrailerCrc 校验失败
        Assert.True(
            result.Error is RbfCrcMismatchError || result.Error is RbfFramingError,
            $"Expected CrcMismatch or FramingError, got {result.Error?.GetType().Name}"
        );
    }

    // ========== CRC 错误测试 ==========

    /// <summary>验证 PayloadCrc 损坏时返回 CrcMismatch 错误（v0.40）。</summary>
    [Fact]
    public void ReadFrame_PayloadCrcMismatch_ReturnsCrcMismatch() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 PayloadCrc 字段为错误值
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        int payloadCrcOffset = frameOffset + layout.PayloadCrcOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(payloadCrcOffset, FrameLayout.PayloadCrcSize),
            0xDEADBEEF // 错误的 PayloadCrc
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfCrcMismatchError>(result.Error);
    }

    /// <summary>验证 TrailerCrc 损坏时返回 CrcMismatch 错误（v0.40）。</summary>
    [Fact]
    public void ReadFrame_TrailerCrcMismatch_ReturnsCrcMismatch() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 TrailerCrc 字段为错误值（TrailerCodeword 的前 4 字节，BE）
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        int trailerCrcOffset = frameOffset + layout.TrailerCodewordOffset;
        BinaryPrimitives.WriteUInt32BigEndian(
            fileContent.AsSpan(trailerCrcOffset, 4),
            0xCAFEBABE // 错误的 TrailerCrc
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfCrcMismatchError>(result.Error);
    }

    // ========== 边界值测试 ==========

    /// <summary>验证不同 payload 长度（对齐边界）的正确解码（v0.40 格式）。</summary>
    [Theory]
    [InlineData(0)]   // paddingLen = 0
    [InlineData(1)]   // paddingLen = 3
    [InlineData(2)]   // paddingLen = 2
    [InlineData(3)]   // paddingLen = 1
    [InlineData(4)]   // paddingLen = 0
    [InlineData(7)]   // paddingLen = 1
    [InlineData(8)]   // paddingLen = 0
    public void ReadFrame_VariousPayloadAlignments_DecodesCorrectly(int payloadLen) {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0) {
            new Random(payloadLen).NextBytes(payload);
        }
        uint tag = (uint)(0x10000000 + payloadLen);
        byte[] fileContent = CreateValidFileWithFrame(tag, payload);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.True(result.IsSuccess, $"Failed for payloadLen={payloadLen}: {result.Error?.Message}");
        var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
        Assert.False(frame.IsTombstone);
    }

    // ========== Buffer 相关测试 ==========

    /// <summary>验证 buffer 太小时返回 RbfBufferTooSmallError。</summary>
    [Fact]
    public void ReadFrameInto_BufferTooSmall_ReturnsBufferError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        uint tag = 0x12345678;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act: 提供太小的 buffer（只有10字节，但需要24字节）
        Span<byte> tooSmallBuffer = stackalloc byte[10];
        var result = RbfReadImpl.ReadFrame(handle, ptr, tooSmallBuffer);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfBufferTooSmallError>(result.Error);

        var error = (RbfBufferTooSmallError)result.Error!;
        Assert.Equal(frameLen, error.RequiredBytes);
        Assert.Equal(10, error.ProvidedBytes);
    }

    /// <summary>验证 ReadFrameInto 的 zero-copy 特性：Payload 直接引用 buffer（v0.40 格式）。</summary>
    [Fact]
    public void ReadFrameInto_ValidBuffer_PayloadIsSlice() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD];
        uint tag = 0x12345678;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act: 提供足够大的 buffer
        byte[] buffer = new byte[frameLen];
        var result = RbfReadImpl.ReadFrame(handle, ptr, buffer);

        // Assert: Payload 应该是 buffer 的切片
        Assert.True(result.IsSuccess);
        var frame = result.Value;

        // 验证 Payload 数据正确
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());

        // 验证 zero-copy：修改 buffer 应该影响 Payload
        // v0.40: Payload 位于 buffer 的 offset 4 处（HeadLen(4) = 4）
        buffer[FrameLayout.PayloadOffset] = 0xFF; // 修改 Payload 第一个字节
        Assert.Equal(0xFF, frame.PayloadAndMeta[0]); // 应该能看到修改（证明是引用）
    }
}

