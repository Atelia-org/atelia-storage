using System.Buffers.Binary;
using Atelia.Data;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Tests.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Values;

public class HandleCodecTests {
    [Fact]
    public void HeadId_GoldenWritesEncodedHandleRatherThanRawLocal() {
        // key=0x04030201, local=7 => encoded=0x04030206.
        HeadId value = HeadId.Create(FormatTestValues.Identity(), 7);
        byte[] expected = [0x06, 0x02, 0x03, 0x04];
        byte[] actual = new byte[4];
        Assert.True(value.TryWrite(actual));
        Assert.Equal(expected, actual);
        Assert.True(HeadId.TryRead(expected, out HeadId decoded));
        Assert.Equal(value, decoded);
        Assert.Equal(value.GetHashCode(), decoded.GetHashCode());
    }

    [Theory]
    [InlineData("01000000")]
    [InlineData("FFFFFFFF")]
    [InlineData("00000080")]
    public void HeadId_AnyNonzeroEncodedValueIsStructurallyAccepted(string hex) {
        byte[] bytes = Convert.FromHexString(hex);
        Assert.True(HeadId.TryRead(bytes, out HeadId value));
        byte[] encoded = new byte[4];
        Assert.True(value.TryWrite(encoded));
        Assert.Equal(bytes, encoded);
    }

    [Fact]
    public void HeadEntryId_GoldenPinsLittleEndianPackedInsteadOfInterleavedSerialization() {
        // head=0x12345678; offset=60, length=40 => (15 << 26) | 10 = 0x3c00000a.
        byte[] golden = [0x78, 0x56, 0x34, 0x12, 0x0A, 0x00, 0x00, 0x3C, 0x00, 0x00, 0x00, 0x00];
        HeadId head = ReadHead("78563412");
        HeadEntryId original = HeadEntryId.Create(head, SizedPtr.Create(60, 40));
        byte[] encoded = new byte[12];
        Assert.True(original.TryWrite(encoded));
        Assert.Equal(golden, encoded);
        Assert.NotEqual(0x3C00000AUL, original.Ticket.Serialize());
        Assert.True(HeadEntryId.TryRead(golden, out HeadEntryId decoded));
        Assert.Equal(head, decoded.HeadId);
        Assert.Equal(60, decoded.Ticket.Offset);
        Assert.Equal(40, decoded.Ticket.Length);
        Assert.Equal(0x3C00000AUL, decoded.Ticket.Packed);
        Assert.Equal(original, decoded);
        Assert.Equal(original.GetHashCode(), decoded.GetHashCode());
    }

    [Theory]
    [InlineData("010000000100000000000000", 0L, 4)]
    [InlineData("01000000FFFFFF0300000000", 0L, 268435452)]
    [InlineData("01000000010000FCFFFFFFFF", 1099511627772L, 4)]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFF", 1099511627772L, 268435452)]
    public void HeadEntryId_GoldenBoundaryValuesPinV1UnitsAndBitPartition(string hex, long offset, int length) {
        byte[] golden = Convert.FromHexString(hex);
        Assert.True(HeadEntryId.TryRead(golden, out HeadEntryId decoded));
        Assert.Equal(offset, decoded.Ticket.Offset);
        Assert.Equal(length, decoded.Ticket.Length);
        HeadEntryId fromCoordinates = HeadEntryId.Create(decoded.HeadId, SizedPtr.Create(offset, length));
        byte[] encoded = new byte[12];
        Assert.True(fromCoordinates.TryWrite(encoded));
        Assert.Equal(golden, encoded);
        Assert.Equal(fromCoordinates, decoded);
    }

