using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>RbfRawOps 格式单元测试（v0.40 格式）。</summary>
/// <remarks>
/// 职责：验证 RawOps 层输出的字节序列符合规范。
/// 规范引用：
/// - @[S-RBF-DECISION-4B-ALIGNMENT-ROOT] - 4B 对齐根不变量
/// - @[F-FRAMEBYTES-LAYOUT] - FrameBytes 布局（v0.40）
/// - @[F-FENCE-RBF1-ASCII-4B] - Fence 必须是 ASCII "RBF1"
/// - @[F-PAYLOAD-CRC-COVERAGE] - PayloadCrc 覆盖范围
/// - @[F-TRAILER-CRC-COVERAGE] - TrailerCrc 覆盖范围
/// </remarks>
public class RbfAppendImplTests : IDisposable {
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

    // ========== 辅助断言方法 ==========

    /// <summary>验证 4B 对齐根不变量 @[S-RBF-DECISION-4B-ALIGNMENT-ROOT]。</summary>
    private static void AssertAlignment(SizedPtr ptr, long tailOffset) {
        Assert.Equal(0L, ptr.Offset % 4);
        Assert.Equal(0, ptr.Length % 4);
        Assert.Equal(0L, tailOffset % 4);
    }

    /// <summary>验证 Fence 字节值 @[F-FENCE-RBF1-ASCII-4B]。</summary>
    private static void AssertFence(ReadOnlySpan<byte> data, int offset) {
        Assert.Equal(0x52, data[offset]);     // 'R'
        Assert.Equal(0x42, data[offset + 1]); // 'B'
        Assert.Equal(0x46, data[offset + 2]); // 'F'
        Assert.Equal(0x31, data[offset + 3]); // '1'
    }

    /// <summary>验证 HeadLen/TailLen 对称性 @[F-FRAMEBYTES-LAYOUT]（v0.40 格式）。</summary>
    /// <remarks>
    /// v0.40 布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
    /// TrailerCodeword: [TrailerCrc(4)][FrameDescriptor(4)][FrameTag(4)][TailLen(4)]
    /// </remarks>
    private static void AssertHeadTailSymmetry(ReadOnlySpan<byte> data, int frameOffset, uint expectedHeadLen) {
        // 读取 HeadLen (offset 0)
        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(data[frameOffset..]);
        Assert.Equal(expectedHeadLen, headLen);

        // 读取 TailLen (TrailerCodeword 末尾 4 字节)
        // TailLen 位于 frameOffset + headLen - 4
        int tailLenOffset = frameOffset + (int)headLen - 4;
        uint tailLen = BinaryPrimitives.ReadUInt32LittleEndian(data[tailLenOffset..]);
        Assert.Equal(expectedHeadLen, tailLen);
    }

