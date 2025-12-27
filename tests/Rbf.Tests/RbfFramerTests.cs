using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using Atelia.Rbf;
using FluentAssertions;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfFramer 和 RbfFrameBuilder 测试。
/// </summary>
/// <remarks>
/// 覆盖：[A-RBF-FRAMER-INTERFACE], [A-RBF-FRAME-BUILDER], [S-RBF-BUILDER-AUTO-ABORT],
/// [F-FRAME-LAYOUT], [F-CRC32C-COVERAGE]
/// </remarks>
public class RbfFramerTests {
    /// <summary>
    /// 测试 RBF-OK-001: 空 payload 帧写入（PayloadLen=0 → StatusLen=4）。
    /// </summary>
    [Fact]
    public void Append_EmptyPayload_WritesValidFrame() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        var tag = new FrameTag(0x41424344); // "DCBA" in LE

        // Act
        var address = framer.Append(tag, ReadOnlySpan<byte>.Empty);

        // Assert
        address.Value.Should().Be(4); // Genesis Fence 后

        // 验证写入的数据
        var data = buffer.WrittenSpan;

        // Genesis Fence (4) + HeadLen(4) + FrameTag(4) + Payload(0) + FrameStatus(4) + TailLen(4) + CRC(4) + Fence(4)
        // = 4 + 20 + 4 = 28 bytes
        data.Length.Should().Be(28);

        // 验证 Genesis Fence
        data.Slice(0, 4).SequenceEqual(RbfConstants.FenceBytes).Should().BeTrue();

