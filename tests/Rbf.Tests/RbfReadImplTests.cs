using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfReadImpl.ReadFrame 格式验证测试。
/// </summary>
/// <remarks>
/// 职责：验证 RawOps 层 ReadFrame 的帧解码符合规范。
/// 规范引用：
/// - @[A-RBF-FRAME-STRUCT] - RbfFrame 结构定义
/// - @[F-FRAMEBYTES-FIELD-OFFSETS] - FrameBytes 布局
/// - @[F-CRC32C-COVERAGE] - CRC 覆盖范围
/// - @[F-FRAMESTATUS-RESERVED-BITS-ZERO] - FrameStatus 保留位
/// - @[F-FRAMESTATUS-FILL] - FrameStatus 全字节同值
/// </remarks>
public class RbfReadImplTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// 生成一个不存在的临时文件路径。
    /// </summary>
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

    /// <summary>
    /// 辅助方法：分配 buffer 并调用 ReadFrameInto。
    /// </summary>
    private static AteliaResult<RbfFrame> ReadFrameIntoHelper(SafeFileHandle handle, SizedPtr ptr) {
        byte[] buffer = new byte[ptr.Length]; // 用堆数组避免 ref struct 生命周期问题
        return RbfReadImpl.ReadFrame(handle, ptr, buffer);
    }

    /// <summary>
    /// 构造一个有效帧的字节数组。
    /// </summary>
    /// <param name="tag">帧 Tag。</param>
    /// <param name="payload">Payload 数据。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <returns>完整帧字节数组（不含 HeaderFence/Fence）。</returns>
    private static byte[] CreateValidFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length);
        int headLen = layout.FrameLength;
        int statusLen = layout.StatusLength;

        byte[] frame = new byte[headLen];
        Span<byte> span = frame;

        // HeadLen (offset 0)
        BinaryPrimitives.WriteUInt32LittleEndian(span[..FrameLayout.HeadLenSize], (uint)headLen);

        // Tag (offset 4)
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(FrameLayout.TagOffset, FrameLayout.TagSize), tag);

        // Payload (offset 8)
        payload.CopyTo(span.Slice(FrameLayout.PayloadOffset, payload.Length));

        // Status
        FrameStatusHelper.FillStatus(span.Slice(layout.StatusOffset, statusLen), isTombstone, statusLen);

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.TailLenOffset, FrameLayout.TailLenSize), (uint)headLen);

        // CRC
        // CRC 覆盖范围：Tag(4) + Payload(N) + Status(1-4) + TailLen(4) = frame[CrcCoverageStart..CrcCoverageEnd]
        ReadOnlySpan<byte> crcInput = span[FrameLayout.CrcCoverageStart..layout.CrcCoverageEnd];
        uint crc = Crc32CHelper.Compute(crcInput);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.CrcOffset, FrameLayout.CrcSize), crc);

        return frame;
    }

    /// <summary>
    /// 构造带 HeaderFence + Frame + Fence 的完整文件内容。
    /// </summary>
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

    /// <summary>
    /// 验证正常帧的读取：Tag、Payload、IsTombstone 正确解码。
    /// 使用从 Append 写入到 ReadPooledFrame 验证的闭环测试。
    /// </summary>
    [Fact]
    public void ReadPooledFrame_ValidFrame_ReturnsCorrectData() {
        // Arrange: 使用 Append 写入帧
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        uint tag = 0x12345678;

        using var rbfFile = RbfFile.CreateNew(path);
        var ptr = rbfFile.Append(tag, payload);

        // Act: 使用 ReadPooledFrame 读取
        var result = rbfFile.ReadPooledFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
        Assert.Equal(ptr, frame.Ticket);
    }

    /// <summary>
    /// 验证空 payload 帧（最小帧 20B）的读取。
    /// 使用从 Append 写入到 ReadPooledFrame 验证的闭环测试。
    /// </summary>
    [Fact]
    public void ReadPooledFrame_EmptyPayload_Succeeds() {
        // Arrange: 使用 Append 写入空 payload 帧
        var path = GetTempFilePath();
        byte[] payload = [];
        uint tag = 0xDEADBEEF;

        using var rbfFile = RbfFile.CreateNew(path);
        var ptr = rbfFile.Append(tag, payload);

        // 验证最小帧长度
        Assert.Equal(FrameLayout.MinFrameLength, ptr.Length);

        // Act: 使用 ReadPooledFrame 读取
        var result = rbfFile.ReadPooledFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Empty(frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
    }

    /// <summary>
    /// 验证大 payload（大于4KB，触发 ArrayPool 路径）的读取。
    /// 使用从 Append 写入到 ReadPooledFrame 验证的闭环测试。
    /// </summary>
    [Fact]
    public void ReadPooledFrame_LargePayload_Succeeds() {
        // Arrange: 使用 Append 写入大 payload 帧
        var path = GetTempFilePath();
        byte[] payload = new byte[8192]; // 8KB，超过 MaxStackAllocSize(4096)
        new Random(42).NextBytes(payload);
        uint tag = 0xCAFEBABE;

        using var rbfFile = RbfFile.CreateNew(path);
        var ptr = rbfFile.Append(tag, payload);

        // Act: 使用 ReadPooledFrame 读取
        var result = rbfFile.ReadPooledFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
    }

    /// <summary>
    /// 验证墓碑帧的正确解码。
    /// </summary>
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
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.True(frame.IsTombstone);
    }

    // ========== 参数错误测试 ==========

    // 注：MisalignedOffset/MisalignedLength 测试已移除
    // 原因：SizedPtr 类型系统使用 << 2 编码，天然保证 4B 对齐，无法构造非对齐值

    /// <summary>
    /// 验证 Length &lt; 20（最小帧长度）时返回 ArgumentError。
    /// </summary>
    [Fact]
    public void ReadFrame_FrameTooShort_ReturnsArgumentError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // Length = 16，小于最小帧长度 20
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, FrameLayout.OverheadLenButStatus);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfArgumentError>(result.Error);
        Assert.Contains("minimum frame length", result.Error!.Message);
    }

    /// <summary>
    /// 验证越界读取（Offset 超出文件尾）返回 ArgumentError。
    /// </summary>
    [Fact]
    public void ReadFrame_OutOfRange_ReturnsArgumentError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // 尝试从超出文件尾的位置读取
        var ptr = SizedPtr.Create(1000, 20);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfArgumentError>(result.Error);
        Assert.Contains("Short read", result.Error!.Message);
    }

    // ========== Framing 错误测试 ==========

    /// <summary>
    /// 验证 HeadLen 字段与 ptr.Length 不匹配时返回 FramingError。
    /// </summary>
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

    /// <summary>
    /// 验证 TailLen 与 HeadLen 不匹配时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadFrame_TailLenMismatch_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 TailLen 字段为错误值
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        int tailLenOffset = frameOffset + layout.TailLenOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(tailLenOffset, FrameLayout.TailLenSize),
            999 // 错误的 TailLen
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("TailLen mismatch", result.Error!.Message);
    }

    /// <summary>
    /// 验证 FrameStatus 保留位非零时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadFrame_InvalidStatusReservedBits_ReturnsFramingError() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 Status 字节的保留位
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        int statusLen = layout.StatusLength;
        int statusOffset = frameOffset + layout.StatusOffset;

        // 设置保留位（Bit6-2）为非零
        byte invalidStatus = 0x04; // Bit2 = 1，保留位非零
        for (int i = 0; i < statusLen; i++) {
            fileContent[statusOffset + i] = invalidStatus;
        }

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("reserved bits", result.Error!.Message);
    }

    /// <summary>
    /// 验证 FrameStatus 字节不一致时返回 FramingError。
    /// </summary>
    [Fact]
    public void ReadFrame_StatusBytesInconsistent_ReturnsFramingError() {
        // Arrange：需要使用 statusLen > 1 的帧
        var path = GetTempFilePath();
        // payload 长度 = 0，statusLen = 4（保证多字节 status）
        byte[] payload = [];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 验证 statusLen = 4
        int statusLen = new FrameLayout(payload.Length).StatusLength;
        Assert.Equal(4, statusLen);

        // 修改 Status 区域的第一个字节，使其与其他字节不一致
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int statusOffset = frameOffset + layout.StatusOffset;

        // 原始 status 字节 = 0x03（statusLen=4，非墓碑）
        // 修改第一个字节为不同值（但保留位仍为零）
        fileContent[statusOffset] = 0x02; // 不同的 status 值

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfFramingError>(result.Error);
        // 可能是 StatusLen inconsistency 或 Status bytes not consistent
        Assert.True(
            result.Error!.Message.Contains("Status") ||
            result.Error.Message.Contains("inconsisten", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ========== CRC 错误测试 ==========

    /// <summary>
    /// 验证 CRC 损坏时返回 CrcMismatch 错误。
    /// </summary>
    [Fact]
    public void ReadFrame_CrcMismatch_ReturnsCrcMismatch() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, payload);

        // 修改 CRC 字段为错误值
        int frameOffset = RbfLayout.FenceSize;
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;
        int crcOffset = frameOffset + layout.CrcOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(crcOffset, FrameLayout.CrcSize),
            0xDEADBEEF // 错误的 CRC
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = ReadFrameIntoHelper(handle, ptr);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.IsType<RbfCrcMismatchError>(result.Error);
        Assert.Contains("CRC mismatch", result.Error!.Message);
    }

    // ========== 边界值测试 ==========

    /// <summary>
    /// 验证不同 payload 长度（对齐边界）的正确解码。
    /// </summary>
    [Theory]
    [InlineData(0)]   // statusLen = 4
    [InlineData(1)]   // statusLen = 3
    [InlineData(2)]   // statusLen = 2
    [InlineData(3)]   // statusLen = 1
    [InlineData(4)]   // statusLen = 4
    [InlineData(7)]   // statusLen = 1
    [InlineData(8)]   // statusLen = 4
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
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
    }

    // ========== Buffer 相关测试 ==========

    /// <summary>
    /// 验证 buffer 太小时返回 RbfBufferTooSmallError。
    /// </summary>
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

    /// <summary>
    /// 验证 ReadFrameInto 的 zero-copy 特性：Payload 直接引用 buffer。
    /// </summary>
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
        Assert.Equal(payload, frame.Payload.ToArray());

        // 验证 zero-copy：修改 buffer 应该影响 Payload
        // Payload 位于 buffer 的 offset 8 处（HeadLen(4) + Tag(4) = 8）
        buffer[FrameLayout.PayloadOffset] = 0xFF; // 修改 Payload 第一个字节
        Assert.Equal(0xFF, frame.Payload[0]); // 应该能看到修改（证明是引用）
    }
}