    /// <summary>验证 PayloadCrc32C（v0.40 格式）。</summary>
    /// <remarks>
    /// PayloadCrc 覆盖：Payload + TailMeta + Padding
    /// 即从 offset+4 到 PayloadCrcOffset
    /// </remarks>
    private static void AssertPayloadCrc(ReadOnlySpan<byte> data, int frameOffset, FrameLayout layout) {
        // PayloadCrc 覆盖范围：Payload + TailMeta + Padding
        int crcInputStart = frameOffset + FrameLayout.PayloadCrcCoverageStart;
        int crcInputLen = layout.PayloadCrcCoverageLength;
        var crcInput = data.Slice(crcInputStart, crcInputLen);
        uint expectedCrc = RollingCrc.CrcForward(crcInput);

        // 读取实际 PayloadCrc
        int payloadCrcOffset = frameOffset + layout.PayloadCrcOffset;
        uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[payloadCrcOffset..]);
        Assert.Equal(expectedCrc, actualCrc);
    }

    /// <summary>验证 TrailerCrc32C（v0.40 格式）。</summary>
    private static void AssertTrailerCrc(ReadOnlySpan<byte> data, int frameOffset, FrameLayout layout) {
        // TrailerCodeword 位于帧末尾 16 字节
        int trailerOffset = frameOffset + layout.TrailerCodewordOffset;
        var trailerCodeword = data.Slice(trailerOffset, TrailerCodewordHelper.Size);

        // 验证 TrailerCrc
        Span<byte> temp = stackalloc byte[TrailerCodewordHelper.Size];
        trailerCodeword.CopyTo(temp);
        Assert.True(Atelia.Data.Hashing.RollingCrc.CheckCodewordBackward(temp), "TrailerCrc32C verification failed");
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

    /// <summary>验证不同大小 payload 的写入格式正确性（v0.40 格式）。
    /// 覆盖场景：空 payload、关键边界（由 _GetKeyAppendPayloadLength 动态提供）、大包等。</summary>
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
        long nextTailOffset = 0;
        var appendResult = RbfAppendImpl.Append(handle, ref nextTailOffset, payload, default, tag);
        Assert.True(appendResult.IsSuccess, $"Append failed: {appendResult.Error?.Message}");
        var ptr = appendResult.Value!;

        // Assert - 4B 对齐根不变量
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr
        Assert.Equal(0L, ptr.Offset); // 从 offset 0 写入
        var layout = new FrameLayout(payload.Length);
        int expectedHeadLen = layout.FrameLength;
        Assert.Equal(expectedHeadLen, ptr.Length);

        // Assert - nextTailOffset
        Assert.Equal(expectedHeadLen + RbfLayout.FenceSize, nextTailOffset); // Frame + Fence

        // Assert - 读取文件内容，完整验证格式
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(data, 0, (uint)expectedHeadLen);

        // PayloadCrc
        AssertPayloadCrc(data, 0, layout);

        // TrailerCrc
        AssertTrailerCrc(data, 0, layout);

        // Tag 验证（v0.40: Tag 在 TrailerCodeword 中，偏移 = TrailerCodewordOffset + 8）
        int tagOffset = layout.TrailerCodewordOffset + 8;
        uint actualTag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tagOffset));
        Assert.Equal(tag, actualTag);

        // Payload 验证 (offset 4)
        Assert.Equal(payload, data.AsSpan(FrameLayout.PayloadOffset, payload.Length).ToArray());

        // Trailing Fence (offset headLen)
        AssertFence(data, expectedHeadLen);
    }

    /// <summary>验证指定 writeOffset 的追加位置正确（参数化测试，v0.40 格式）。</summary>
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
        long nextTailOffset = writeOffset;
        var appendResult = RbfAppendImpl.Append(handle, ref nextTailOffset, payload, default, tag);
        Assert.True(appendResult.IsSuccess, $"Append failed: {appendResult.Error?.Message}");
        var ptr = appendResult.Value!;

        // Assert - SizedPtr 指向正确位置
        Assert.Equal(writeOffset, ptr.Offset);
        var layout = new FrameLayout(payload.Length);
        int expectedHeadLen = layout.FrameLength;
        Assert.Equal(expectedHeadLen, ptr.Length);

        // Assert - nextTailOffset
        Assert.Equal(writeOffset + expectedHeadLen + RbfLayout.FenceSize, nextTailOffset);

        // Assert - 验证文件内容在正确位置
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // 验证帧写在 offset 位置
        AssertHeadTailSymmetry(data, (int)writeOffset, (uint)expectedHeadLen);
        AssertPayloadCrc(data, (int)writeOffset, layout);
        AssertTrailerCrc(data, (int)writeOffset, layout);
        AssertFence(data, (int)writeOffset + expectedHeadLen);

        // Payload 验证
        Assert.Equal(payload, data.AsSpan((int)writeOffset + FrameLayout.PayloadOffset, payload.Length).ToArray());
    }

    /// <summary>验证大帧路径：10MB payload（压力测试，v0.40 格式）。
    /// 单独保留以进行非完整比对的快速验证，并作为压力测试用例。</summary>
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
        long nextTailOffset = 0;
        var appendResult = RbfAppendImpl.Append(handle, ref nextTailOffset, payload, default, tag);
        Assert.True(appendResult.IsSuccess, $"Append failed: {appendResult.Error?.Message}");
        var ptr = appendResult.Value!;

        // Assert - 4B 对齐
        AssertAlignment(ptr, nextTailOffset);

        // Assert - SizedPtr
        var layout = new FrameLayout(payload.Length);
        int expectedHeadLen = layout.FrameLength;
        Assert.Equal(expectedHeadLen, ptr.Length);

        // Assert - 读取文件内容并验证关键点（不做完整比对以节省时间）
        var data = new byte[nextTailOffset];
        RandomAccess.Read(handle, data, 0);

        // HeadLen/TailLen 对称性
        AssertHeadTailSymmetry(data, 0, (uint)expectedHeadLen);

        // PayloadCrc（关键验证）
        AssertPayloadCrc(data, 0, layout);

        // TrailerCrc（关键验证）
        AssertTrailerCrc(data, 0, layout);

        // Payload 首尾验证
        Assert.Equal(payload[..16], data.AsSpan(FrameLayout.PayloadOffset, 16).ToArray());
        Assert.Equal(payload[^16..], data.AsSpan(FrameLayout.PayloadOffset + payload.Length - 16, 16).ToArray());

        // Fence
        AssertFence(data, expectedHeadLen);
    }
}
