using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfRawOps.ReadFrame 格式验证测试。
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
public class RbfReadFrameTests : IDisposable {
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
    /// 构造一个有效帧的字节数组。
    /// </summary>
    /// <param name="tag">帧 Tag。</param>
    /// <param name="payload">Payload 数据。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <returns>完整帧字节数组（不含 Genesis/Fence）。</returns>
    private static byte[] CreateValidFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        int statusLen = FrameStatusHelper.ComputeStatusLen(payload.Length);
        int headLen = RbfConstants.FrameFixedOverheadBytes + payload.Length + statusLen;

        byte[] frame = new byte[headLen];
        Span<byte> span = frame;

        // HeadLen (offset 0)
        BinaryPrimitives.WriteUInt32LittleEndian(span[..RbfConstants.HeadLenFieldLength], (uint)headLen);

        // Tag (offset 4)
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(RbfConstants.TagFieldOffset, RbfConstants.TagFieldLength), tag);

        // Payload (offset 8)
        payload.CopyTo(span.Slice(8, payload.Length));

        // Status (offset 8 + payloadLen)
        int statusOffset = 8 + payload.Length;
        FrameStatusHelper.FillStatus(span.Slice(statusOffset, statusLen), isTombstone, statusLen);

        // TailLen (offset headLen - TailSuffixLength)
        int tailLenOffset = headLen - RbfConstants.TailSuffixLength;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(tailLenOffset, RbfConstants.TailLenFieldLength), (uint)headLen);

        // CRC (offset headLen - CrcFieldLength)
        // CRC 覆盖范围：Tag(4) + Payload(N) + Status(1-4) + TailLen(4) = frame[TagFieldOffset..(headLen-CrcFieldLength)]
        int crcOffset = headLen - RbfConstants.CrcFieldLength;
        ReadOnlySpan<byte> crcInput = span.Slice(RbfConstants.TagFieldOffset, headLen - RbfConstants.TailSuffixLength);
        uint crc = Crc32CHelper.Compute(crcInput);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(crcOffset, RbfConstants.CrcFieldLength), crc);

        return frame;
    }

    /// <summary>
    /// 构造带 Genesis + Frame + Fence 的完整文件内容。
    /// </summary>
    private static byte[] CreateValidFileWithFrame(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        byte[] frameBytes = CreateValidFrameBytes(tag, payload, isTombstone);
        int totalLen = RbfConstants.GenesisLength + frameBytes.Length + RbfConstants.FenceLength;
        byte[] file = new byte[totalLen];

        // Genesis
        RbfConstants.Fence.CopyTo(file.AsSpan(0, 4));
        // Frame
        frameBytes.CopyTo(file.AsSpan(RbfConstants.GenesisLength));
        // Fence
        RbfConstants.Fence.CopyTo(file.AsSpan(RbfConstants.GenesisLength + frameBytes.Length, 4));

        return file;
    }

    // ========== 正常路径测试 ==========

    /// <summary>
    /// 验证正常帧的读取：Tag、Payload、IsTombstone 正确解码。
    /// 使用 Append 写入 -> ReadFrame 验证的闭环测试。
    /// </summary>
    [Fact]
    public void ReadFrame_ValidFrame_ReturnsCorrectData() {
        // Arrange: 使用 Append 写入帧
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        uint tag = 0x12345678;

        using var rbfFile = RbfFile.CreateNew(path);
        var ptr = rbfFile.Append(tag, payload);

        // Act: 使用 ReadFrame 读取
        var result = rbfFile.ReadFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
        Assert.Equal(ptr, frame.Ptr);
    }

    /// <summary>
    /// 验证空 payload 帧（最小帧 20B）的读取。
    /// 使用 Append 写入 -> ReadFrame 验证的闭环测试。
    /// </summary>
    [Fact]
    public void ReadFrame_EmptyPayload_Succeeds() {
        // Arrange: 使用 Append 写入空 payload 帧
        var path = GetTempFilePath();
        byte[] payload = [];
        uint tag = 0xDEADBEEF;

        using var rbfFile = RbfFile.CreateNew(path);
        var ptr = rbfFile.Append(tag, payload);

        // 验证最小帧长度
        Assert.Equal(20u, ptr.LengthBytes);

        // Act: 使用 ReadFrame 读取
        var result = rbfFile.ReadFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Empty(frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
    }

    /// <summary>
    /// 验证大 payload（>4KB，触发 ArrayPool 路径）的读取。
    /// 使用 Append 写入 -> ReadFrame 验证的闭环测试。
    /// </summary>
    [Fact]
    public void ReadFrame_LargePayload_Succeeds() {
        // Arrange: 使用 Append 写入大 payload 帧
        var path = GetTempFilePath();
        byte[] payload = new byte[8192]; // 8KB，超过 MaxStackAllocSize(4096)
        new Random(42).NextBytes(payload);
        uint tag = 0xCAFEBABE;

        using var rbfFile = RbfFile.CreateNew(path);
        var ptr = rbfFile.Append(tag, payload);

        // Act: 使用 ReadFrame 读取
        var result = rbfFile.ReadFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        var frame = result.Value;
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

        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        var ptr = SizedPtr.Create(4, 16);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        int frameOffset = RbfConstants.GenesisLength;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(frameOffset, 4),
            999 // 错误的 HeadLen
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        int frameOffset = RbfConstants.GenesisLength;
        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        int tailLenOffset = frameOffset + frameLen - 8;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(tailLenOffset, 4),
            999 // 错误的 TailLen
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        int frameOffset = RbfConstants.GenesisLength;
        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out int statusLen);
        int statusOffset = frameOffset + 8 + payload.Length;

        // 设置保留位（Bit6-2）为非零
        byte invalidStatus = 0x04; // Bit2 = 1，保留位非零
        for (int i = 0; i < statusLen; i++) {
            fileContent[statusOffset + i] = invalidStatus;
        }

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        int statusLen = FrameStatusHelper.ComputeStatusLen(payload.Length);
        Assert.Equal(4, statusLen);

        // 修改 Status 区域的第一个字节，使其与其他字节不一致
        int frameOffset = RbfConstants.GenesisLength;
        int statusOffset = frameOffset + 8 + payload.Length;

        // 原始 status 字节 = 0x03（statusLen=4，非墓碑）
        // 修改第一个字节为不同值（但保留位仍为零）
        fileContent[statusOffset] = 0x02; // 不同的 status 值

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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
        int frameOffset = RbfConstants.GenesisLength;
        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        int crcOffset = frameOffset + frameLen - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileContent.AsSpan(crcOffset, 4),
            0xDEADBEEF // 错误的 CRC
        );

        File.WriteAllBytes(path, fileContent);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

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

        int frameLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        var ptr = SizedPtr.Create(RbfConstants.GenesisLength, (uint)frameLen);

        // Act
        var result = RbfRawOps.ReadFrame(handle, ptr);

        // Assert
        Assert.True(result.IsSuccess, $"Failed for payloadLen={payloadLen}: {result.Error?.Message}");
        var frame = result.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.IsTombstone);
    }
}
