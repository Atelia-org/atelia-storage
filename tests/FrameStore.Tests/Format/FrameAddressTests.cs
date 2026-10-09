using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Format;

public class FrameAddressTests {
    [Fact]
    public void SpecificationPackedVector_RoundtripsCanonicalTwelveBytes() {
        byte[] wire = Convert.FromHexString("EFCDAB89F0DEBC9A78563412");
        Assert.True(FrameAddress.TryRead(wire, out FrameAddress address));
        Assert.Equal(0x89abcdefu, address.FileId);
        Assert.Equal(0x123456789abcdef0UL, address.Ticket.Packed);
        Assert.Equal(FrameAddress.Create(0x89abcdef, SizedPtr.FromPacked(0x123456789abcdef0UL)), address);
        byte[] destination = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(destination));
        Assert.Equal(wire, destination);
    }

    [Fact]
    public void IndependentVector_UsesLittleEndianFileIdAndFullPackedTicket() {
        // FileId=0x89abcdef, offset=0x123456789c, length=0x02345678.
        byte[] wire = Convert.FromHexString("EFCDAB899E158D9C78563412");
        Assert.True(FrameAddress.TryRead(wire, out FrameAddress address));
        Assert.Equal(0x89abcdefu, address.FileId);
        Assert.Equal(0x123456789cL, address.Ticket.Offset);
        Assert.Equal(0x02345678, address.Ticket.Length);
        Assert.Equal(0x123456789c8d159eUL, address.Ticket.Packed);
        Assert.NotEqual(address.Ticket.Serialize(), address.Ticket.Packed);
        byte[] destination = new byte[FrameAddress.EncodedSize + 3];
        Array.Fill(destination, (byte)0xA5);
        Assert.True(address.TryWrite(destination));
        Assert.Equal(wire, destination[..FrameAddress.EncodedSize]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[FrameAddress.EncodedSize..]);
    }

    [Fact]
    public void NumericLowerBounds_AllowHeaderCoordinatesAndMinimumFrame() {
        byte[] minimum = Convert.FromHexString("010000000700000400000000");
        Assert.True(FrameAddress.TryRead(minimum, out FrameAddress address));
        Assert.Equal(RbfScanBoundary.Empty.EndExclusive, address.Ticket.Offset);
        Assert.Equal(RbfFile.MeasureWriteSize(0, 0).Value.FrameLength, address.Ticket.Length);
        Assert.True(FrameAddress.TryRead(Convert.FromHexString("010000000D00000400000000"), out FrameAddress header));
        Assert.Equal(RbfScanBoundary.Empty.EndExclusive, header.Ticket.Offset);
        Assert.Equal(RbfFile.MeasureWriteSize(24, 0).Value.FrameLength, header.Ticket.Length);
    }

    [Fact]
    public void FullRepresentableTicketRange_AllowsEndBeyondMaxOffset() {
        byte[] wire = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFF");
        Assert.True(FrameAddress.TryRead(wire, out FrameAddress address));
        Assert.Equal(uint.MaxValue, address.FileId);
        Assert.Equal(SizedPtr.MaxOffset, address.Ticket.Offset);
        Assert.Equal(SizedPtr.MaxLength, address.Ticket.Length);
        Assert.True(address.Ticket.EndOffsetExclusive > SizedPtr.MaxOffset);
        byte[] encoded = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(encoded));
        Assert.Equal(wire, encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0100000007000004000000")]
    [InlineData("01000000070000040000000000")]
    [InlineData("000000000700000400000000")]
    [InlineData("010000000700000000000000")]
    [InlineData("010000000000000400000000")]
    [InlineData("010000000600000400000000")]
    public void RejectedDecode_ResetsOutToDefault(string hex) {
        FrameAddress address = FrameAddress.Create(1, SizedPtr.Create(4, 28));
        Assert.False(FrameAddress.TryRead(Convert.FromHexString(hex), out address));
        Assert.Equal(default, address);
    }

    [Fact]
    public void RejectedWrite_PreservesEntireDestination() {
        Assert.True(FrameAddress.TryRead(Convert.FromHexString("010000000700000400000000"), out FrameAddress address));
        for (int length = 0; length < FrameAddress.EncodedSize; length++) {
            byte[] destination = Enumerable.Repeat((byte)0xCD, length).ToArray();
            byte[] original = destination.ToArray();
            Assert.False(address.TryWrite(destination));
            Assert.Equal(original, destination);
        }

        byte[] large = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        byte[] before = large.ToArray();
        Assert.False(default(FrameAddress).TryWrite(large));
        Assert.Equal(before, large);
    }

    [Fact]
    public void ValueEquality_UsesAllFileIdAndPackedBitsAndAcceptsDefault() {
        byte[] wire = Convert.FromHexString("EFCDAB899E158D9C78563412");
        Assert.True(FrameAddress.TryRead(wire, out FrameAddress original));
        Assert.True(FrameAddress.TryRead(wire.ToArray(), out FrameAddress same));
        Assert.True(original == same);
        Assert.False(original != same);
        Assert.True(original.Equals((object)same));
        Assert.Equal(original.GetHashCode(), same.GetHashCode());
        Assert.False(original.Equals(null));
        Assert.False(original.Equals(1));
        foreach (int changedByte in new[] { 0, 4, 11 }) {
            byte[] differentWire = wire.ToArray();
            differentWire[changedByte] ^= 1;
            Assert.True(FrameAddress.TryRead(differentWire, out FrameAddress different));
            Assert.True(original != different);
        }

        FrameAddress empty = default;
        Assert.True(empty.Equals(default(FrameAddress)));
        Assert.Equal(empty.GetHashCode(), default(FrameAddress).GetHashCode());
        Assert.False(empty == original);
    }

    [Fact]
    public void PublicSurface_ProvidesCodecAndEqualityWithoutNumericProjection() {
        Type type = typeof(FrameAddress);
        Assert.Empty(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(type.GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.Null(type.GetMethod("Deconstruct", BindingFlags.Instance | BindingFlags.Public));
        Assert.False(typeof(IComparable<FrameAddress>).IsAssignableFrom(type));
        Assert.Equal(12, FrameAddress.EncodedSize);
    }
}
