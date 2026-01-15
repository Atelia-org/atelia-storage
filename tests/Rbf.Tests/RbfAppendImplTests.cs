using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfRawOps 格式单元测试。
/// </summary>
/// <remarks>
/// 职责：验证 RawOps 层输出的字节序列符合规范。
/// 规范引用：
/// - @[S-RBF-DECISION-4B-ALIGNMENT-ROOT] - 4B 对齐根不变量
/// - @[F-FRAMEBYTES-FIELD-OFFSETS] - FrameBytes 布局、HeadLen/TailLen 对称性
/// - @[F-FENCE-VALUE-IS-RBF1-ASCII-4B] - Fence 必须是 ASCII "RBF1"
/// - @[F-CRC32C-COVERAGE] - CRC 覆盖范围
/// </remarks>
public class RbfAppendImplTests : IDisposable {
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

    // ========== 辅助断言方法 ==========

    /// <summary>
    /// 验证 4B 对齐根不变量 @[S-RBF-DECISION-4B-ALIGNMENT-ROOT]。
    /// </summary>
    private static void AssertAlignment(SizedPtr ptr, long tailOffset) {
        Assert.Equal(0UL, ptr.OffsetBytes % 4);
        Assert.Equal(0U, ptr.LengthBytes % 4);
        Assert.Equal(0L, tailOffset % 4);
    }

    /// <summary>
    /// 验证 Fence 字节值 @[F-FENCE-VALUE-IS-RBF1-ASCII-4B]。
    /// </summary>
    private static void AssertFence(ReadOnlySpan<byte> data, int offset) {
        Assert.Equal(0x52, data[offset]);     // 'R'
        Assert.Equal(0x42, data[offset + 1]); // 'B'
        Assert.Equal(0x46, data[offset + 2]); // 'F'
        Assert.Equal(0x31, data[offset + 3]); // '1'
    }

    /// <summary>
    /// 验证 HeadLen/TailLen 对称性 @[F-FRAMEBYTES-FIELD-OFFSETS]。
    /// </summary>
    private static void AssertHeadTailSymmetry(ReadOnlySpan<byte> data, int frameOffset, uint expectedHeadLen) {
        // 读取 HeadLen (offset 0)
        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(data[frameOffset..]);
        Assert.Equal(expectedHeadLen, headLen);

        // 读取 TailLen (headLen - 8 处，即 CRC 前 4 字节)
        int tailLenOffset = frameOffset + (int)headLen - 8;
        uint tailLen = BinaryPrimitives.ReadUInt32LittleEndian(data[tailLenOffset..]);
        Assert.Equal(expectedHeadLen, tailLen);
    }

