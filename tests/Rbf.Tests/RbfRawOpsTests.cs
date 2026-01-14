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
public class RbfRawOpsTests : IDisposable {
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
            } catch {
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

    // ========== 测试用例 ==========

    /// <summary>
    /// 验证单帧写入：完整格式验证（HeadLen/TailLen 对称、CRC、Fence）。
    /// </summary>
    [Fact]
    public void _AppendFrame_SingleFrame_WritesCorrectFormat() {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        byte[] payload = [0x01, 0x02, 0x03]; // 3 字节 payload
        uint tag = 0x12345678;

        // Act
        var ptr = RbfRawOps._AppendFrame(handle, 0, tag, payload, out long nextTailOffset);

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr 返回值
        Assert.Equal(0UL, ptr.OffsetBytes); // 从 offset 0 写入
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(payload.Length, out _);
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
    /// 验证空 payload 边界情况。
    /// </summary>
    [Fact]
    public void _AppendFrame_EmptyPayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        byte[] payload = []; // 空 payload
        uint tag = 0xDEADBEEF;

        // Act
        var ptr = RbfRawOps._AppendFrame(handle, 0, tag, payload, out long nextTailOffset);

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr
        Assert.Equal(0UL, ptr.OffsetBytes);
        // 空 payload：HeadLen = 4 + 4 + 0 + 4 + 4 + 4 = 20
        // StatusLen for payload=0: 1 + ((4 - 1) % 4) = 1 + 3 = 4
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(0, out _);
        Assert.Equal(20, expectedHeadLen); // 验证计算
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - nextTailOffset
        Assert.Equal(expectedHeadLen + 4, nextTailOffset);

        // Assert - 读取文件内容
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(data, 0, (uint)expectedHeadLen);

        // CRC
        AssertCrc(data, 0, (uint)expectedHeadLen);

        // Tag 验证 (offset 4)
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        Assert.Equal(tag, actualTag);

        // Trailing Fence (offset headLen)
        AssertFence(data, expectedHeadLen);
    }

    /// <summary>
    /// 验证大 payload（>512B）使用 ArrayPool 路径。
    /// </summary>
    [Fact]
    public void _AppendFrame_LargePayload_UsesArrayPool() {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        byte[] payload = new byte[2048]; // 2KB payload，超过 512B 阈值
        new Random(42).NextBytes(payload); // 填充随机数据
        uint tag = 0xCAFEBABE;

        // Act
        var ptr = RbfRawOps._AppendFrame(handle, 0, tag, payload, out long nextTailOffset);

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr
        Assert.Equal(0UL, ptr.OffsetBytes);
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(payload.Length, out _);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - nextTailOffset
        Assert.Equal(expectedHeadLen + 4, nextTailOffset);

        // Assert - 读取文件内容
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(data, 0, (uint)expectedHeadLen);

        // CRC 验证
        AssertCrc(data, 0, (uint)expectedHeadLen);

        // Tag 验证 (offset 4)
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        Assert.Equal(tag, actualTag);

        // Payload 验证（完整比对）
        Assert.Equal(payload, data.AsSpan(8, payload.Length).ToArray());

        // Trailing Fence
        AssertFence(data, expectedHeadLen);
    }

    /// <summary>
    /// 验证指定 writeOffset 的追加位置正确。
    /// </summary>
    [Fact]
    public void _AppendFrame_WithOffset_WritesAtCorrectPosition() {
        // Arrange
        var path = GetTempFilePath();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        byte[] payload = [0xAA, 0xBB];
        uint tag = 0x11111111;
        long writeOffset = 100; // 从偏移 100 开始写

        // 预写一些占位数据
        var placeholder = new byte[writeOffset];
        RandomAccess.Write(handle, placeholder, 0);

        // Act
        var ptr = RbfRawOps._AppendFrame(handle, writeOffset, tag, payload, out long nextTailOffset);

        // Assert - SizedPtr 指向正确位置
        Assert.Equal((ulong)writeOffset, ptr.OffsetBytes);
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(payload.Length, out _);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - nextTailOffset
        Assert.Equal(writeOffset + expectedHeadLen + 4, nextTailOffset);

        // Assert - 验证文件内容在正确位置
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // 验证帧写在 offset 100 位置
        AssertHeadTailSymmetry(data, (int)writeOffset, (uint)expectedHeadLen);
        AssertCrc(data, (int)writeOffset, (uint)expectedHeadLen);
        AssertFence(data, (int)writeOffset + expectedHeadLen);
    }
}
