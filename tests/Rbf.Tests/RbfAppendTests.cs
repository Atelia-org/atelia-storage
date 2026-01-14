using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfFileImpl.Append 集成测试。
/// </summary>
/// <remarks>
/// 规范引用：
/// - @[S-RBF-DECISION-4B-ALIGNMENT-ROOT] - 4B 对齐根不变量
/// - @[F-FRAMEBYTES-FIELD-OFFSETS] - FrameBytes 布局、HeadLen/TailLen 对称性
/// - @[F-FENCE-VALUE-IS-RBF1-ASCII-4B] - Fence 必须是 ASCII "RBF1"
/// - @[F-CRC32C-COVERAGE] - CRC 覆盖范围
/// </remarks>
public class RbfAppendTests : IDisposable {
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
    /// 验证单帧写入：TailOffset 正确更新、SizedPtr 正确、文件内容符合规范。
    /// </summary>
    [Fact]
    public void Append_SingleFrame_WritesCorrectFormat() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03]; // 3 字节 payload
        uint tag = 0x12345678;

        // Act
        SizedPtr ptr;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            ptr = rbf.Append(tag, payload);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, tailOffset);

        // Assert - SizedPtr 返回值
        Assert.Equal(4UL, ptr.OffsetBytes); // Genesis(4) 后的位置
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(payload.Length);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - TailOffset
        Assert.Equal(4 + expectedHeadLen + 4, tailOffset); // Genesis(4) + Frame + Fence(4)

        // Assert - 文件内容
        var fileData = File.ReadAllBytes(path);
        Assert.Equal(tailOffset, fileData.Length);

        // Genesis Fence (offset 0)
        AssertFence(fileData, 0);

        // Frame (offset 4)
        AssertHeadTailSymmetry(fileData, 4, (uint)expectedHeadLen);
        AssertCrc(fileData, 4, (uint)expectedHeadLen);

        // Tag 验证 (offset 8)
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(fileData.AsSpan(8));
        Assert.Equal(tag, actualTag);

        // Payload 验证 (offset 12)
        Assert.Equal(payload, fileData.AsSpan(12, payload.Length).ToArray());

        // 帧后 Fence (offset 4 + headLen)
        AssertFence(fileData, 4 + expectedHeadLen);
    }

    /// <summary>
    /// 验证多帧追加：第二帧 offset 正确、所有帧后都有 Fence。
    /// </summary>
    [Fact]
    public void Append_MultipleFrames_AppendSequentially() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload1 = [0xAA, 0xBB]; // 2 字节
        byte[] payload2 = [0xCC, 0xDD, 0xEE, 0xFF, 0x11]; // 5 字节
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;

        // Act
        SizedPtr ptr1, ptr2;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            ptr1 = rbf.Append(tag1, payload1);
            ptr2 = rbf.Append(tag2, payload2);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 4B 对齐根不变量（两帧都验证）
        AssertAlignment(ptr1, 4 + RbfRawOps.ComputeHeadLen(payload1.Length) + 4);
        AssertAlignment(ptr2, tailOffset);

        // Assert - 第一帧位置
        Assert.Equal(4UL, ptr1.OffsetBytes); // Genesis(4) 后
        int headLen1 = RbfRawOps.ComputeHeadLen(payload1.Length);
        Assert.Equal((uint)headLen1, ptr1.LengthBytes);

        // Assert - 第二帧位置
        // secondFrameOffset = Genesis(4) + headLen1 + Fence(4)
        long expectedOffset2 = 4 + headLen1 + 4;
        Assert.Equal((ulong)expectedOffset2, ptr2.OffsetBytes);
        int headLen2 = RbfRawOps.ComputeHeadLen(payload2.Length);
        Assert.Equal((uint)headLen2, ptr2.LengthBytes);

        // Assert - TailOffset
        long expectedTailOffset = 4 + headLen1 + 4 + headLen2 + 4; // Genesis + F1 + Fence + F2 + Fence
        Assert.Equal(expectedTailOffset, tailOffset);

        // Assert - 文件内容
        var fileData = File.ReadAllBytes(path);

        // Genesis Fence (offset 0)
        AssertFence(fileData, 0);

        // 第一帧 (offset 4)
        AssertHeadTailSymmetry(fileData, 4, (uint)headLen1);
        AssertCrc(fileData, 4, (uint)headLen1);

        // 第一帧后 Fence (offset 4 + headLen1)
        AssertFence(fileData, 4 + headLen1);

        // 第二帧 (offset 4 + headLen1 + 4)
        int frame2Offset = 4 + headLen1 + 4;
        AssertHeadTailSymmetry(fileData, frame2Offset, (uint)headLen2);
        AssertCrc(fileData, frame2Offset, (uint)headLen2);

        // 第二帧后 Fence (offset 4 + headLen1 + 4 + headLen2)
        AssertFence(fileData, frame2Offset + headLen2);
    }

    /// <summary>
    /// 验证空 payload 场景。
    /// </summary>
    [Fact]
    public void Append_EmptyPayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = []; // 空 payload
        uint tag = 0xDEADBEEF;

        // Act
        SizedPtr ptr;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            ptr = rbf.Append(tag, payload);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, tailOffset);

        // Assert - SizedPtr
        Assert.Equal(4UL, ptr.OffsetBytes);
        // 空 payload：HeadLen = 4 + 4 + 0 + 4 + 4 + 4 = 20
        // StatusLen for payload=0: 1 + ((4 - 1) % 4) = 1 + 3 = 4
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(0);
        Assert.Equal(20, expectedHeadLen); // 验证计算
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - TailOffset
        Assert.Equal(4 + expectedHeadLen + 4, tailOffset);

        // Assert - 文件内容
        var fileData = File.ReadAllBytes(path);
        Assert.Equal(tailOffset, fileData.Length);

        // Genesis Fence
        AssertFence(fileData, 0);

        // Frame
        AssertHeadTailSymmetry(fileData, 4, (uint)expectedHeadLen);
        AssertCrc(fileData, 4, (uint)expectedHeadLen);

        // Tag 验证
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(fileData.AsSpan(8));
        Assert.Equal(tag, actualTag);

        // 帧后 Fence
        AssertFence(fileData, 4 + expectedHeadLen);
    }

    /// <summary>
    /// 验证大 payload（>1KB）场景，触发 ArrayPool 分配路径。
    /// </summary>
    [Fact]
    public void Append_LargePayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[2048]; // 2KB payload，超过 512B 阈值
        new Random(42).NextBytes(payload); // 填充随机数据
        uint tag = 0xCAFEBABE;

        // Act
        SizedPtr ptr;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            ptr = rbf.Append(tag, payload);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, tailOffset);

        // Assert - SizedPtr
        Assert.Equal(4UL, ptr.OffsetBytes);
        int expectedHeadLen = RbfRawOps.ComputeHeadLen(payload.Length);
        Assert.Equal((uint)expectedHeadLen, ptr.LengthBytes);

        // Assert - TailOffset
        Assert.Equal(4 + expectedHeadLen + 4, tailOffset);

        // Assert - 文件内容
        var fileData = File.ReadAllBytes(path);
        Assert.Equal(tailOffset, fileData.Length);

        // Genesis Fence
        AssertFence(fileData, 0);

        // Frame HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(fileData, 4, (uint)expectedHeadLen);

        // CRC 验证
        AssertCrc(fileData, 4, (uint)expectedHeadLen);

        // Tag 验证
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(fileData.AsSpan(8));
        Assert.Equal(tag, actualTag);

        // Payload 验证（完整比对）
        Assert.Equal(payload, fileData.AsSpan(12, payload.Length).ToArray());

        // 帧后 Fence
        AssertFence(fileData, 4 + expectedHeadLen);
    }
}