    [Fact]
    public void HeadEntryId_EachTicketBitHasTheFrozenV1CoordinateMeaning() {
        HeadId head = ReadHead("78563412");
        for (int bit = 0; bit < 64; bit++) {
            // A single offset bit still needs a positive length; length bits stand alone.
            ulong packed = (1UL << bit) | (bit >= 26 ? 1UL : 0UL);
            long offset = bit >= 26 ? 4L << (bit - 26) : 0;
            int length = bit < 26 ? 4 << bit : 4;
            byte[] bytes = new byte[12];
            Assert.True(head.TryWrite(bytes.AsSpan(0, 4)));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), packed);
            Assert.True(HeadEntryId.TryRead(bytes, out HeadEntryId value));
            Assert.Equal(offset, value.Ticket.Offset);
            Assert.Equal(length, value.Ticket.Length);
            HeadEntryId fromCoordinates = HeadEntryId.Create(head, SizedPtr.Create(offset, length));
            byte[] encoded = new byte[12];
            Assert.True(fromCoordinates.TryWrite(encoded));
            Assert.Equal(bytes, encoded);
        }
    }

    [Fact]
    public void TryRead_RequiresExactLengthsAndResetsOutputOnEveryFailure() {
        for (int length = 0; length <= 25; length++) {
            byte[] bytes = Enumerable.Repeat((byte)0xFF, length).ToArray();
            HeadId head = ReadHead("01000000");
            HeadEntryId entry = HeadEntryId.Create(head, SizedPtr.Create(60, 40));
            Assert.Equal(length == 4, HeadId.TryRead(bytes, out head));
            Assert.Equal(length == 12, HeadEntryId.TryRead(bytes, out entry));
            if (length != 4) { Assert.Equal(default, head); }
            if (length != 12) { Assert.Equal(default, entry); }
        }
    }

    [Theory]
    [InlineData("000000000100000000000000")]
    [InlineData("010000000000000000000000")]
    [InlineData("01000000000000FCFFFFFFFF")]
    [InlineData("000000000000000000000000")]
    public void TryRead_RejectsZeroHeadOrZeroLengthEvenAtMaximumOffset(string hex) {
        HeadEntryId entry = HeadEntryId.Create(ReadHead("01000000"), SizedPtr.Create(60, 40));
        Assert.False(HeadEntryId.TryRead(Convert.FromHexString(hex), out entry));
        Assert.Equal(default, entry);
        HeadId head = ReadHead("01000000");
        Assert.False(HeadId.TryRead(new byte[4], out head));
        Assert.Equal(default, head);
    }

    [Fact]
    public void TryWrite_RejectsShortTargetsWithoutChangingAnyByte() {
        HeadId head = ReadHead("78563412");
        HeadEntryId entry = HeadEntryId.Create(head, SizedPtr.Create(60, 40));
        for (int length = 0; length < 12; length++) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, 20).ToArray();
            byte[] before = (byte[])destination.Clone();
            Assert.False(entry.TryWrite(destination.AsSpan(2, length)));
            Assert.Equal(before, destination);
            if (length < 4) {
                Assert.False(head.TryWrite(destination.AsSpan(2, length)));
                Assert.Equal(before, destination);
            }
        }
    }

    [Fact]
    public void TryWrite_RejectsAllInvalidValueShapesBeforeWriting() {
        HeadId head = ReadHead("78563412");
        HeadEntryId[] invalid = [default, HeadEntryId.Create(default, SizedPtr.Create(60, 40)),
            HeadEntryId.Create(head, default), HeadEntryId.Create(head, SizedPtr.Create(SizedPtr.MaxOffset, 0))];
        foreach (HeadEntryId value in invalid) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, 20).ToArray();
            byte[] before = (byte[])destination.Clone();
            Assert.False(value.TryWrite(destination));
            Assert.Equal(before, destination);
            Assert.False(default(HeadId).TryWrite(destination));
            Assert.Equal(before, destination);
        }
    }

    [Fact]
    public void TryWrite_WritesOnlyItsFieldWithoutPaddingOrSuffixChanges() {
        HeadId head = ReadHead("78563412");
        HeadEntryId entry = HeadEntryId.Create(head, SizedPtr.Create(60, 40));
        byte[] destination = Enumerable.Repeat((byte)0xA5, 24).ToArray();
        Assert.True(head.TryWrite(destination.AsSpan(2)));
        Assert.Equal(Convert.FromHexString("A5A578563412"), destination[..6]);
        Assert.All(destination[6..], value => Assert.Equal((byte)0xA5, value));
        destination.AsSpan().Fill(0xA5);
        Assert.True(entry.TryWrite(destination.AsSpan(2)));
        Assert.Equal(Convert.FromHexString("A5A5785634120A00003C00000000"), destination[..14]);
        Assert.All(destination[14..], value => Assert.Equal((byte)0xA5, value));
    }

    [Fact]
    public void ImportedHandlesRetainProbabilisticOwnerContextRatherThanFullIdentity() {
        StoreIdentity owner = FormatTestValues.Identity();
        HeadId original = HeadId.Create(owner, 7);
        byte[] bytes = new byte[4];
        Assert.True(original.TryWrite(bytes));
        Assert.True(HeadId.TryRead(bytes, out HeadId imported));
        StoreIdentity sameKey = FormatTestValues.ReadIdentity("0102030405060708090A0B0C0D0E0F11");
        Assert.NotEqual(owner, sameKey);
        Assert.True(imported.TryGetLocalId(sameKey, out uint local));
        Assert.Equal(7u, local);
        StoreIdentity sameHighByte = FormatTestValues.ReadIdentity("0002030405060708090A0B0C0D0E0F10");
        Assert.True(imported.TryGetLocalId(sameHighByte, out local));
        Assert.Equal(6u, local);
        StoreIdentity differentHighByte = FormatTestValues.ReadIdentity("0102038405060708090A0B0C0D0E0F10");
        Assert.False(imported.TryGetLocalId(differentHighByte, out local));
        Assert.Equal(0u, local);
    }

    private static HeadId ReadHead(string hex) {
        Assert.True(HeadId.TryRead(Convert.FromHexString(hex), out HeadId value));
        return value;
    }
}
