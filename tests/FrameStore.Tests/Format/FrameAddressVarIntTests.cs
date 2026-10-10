using System.Buffers;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Format;

public class FrameAddressVarIntTests {
    [Theory]
    [InlineData("010000000700000400000000", "018702")]
    [InlineData("010000000D00000400000000", "018506")]
    [InlineData("EFCDAB89F0DEBC9A78563412", "EF9BAFCD089889FFF4BAE8CC971A")]
    [InlineData("EFCDAB899E158D9C78563412", "EF9BAFCD089E8FF3ECAA88CD9112")]
    [InlineData("EFCDAB890700000400000080", "EF9BAFCD0887828080808080808001")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFF", "FFFFFFFF0FFFFFFFFFFFFFFFFFFF01")]
    public void IndependentGoldenVectors_PreserveFixedBytesAndUseInterleavedVarInt(string fixedHex, string variableHex) {
        byte[] fixedWire = Convert.FromHexString(fixedHex);
        byte[] variableWire = Convert.FromHexString(variableHex);
        Assert.True(FrameAddress.TryRead(fixedWire, out FrameAddress address));

        var reader = new BareValueReader(variableWire);
        FrameAddress restored = FrameAddress.ReadVarInt(ref reader);
        reader.EnsureFullyConsumed();
        Assert.Equal(address, restored);
        Assert.Equal(variableWire.Length, address.MeasureVarInt());

        var sink = new ArrayBufferWriter<byte>();
        address.WriteVarInt(new BareValueWriter(sink));
        Assert.Equal(variableWire, sink.WrittenSpan.ToArray());

        byte[] restoredFixedWire = new byte[FrameAddress.EncodedSize];
        Assert.True(restored.TryWrite(restoredFixedWire));
        Assert.Equal(fixedWire, restoredFixedWire);
    }

    [Fact]
    public void MinimumAndMaximumVectors_KeepNumericGuardBounds() {
        var minimumReader = new BareValueReader(new byte[] { 0x01, 0x87, 0x02 });
        FrameAddress minimum = FrameAddress.ReadVarInt(ref minimumReader);
        Assert.Equal(1u, minimum.FileId);
        Assert.Equal(4L, minimum.Ticket.Offset);
        Assert.Equal(28, minimum.Ticket.Length);
        Assert.Equal(RbfScanBoundary.Empty.EndExclusive, minimum.Ticket.Offset);
        Assert.Equal(RbfFile.MeasureWriteSize(0, 0).Value.FrameLength, minimum.Ticket.Length);
        Assert.Equal(3, minimum.MeasureVarInt());

        // The header coordinate is numerically valid, without proving user-frame membership.
        var headerReader = new BareValueReader(new byte[] { 0x01, 0x85, 0x06 });
        FrameAddress header = FrameAddress.ReadVarInt(ref headerReader);
        Assert.Equal(4L, header.Ticket.Offset);
        Assert.Equal(52, header.Ticket.Length);

        byte[] maximumWire = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        var maximumReader = new BareValueReader(maximumWire);
        FrameAddress maximum = FrameAddress.ReadVarInt(ref maximumReader);
        Assert.Equal(uint.MaxValue, maximum.FileId);
        Assert.Equal(ulong.MaxValue, maximum.Ticket.Packed);
        Assert.Equal(SizedPtr.MaxOffset, maximum.Ticket.Offset);
        Assert.Equal(SizedPtr.MaxLength, maximum.Ticket.Length);
        Assert.True(maximum.Ticket.EndOffsetExclusive > SizedPtr.MaxOffset);
        Assert.Equal(15, maximum.MeasureVarInt());
        maximumReader.EnsureFullyConsumed();
    }

    [Fact]
    public void Measurement_MatchesOutputAtEveryFieldWidth() {
        (uint FileId, int Width)[] fileIds = [(1, 1), (128, 2), (16384, 3), (2097152, 4), (268435456, 5)];
        foreach ((uint fileId, int fileWidth) in fileIds) {
            for (int ticketWidth = 2; ticketWidth <= 10; ticketWidth++) {
                // Set the first bit of this Base128 group while retaining offset=4/length=28 lower bits.
                ulong serialized = 0x107UL | (1UL << (7 * (ticketWidth - 1)));
                FrameAddress address = FrameAddress.Create(fileId, SizedPtr.Deserialize(serialized));
                var sink = new ArrayBufferWriter<byte>();
                address.WriteVarInt(new BareValueWriter(sink));
                Assert.Equal(fileWidth + ticketWidth, address.MeasureVarInt());
                Assert.Equal(address.MeasureVarInt(), sink.WrittenCount);

                var reader = new BareValueReader(sink.WrittenSpan);
                Assert.Equal(address, FrameAddress.ReadVarInt(ref reader));
                reader.EnsureFullyConsumed();
            }
        }
    }

    [Fact]
    public void MultipleFields_ConsumeOnlyEachAddressAfterExistingPrefix() {
        byte[] golden = [0xAA, 0x55, 0x01, 0x87, 0x02,
            0xFF, 0xFF, 0xFF, 0xFF, 0x0F,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01,
            0xDE, 0xAD, 0xBE, 0xEF];
        var reader = new BareValueReader(golden);
        Assert.Equal((ushort)0x55AA, reader.ReadUInt16LE());
        FrameAddress minimum = FrameAddress.ReadVarInt(ref reader);
        Assert.Equal(5, reader.ConsumedCount);
        FrameAddress maximum = FrameAddress.ReadVarInt(ref reader);
        Assert.Equal(20, reader.ConsumedCount);
        Assert.Equal(0xEFBEADDEu, reader.ReadUInt32LE());
        reader.EnsureFullyConsumed();

        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        writer.WriteUInt16LE(0x55AA);
        minimum.WriteVarInt(writer);
        maximum.WriteVarInt(writer);
        writer.WriteUInt32LE(0xEFBEADDE);
        Assert.Equal(golden, sink.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData("018702")]
    [InlineData("EF9BAFCD089889FFF4BAE8CC971A")]
    [InlineData("FFFFFFFF0FFFFFFFFFFFFFFFFFFF01")]
    public void EveryTruncation_RollsBackWholeAddressAtNonzeroCursor(string hex) {
        byte[] complete = Convert.FromHexString(hex);
        for (int length = 0; length < complete.Length; length++) {
            byte[] input = new byte[length + 1];
            input[0] = 0xA5;
            complete.AsSpan(0, length).CopyTo(input.AsSpan(1));
            var reader = new BareValueReader(input);
            Assert.Equal((byte)0xA5, reader.ReadByte());
            AssertReadFailure<EndOfStreamException>(ref reader);
            Assert.Equal(complete[..length], reader.ReadRawBytes(length).ToArray());
        }
    }

    [Theory]
    [InlineData("80808080108702")] // VarUInt32 fifth payload exceeds 0x0F.
    [InlineData("8080808080008702")] // VarUInt32 fifth continuation cannot borrow a sixth byte.
    [InlineData("01FFFFFFFFFFFFFFFFFF02")] // VarUInt64 tenth payload exceeds 0x01.
    [InlineData("018080808080808080808000")] // VarUInt64 tenth continuation cannot borrow an eleventh byte.
    public void EitherFieldOverflow_RollsBackWholeAddress(string hex) {
        byte[] wire = Convert.FromHexString(hex);
        byte[] input = new byte[wire.Length + 1];
        input[0] = 0xA5;
        wire.CopyTo(input, 1);
        var reader = new BareValueReader(input);
        Assert.Equal((byte)0xA5, reader.ReadByte());
        AssertReadFailure<InvalidDataException>(ref reader);
        Assert.Equal(wire, reader.ReadRawBytes(wire.Length).ToArray());
    }

    [Fact]
    public void BoundedRedundantInputs_ConsumeActualWidthsAndRewriteShortest() {
        FrameAddress expected = FrameAddress.Create(1, SizedPtr.Create(4, 28));
        for (int fileWidth = 1; fileWidth <= 5; fileWidth++) {
            for (int ticketWidth = 2; ticketWidth <= 10; ticketWidth++) {
                byte[] input = Enumerable.Repeat((byte)0x80, 1 + fileWidth + ticketWidth + 1).ToArray();
                input[0] = 0xA5;
                input[1] = fileWidth == 1 ? (byte)0x01 : (byte)0x81;
                if (fileWidth > 1) { input[fileWidth] = 0x00; }
                input[1 + fileWidth] = 0x87;
                input[2 + fileWidth] = ticketWidth == 2 ? (byte)0x02 : (byte)0x82;
                if (ticketWidth > 2) { input[fileWidth + ticketWidth] = 0x00; }
                input[^1] = 0x5A;

                var reader = new BareValueReader(input);
                Assert.Equal((byte)0xA5, reader.ReadByte());
                FrameAddress address = FrameAddress.ReadVarInt(ref reader);
                Assert.Equal(expected, address);
                Assert.Equal(1 + fileWidth + ticketWidth, reader.ConsumedCount);
                Assert.Equal(1, reader.RemainingCount);
                Assert.Equal((byte)0x5A, reader.ReadByte());
                reader.EnsureFullyConsumed();
                Assert.Equal(3, address.MeasureVarInt());

                var sink = new ArrayBufferWriter<byte>();
                address.WriteVarInt(new BareValueWriter(sink));
                Assert.Equal(new byte[] { 0x01, 0x87, 0x02 }, sink.WrittenSpan.ToArray());
            }
        }
    }

    [Theory]
    [InlineData("000000000700000400000000", "008702")]
    [InlineData("010000000700000000000000", "018302")]
    [InlineData("010000000000000400000000", "0104")]
    [InlineData("010000000600000400000000", "018602")]
    [InlineData("010000000000000000000000", "0100")]
    [InlineData("000000000000000000000000", "0000")]
    public void InvalidNumericCoordinates_AreRejectedByBothCodecs(string fixedHex, string variableHex) {
        Assert.False(FrameAddress.TryRead(Convert.FromHexString(fixedHex), out FrameAddress address));
        Assert.Equal(default, address);
        byte[] variableWire = Convert.FromHexString(variableHex);
        byte[] input = new byte[variableWire.Length + 1];
        input[0] = 0xA5;
        variableWire.CopyTo(input, 1);
        var reader = new BareValueReader(input);
        Assert.Equal((byte)0xA5, reader.ReadByte());
        AssertReadFailure<InvalidDataException>(ref reader);
        Assert.Equal(variableWire, reader.ReadRawBytes(variableWire.Length).ToArray());
    }

    [Fact]
    public void DefaultAddress_RejectsMeasurementAndWriteBeforeAnySinkCall() {
        var sink = new RecordingSink();
        var writer = new BareValueWriter(sink);
        Assert.Throws<InvalidOperationException>(() => default(FrameAddress).MeasureVarInt());
        Assert.Throws<InvalidOperationException>(() => default(FrameAddress).WriteVarInt(writer));
        Assert.Empty(sink.Hints);
        Assert.Empty(sink.Advances);
        Assert.Empty(sink.Bytes);

        FrameAddress valid = FrameAddress.Create(1, SizedPtr.Create(4, 28));
        Assert.Throws<InvalidOperationException>(() => valid.WriteVarInt(default));
    }

    [Fact]
    public void SecondFieldSinkFailure_PropagatesAndRetainsFirstFieldPrefix() {
        var failure = new IOException("second field sink failure");
        var sink = new RecordingSink(failure);
        FrameAddress address = FrameAddress.Create(0x89ABCDEF, SizedPtr.Create(4, 28));
        Assert.Same(failure, Assert.Throws<IOException>(() => address.WriteVarInt(new BareValueWriter(sink))));
        Assert.Equal(new[] { 5, 10 }, sink.Hints);
        Assert.Equal(new[] { 5 }, sink.Advances);
        Assert.Equal(new byte[] { 0xEF, 0x9B, 0xAF, 0xCD, 0x08 }, sink.Bytes.ToArray());
    }

    private static void AssertReadFailure<TException>(ref BareValueReader reader) where TException : Exception {
        int beforeConsumed = reader.ConsumedCount;
        int beforeRemaining = reader.RemainingCount;
        Exception? failure = null;
        try { FrameAddress.ReadVarInt(ref reader); }
        catch (Exception error) { failure = error; }
        Assert.IsType<TException>(failure);
        Assert.Equal(beforeConsumed, reader.ConsumedCount);
        Assert.Equal(beforeRemaining, reader.RemainingCount);
    }

    private sealed class RecordingSink(Exception? secondRequestFailure = null) : IBufferWriter<byte> {
        private byte[] _block = [];
        public List<int> Hints { get; } = [];
        public List<int> Advances { get; } = [];
        public List<byte> Bytes { get; } = [];

        public void Advance(int count) {
            Advances.Add(count);
            Bytes.AddRange(_block.AsSpan(0, count).ToArray());
        }

        public Memory<byte> GetMemory(int sizeHint = 0) {
            Hints.Add(sizeHint);
            if (Hints.Count == 2 && secondRequestFailure is not null) { throw secondRequestFailure; }
            _block = new byte[Math.Max(1, sizeHint)];
            return _block;
        }

        public Span<byte> GetSpan(int sizeHint = 0) {
            return GetMemory(sizeHint).Span;
        }
    }
}