        // 验证 HeadLen = 20
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)).Should().Be(20);

        // 验证 FrameTag
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)).Should().Be(0x41424344);

        // 验证 FrameStatus (4 bytes of 0x03 for Valid with StatusLen=4)
        // New format: bit 7=0 (Valid), bits 0-1 = StatusLen-1 = 3 → 0x03
        data.Slice(12, 4).ToArray().Should().AllBeEquivalentTo((byte)0x03);

        // 验证 TailLen = 20
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4)).Should().Be(20);

        // 验证 CRC32C
        var crcData = data.Slice(8, 12); // FrameTag(4) + FrameStatus(4) + TailLen(4)
        var expectedCrc = RbfCrc.Compute(crcData);
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4)).Should().Be(expectedCrc);

        // 验证尾部 Fence
        data.Slice(24, 4).SequenceEqual(RbfConstants.FenceBytes).Should().BeTrue();
    }

    /// <summary>
    /// 测试有 payload 帧写入（PayloadLen=5 → StatusLen=3）。
    /// </summary>
    [Fact]
    public void Append_WithPayload_WritesValidFrame() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        var tag = new FrameTag(0x12345678);
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05]; // 5 bytes

        // Act
        var address = framer.Append(tag, payload);

        // Assert
        address.Value.Should().Be(4);

        var data = buffer.WrittenSpan;

        // Genesis Fence(4) + HeadLen(4) + FrameTag(4) + Payload(5) + FrameStatus(3) + TailLen(4) + CRC(4) + Fence(4)
        // = 4 + 24 + 4 = 32 bytes
        // HeadLen = 16 + 5 + 3 = 24
        data.Length.Should().Be(32);

        // 验证 HeadLen = 24
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)).Should().Be(24);

        // 验证 Payload
        data.Slice(12, 5).ToArray().Should().Equal(payload);

        // 验证 FrameStatus (3 bytes of 0x02 for Valid with StatusLen=3)
        // PayloadLen=5, 5%4=1, StatusLen=3 → bits 0-1 = 2
        data.Slice(17, 3).ToArray().Should().AllBeEquivalentTo((byte)0x02);

        // 验证 TailLen = 24
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4)).Should().Be(24);

        // 验证 CRC32C
        // CRC 覆盖: FrameTag(4) + Payload(5) + FrameStatus(3) + TailLen(4) = 16 bytes
        var crcData = data.Slice(8, 16);
        var expectedCrc = RbfCrc.Compute(crcData);
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4)).Should().Be(expectedCrc);
    }

    /// <summary>
    /// 测试 StatusLen 覆盖（PayloadLen % 4 = 0, 1, 2, 3）。
    /// </summary>
    [Theory]
    [InlineData(0, 4)]  // PayloadLen=0, StatusLen=4
    [InlineData(1, 3)]  // PayloadLen=1, StatusLen=3
    [InlineData(2, 2)]  // PayloadLen=2, StatusLen=2
    [InlineData(3, 1)]  // PayloadLen=3, StatusLen=1
    [InlineData(4, 4)]  // PayloadLen=4, StatusLen=4
    [InlineData(5, 3)]  // PayloadLen=5, StatusLen=3
    public void Append_VariousPayloadLengths_CorrectStatusLen(int payloadLen, int expectedStatusLen) {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        var tag = new FrameTag(0xDEADBEEF);
        var payload = new byte[payloadLen];

        // Act
        framer.Append(tag, payload);

        // Assert
        var data = buffer.WrittenSpan;
        int expectedFrameLen = 16 + payloadLen + expectedStatusLen;

        // 验证 HeadLen
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)).Should().Be((uint)expectedFrameLen);

        // 验证 StatusLen 字节全部相同（新位域格式：bits 0-1 = StatusLen-1）
        int statusOffset = 4 + 4 + 4 + payloadLen; // Genesis + HeadLen + FrameTag + Payload
        var statusBytes = data.Slice(statusOffset, expectedStatusLen);
        byte expectedStatusByte = (byte)(expectedStatusLen - 1); // Valid with StatusLen encoded
        statusBytes.ToArray().Should().AllBeEquivalentTo(expectedStatusByte);
    }

    /// <summary>
    /// 测试 CRC32C 正确性验证。
    /// </summary>
    [Fact]
    public void Append_CrcCoversCorrectRange() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        var tag = new FrameTag(0xCAFEBABE);
        byte[] payload = [0xAA, 0xBB, 0xCC]; // 3 bytes → StatusLen = 1

        // Act
        framer.Append(tag, payload);

        // Assert
        var data = buffer.WrittenSpan;

        // HeadLen = 16 + 3 + 1 = 20
        int frameLen = 20;

        // CRC 覆盖范围: FrameTag(4) + Payload(3) + FrameStatus(1) + TailLen(4) = 12 bytes
        // 在 data 中: offset 8 to 20 (从 Genesis Fence 后开始算)
        int crcStart = 8;  // FrameTag 起点 (4 + 4)
        int crcLen = 4 + 3 + 1 + 4; // 12 bytes
        var crcData = data.Slice(crcStart, crcLen);
        var computedCrc = RbfCrc.Compute(crcData);

        int crcOffset = 4 + frameLen - 4; // Genesis + (frameLen - 4) = offset of CRC
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(crcOffset, 4));

        storedCrc.Should().Be(computedCrc);
    }

    /// <summary>
    /// 测试 BeginFrame + Commit 写入帧。
    /// </summary>
    [Fact]
    public void BeginFrame_Commit_WritesValidFrame() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        var tag = new FrameTag(0x11223344);

        // Act
        Address64 address;
        using (var builder = framer.BeginFrame(tag)) {
            // 写入 payload
            var span = builder.Payload.GetSpan(4);
            span[0] = 0xDE;
            span[1] = 0xAD;
            span[2] = 0xBE;
            span[3] = 0xEF;
            builder.Payload.Advance(4);

            address = builder.Commit();
        }

        // Assert
        address.Value.Should().Be(4);

        var data = buffer.WrittenSpan;
        // Genesis(4) + HeadLen(4) + Tag(4) + Payload(4) + Status(4) + TailLen(4) + CRC(4) + Fence(4)
        // HeadLen = 16 + 4 + 4 = 24
        data.Length.Should().Be(32);

        // 验证 HeadLen
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)).Should().Be(24);

        // 验证 Payload
        data.Slice(12, 4).ToArray().Should().Equal([0xDE, 0xAD, 0xBE, 0xEF]);
    }

    /// <summary>
    /// 测试 [S-RBF-BUILDER-AUTO-ABORT]: 未 Commit 时写入 Tombstone。
    /// </summary>
    [Fact]
    public void BeginFrame_DisposeWithoutCommit_DoesNotWriteFrame_ZeroIoAbort() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);
        var tag = new FrameTag(0x55667788);

        // Act
        using (var builder = framer.BeginFrame(tag)) {
            // 写入一些 payload
            var span = builder.Payload.GetSpan(2);
            span[0] = 0x12;
            span[1] = 0x34;
            builder.Payload.Advance(2);

            // 不调用 Commit，直接 Dispose
        }

        // Assert
        var data = buffer.WrittenSpan;

        // Zero I/O abort: should only have Genesis Fence.
        data.Length.Should().Be(4);
        data.Slice(0, 4).SequenceEqual(RbfConstants.FenceBytes).Should().BeTrue();
    }

    /// <summary>
    /// 测试 [S-RBF-BUILDER-SINGLE-OPEN]: 不允许同时打开多个 Builder。
    /// </summary>
    [Fact]
    public void BeginFrame_WhileBuilderOpen_ThrowsInvalidOperationException() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        using var builder = framer.BeginFrame(new FrameTag(1));

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => framer.BeginFrame(new FrameTag(2)));
        ex.Message.Should().Contain("already open");
    }

    /// <summary>
    /// 测试 Builder 打开时不能 Append。
    /// </summary>
    [Fact]
    public void Append_WhileBuilderOpen_ThrowsInvalidOperationException() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        using var builder = framer.BeginFrame(new FrameTag(1));

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => framer.Append(new FrameTag(2), []));
        ex.Message.Should().Contain("RbfFrameBuilder is open");
    }

    /// <summary>
    /// 测试重复 Commit 抛出异常。
    /// </summary>
    [Fact]
    public void Commit_Twice_ThrowsInvalidOperationException() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var builder = framer.BeginFrame(new FrameTag(1));
        builder.Commit();

        // Act & Assert
        InvalidOperationException? ex = null;
        try {
            builder.Commit();
        }
        catch (InvalidOperationException e) {
            ex = e;
        }

        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("already been committed");

        // Cleanup
        builder.Dispose();
    }

    /// <summary>
    /// 测试多帧连续写入。
    /// </summary>
    [Fact]
    public void Append_MultipleFrames_CorrectAddresses() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);

        // Act
        var addr1 = framer.Append(new FrameTag(1), [0x01]);
        var addr2 = framer.Append(new FrameTag(2), [0x02, 0x03]);
        var addr3 = framer.Append(new FrameTag(3), []);

        // Assert
        // Frame 1: Genesis(4) → HeadLen starts at 4
        addr1.Value.Should().Be(4);

        // Frame 1 size: HeadLen(4) + Tag(4) + Payload(1) + Status(3) + TailLen(4) + CRC(4) + Fence(4) = 24
        // Frame 1 HeadLen = 16 + 1 + 3 = 20, total written = 20 + 4(fence) = 24
        // Frame 2 starts at: 4 + 24 = 28
        addr2.Value.Should().Be(28);

        // Frame 2 size: HeadLen(4) + Tag(4) + Payload(2) + Status(2) + TailLen(4) + CRC(4) + Fence(4) = 24
        // Frame 2 HeadLen = 16 + 2 + 2 = 20, total written = 20 + 4 = 24
        // Frame 3 starts at: 28 + 24 = 52
        addr3.Value.Should().Be(52);
    }

    /// <summary>
    /// 测试 Address64 对齐验证。
    /// </summary>
    [Fact]
    public void Append_Returns4ByteAlignedAddress() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 0, writeGenesis: true);

        // Act
        for (int i = 0; i < 10; i++) {
            var payload = new byte[i];
            var address = framer.Append(new FrameTag((uint)i), payload);

            // Assert
            (address.Value % 4).Should().Be(0, $"Address for frame {i} should be 4-byte aligned");
            address.IsValid.Should().BeTrue();
        }
    }

    /// <summary>
    /// 测试 ReservablePayload 返回 null（MVP 实现）。
    /// </summary>
    [Fact]
    public void BeginFrame_ReservablePayload_AllowsReserveAndBackfill() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);

        // Act
        using var builder = framer.BeginFrame(new FrameTag(1));

        builder.ReservablePayload.Should().NotBeNull();

        // Reserve a 4-byte length prefix, then write payload, then backfill and commit reservation.
        var rw = builder.ReservablePayload!;
        var lenSpan = rw.ReserveSpan(4, out var token, tag: "len");
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var span = builder.Payload.GetSpan(payload.Length);
        payload.CopyTo(span);
        builder.Payload.Advance(payload.Length);

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenSpan, payload.Length);
        rw.Commit(token);

        builder.Commit();

        // Assert framing still valid.
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();
        frames.Should().HaveCount(1);
        scanner.ReadPayload(frames[0]).Should().Equal(new byte[] { 0x03, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC });
    }

    /// <summary>
    /// 测试无 Genesis Fence 模式。
    /// </summary>
    [Fact]
    public void Framer_WithoutGenesis_StartsAtPosition0() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer, startPosition: 100, writeGenesis: false);

        // Act
        var address = framer.Append(new FrameTag(1), []);

        // Assert
        address.Value.Should().Be(100);

        // 验证没有写入 Genesis Fence
        var data = buffer.WrittenSpan;
        // 第一个字节应该是 HeadLen，不是 Fence
        data.Slice(0, 4).SequenceEqual(RbfConstants.FenceBytes).Should().BeFalse();
    }
}