    /// <summary>
    /// 验证 CRC32C 校验和 @[F-CRC32C-COVERAGE]。
    /// </summary>
    private static void AssertCrc(ReadOnlySpan<byte> data, int frameOffset, uint headLen) {
        // CRC 覆盖：Tag(4) + Payload(N) + Status(1-4) + TailLen(4)
        // 即从 offset+4 到 offset+headLen-4
        var crcInput = data.Slice(frameOffset + 4, (int)headLen - 8);
        uint expectedCrc = Crc32CHelper.Compute(crcInput);

        // 读取实际 CRC (在 headLen-4 处)
        int crcOffset = frameOffset + (int)headLen - 4;
        uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[crcOffset..]);
        Assert.Equal(expectedCrc, actualCrc);
    }

    public static IEnumerable<object[]> GetVariablePayloadSizes() {
        // 动态获取基于 UnifiedBufferSize 的关键边界值
        foreach (var size in RbfAppendImpl.GetPayloadEdgeCase()) {
            yield return new object[] { size };
        }

        // 补充常见场景
        yield return new object[] { 0 }; // 典型小包
        yield return new object[] { 100 }; // 典型小包
        yield return new object[] { 2048 }; // ArrayPool 阈值
        yield return new object[] { 4096 }; // 4KB 页对齐
        yield return new object[] { 1024 * 1024 }; // 1MB 大包
    }

    /// <summary>
    /// 验证不同大小 payload 的写入格式正确性。
    /// 覆盖场景：空 payload、关键边界（由 _GetKeyAppendPayloadLength 动态提供）、大包等。
    /// </summary>
    [Theory]
    [MemberData(nameof(GetVariablePayloadSizes))]
    public void Append_VariablePayloads_WritesCorrectFormat(int payloadSize) {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        byte[] payload = new byte[payloadSize];
        if (payloadSize > 0) {
            new Random(payloadSize).NextBytes(payload); // Use size as seed for reproducibility
        }
        uint tag = 0x12345678;

        // Act
        var ptr = RbfAppendImpl.Append(handle, 0, tag, payload, out long nextTailOffset);

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr
        Assert.Equal(0UL, ptr.OffsetBytes); // 从 offset 0 写入
        int expectedHeadLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - nextTailOffset
        Assert.Equal(expectedHeadLen + 4, nextTailOffset); // Frame + Fence(4)

        // Assert - 读取文件内容，完整验证格式
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(data, 0, (uint)expectedHeadLen);

        // CRC
        AssertCrc(data, 0, (uint)expectedHeadLen);

        // Tag 验证 (offset 4)
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        Assert.Equal(tag, actualTag);

        // Payload 验证 (offset 8)
        Assert.Equal(payload, data.AsSpan(8, payload.Length).ToArray());

        // Trailing Fence (offset headLen)
        AssertFence(data, expectedHeadLen);
    }

    /// <summary>
    /// 验证指定 writeOffset 的追加位置正确（参数化测试）。
    /// </summary>
    [Theory]
    [InlineData(2, 100)]           // 小 payload，小 offset
    [InlineData(8 * 1024, 256)]    // 大 payload，中等 offset
    public void Append_WithOffset_WritesAtCorrectPosition(int payloadSize, long writeOffset) {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        byte[] payload = new byte[payloadSize];
        new Random(payloadSize).NextBytes(payload);
        uint tag = 0x11111111;

        // 预写一些占位数据
        var placeholder = new byte[writeOffset];
        RandomAccess.Write(handle, placeholder, 0);

        // Act
        var ptr = RbfAppendImpl.Append(handle, writeOffset, tag, payload, out long nextTailOffset);

        // Assert - SizedPtr 指向正确位置
        Assert.Equal((ulong)writeOffset, ptr.OffsetBytes);
        int expectedHeadLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - nextTailOffset
        Assert.Equal(writeOffset + expectedHeadLen + 4, nextTailOffset);

        // Assert - 验证文件内容在正确位置
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // 验证帧写在 offset 位置
        AssertHeadTailSymmetry(data, (int)writeOffset, (uint)expectedHeadLen);
        AssertCrc(data, (int)writeOffset, (uint)expectedHeadLen);
        AssertFence(data, (int)writeOffset + expectedHeadLen);

        // Payload 验证
        Assert.Equal(payload, data.AsSpan((int)writeOffset + 8, payload.Length).ToArray());
    }

    /// <summary>
    /// 验证大帧路径：10MB payload（压力测试）。
    /// 单独保留以进行非完整比对的快速验证，并作为压力测试用例。
    /// </summary>
    [Fact]
    public void Append_VeryLargePayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        // 10MB payload
        byte[] payload = new byte[10 * 1024 * 1024];
        new Random(12345).NextBytes(payload);
        uint tag = 0xDEADC0DE;

        // Act
        var ptr = RbfAppendImpl.Append(handle, 0, tag, payload, out long nextTailOffset);

        // Assert - 4B 对齐
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr
        int expectedHeadLen = RbfConstants.ComputeFrameLen(payload.Length, out _);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - 读取文件内容并验证关键点（不做完整比对以节省时间）
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(data, 0, (uint)expectedHeadLen);

        // CRC（关键验证）
        AssertCrc(data, 0, (uint)expectedHeadLen);

        // Payload 首尾验证
        Assert.Equal(payload[..16], data.AsSpan(8, 16).ToArray());
        Assert.Equal(payload[^16..], data.AsSpan(8 + payload.Length - 16, 16).ToArray());

        // Fence
        AssertFence(data, expectedHeadLen);
    }
}
