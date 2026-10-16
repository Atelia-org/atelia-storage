using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Format;

public class HeadHeaderTests {
    [Theory]
    [InlineData(false, FormatTestValues.HeaderHex)]
    [InlineData(true, FormatTestValues.ForkHeaderHex)]
    public void Header_IndependentGoldensPreserveContextAndNullableOrigin(bool hasOrigin, string hex) {
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId own = HeadId.Create(identity, 0x89ABCDEF);
        byte[] golden = Convert.FromHexString(hex);
        Assert.True(HeadHeaderCodec.TryRead(golden, identity, own, out HeadHeader header));
        Assert.Equal(own, header.HeadId);
        Assert.Equal(hasOrigin, header.Origin.HasValue);
        ForkOrigin? expectedOrigin = hasOrigin
            ? new ForkOrigin(HeadId.Create(identity, 0x76543210), SizedPtr.Create(60, 40))
            : null;
        Assert.Equal(expectedOrigin, header.Origin);
        byte[] destination = Enumerable.Repeat((byte)0xA5, golden.Length + 3).ToArray();
        Assert.True(HeadHeaderCodec.TryWrite(identity, own, expectedOrigin, destination));
        Assert.Equal(golden, destination[..golden.Length]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[golden.Length..]);
        if (header.Origin is ForkOrigin source) {
            Assert.Equal(60, source.SourceEntryTicket.Offset);
            Assert.Equal(40, source.SourceEntryTicket.Length);
            Assert.Equal(0x3C00000AUL, source.SourceEntryTicket.Packed);
            Assert.NotEqual(source.SourceEntryTicket.Serialize(), source.SourceEntryTicket.Packed);
        }
    }

    [Fact]
    public void Header_RejectsEveryOtherWidthAndKeepsResultDefault() {
        byte[] golden = Convert.FromHexString(FormatTestValues.ForkHeaderHex);
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId own = HeadId.Create(identity, 0x89ABCDEF);
        for (int length = 0; length <= 40; length++) {
            if (length is 24 or 36) { continue; }
            byte[] source = new byte[length];
            golden.AsSpan(0, Math.Min(length, golden.Length)).CopyTo(source);
            HeadHeader output = new(own, null);
            Assert.False(HeadHeaderCodec.TryRead(source, identity, own, out output));
            Assert.Equal(default, output);
        }

        // This is another complete payload shape, not proof that an actual 36B RBF frame may be truncated.
        Assert.True(HeadHeaderCodec.TryRead(golden.AsSpan(0, 24), identity, own, out HeadHeader noOrigin));
        Assert.Null(noOrigin.Origin);
        Assert.False(HeadHeaderCodec.TryRead(Convert.FromHexString(FormatTestValues.GateHex), identity, own, out _));
    }

    [Fact]
    public void Header_RequiresExpectedIdentityAndExactPathHeadRatherThanClaimingThemFromPayload() {
        byte[] golden = Convert.FromHexString(FormatTestValues.HeaderHex);
        StoreIdentity identity = FormatTestValues.Identity();
        StoreIdentity foreign = FormatTestValues.DataIdentity();
        HeadId own = HeadId.Create(identity, 0x89ABCDEF);
        Assert.False(HeadHeaderCodec.TryRead(golden, default, own, out _));
        Assert.False(HeadHeaderCodec.TryRead(golden, identity, default, out _));
        Assert.False(HeadHeaderCodec.TryRead(golden, foreign, own, out _));
        Assert.False(HeadHeaderCodec.TryRead(golden, foreign, HeadId.Create(foreign, own.LocalId), out _));
        Assert.False(HeadHeaderCodec.TryRead(golden, identity, HeadId.Create(identity, own.LocalId - 1), out _));
        Assert.False(HeadHeaderCodec.TryRead(golden, identity, HeadId.Create(foreign, own.LocalId), out _));
    }

    [Theory]
    [InlineData(0)] // Unknown version.
    [InlineData(1)] // Zero VSID.
    [InlineData(2)] // Different VSID.
    [InlineData(3)] // Own local zero.
    [InlineData(4)] // Source local zero.
    [InlineData(5)] // Self-origin.
    [InlineData(6)] // Zero ticket.
    public void Header_InvalidFieldsCannotClaimMetadata(int mutation) {
        byte[] changed = Convert.FromHexString(FormatTestValues.ForkHeaderHex);
        switch (mutation) {
            case 0: changed[0] = 1; break;
            case 1: changed.AsSpan(4, 16).Clear(); break;
            case 2: changed[19] ^= 1; break;
            case 3: changed.AsSpan(20, 4).Clear(); break;
            case 4: changed.AsSpan(24, 4).Clear(); break;
            case 5: changed.AsSpan(20, 4).CopyTo(changed.AsSpan(24, 4)); break;
            case 6: changed.AsSpan(28, 8).Clear(); break;
        }

        StoreIdentity identity = FormatTestValues.Identity();
        Assert.False(HeadHeaderCodec.TryRead(changed, identity, HeadId.Create(identity, 0x89ABCDEF), out HeadHeader header));
        Assert.Equal(default, header);
    }

    [Theory]
    [InlineData(56L, 40)] // Before even the smallest source header's physical successor.
    [InlineData(60L, 36)] // Below smallest meta8 Entry.
    public void Header_SourceTicketRejectsCoordinatesBelowAnyPublication(long offset, int length) {
        byte[] changed = Convert.FromHexString(FormatTestValues.ForkHeaderHex);
        BinaryPrimitives.WriteUInt64LittleEndian(changed.AsSpan(28), SizedPtr.Create(offset, length).Packed);
        StoreIdentity identity = FormatTestValues.Identity();
        Assert.False(HeadHeaderCodec.TryRead(changed, identity, HeadId.Create(identity, 0x89ABCDEF), out HeadHeader header));
        Assert.Equal(default, header);
    }

    [Fact]
    public void Header_LastLegalSourceStartCanHaveEndBeyondMaxOffset() {
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId own = HeadId.Create(identity, 7);
        ForkOrigin origin = new(HeadId.Create(identity, uint.MaxValue), SizedPtr.Create(SizedPtr.MaxOffset, 40));
        byte[] wire = new byte[36];
        Assert.True(HeadHeaderCodec.TryWrite(identity, own, origin, wire));
        Assert.True(HeadHeaderCodec.TryRead(wire, identity, own, out HeadHeader header));
        Assert.Equal(origin, header.Origin);
        Assert.True(origin.SourceEntryTicket.EndOffsetExclusive > SizedPtr.MaxOffset);
    }

    [Fact]
    public void SourceBounds_AreDerivedFromPublicRbfSizes() {
        Assert.Equal(60, RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(24, 0).Value.AppendLength);
        Assert.Equal(72, RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(36, 0).Value.AppendLength);
        Assert.Equal(40, RbfFile.MeasureWriteSize(1, 8).Value.FrameLength);
        Assert.Equal(1048612, RbfFile.MeasureWriteSize(HeadStore.MaxHeadEntryPayloadLength, 8).Value.FrameLength);
        Assert.Equal(SizedPtr.MaxLength, RbfFile.MeasureWriteSize(RbfFile.MaxPayloadAndMetaLength - 8, 8).Value.FrameLength);
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId own = HeadId.Create(identity, 7);
        // The header declares coordinates only. Exact reads separately establish
        // Entry or committed CU membership, codec, and root-payload bounds.
        foreach (int length in new[] { 40, 1048612, 1048616, SizedPtr.MaxLength }) {
            ForkOrigin origin = new(HeadId.Create(identity, 8), SizedPtr.Create(60, length));
            byte[] wire = new byte[36];
            Assert.True(HeadHeaderCodec.TryWrite(identity, own, origin, wire));
            Assert.True(HeadHeaderCodec.TryRead(wire, identity, own, out _));
        }
    }

    [Fact]
    public void RejectedHeaderWrite_DoesNotPartiallyModifyDestination() {
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId own = HeadId.Create(identity, 7);
        SizedPtr legal = SizedPtr.Create(60, 40);
        ForkOrigin[] invalidOrigins = [
            new(default, legal), new(own, legal),
            new(HeadId.Create(FormatTestValues.DataIdentity(), 8), legal),
            new(HeadId.Create(identity, 8), default),
            new(HeadId.Create(identity, 8), SizedPtr.Create(56, 40)),
            new(HeadId.Create(identity, 8), SizedPtr.Create(60, 36))
        ];
        foreach (ForkOrigin origin in invalidOrigins) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, 40).ToArray();
            Assert.False(HeadHeaderCodec.TryWrite(identity, own, origin, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }

        foreach (ForkOrigin? origin in new ForkOrigin?[] { null, new(HeadId.Create(identity, 8), legal) }) {
            int requiredLength = origin.HasValue ? 36 : 24;
            for (int length = 0; length < requiredLength; length++) {
                byte[] destination = Enumerable.Repeat((byte)0xA5, length).ToArray();
                Assert.False(HeadHeaderCodec.TryWrite(identity, own, origin, destination));
                Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
            }

            byte[] defaultDestination = Enumerable.Repeat((byte)0xA5, 40).ToArray();
            Assert.False(HeadHeaderCodec.TryWrite(identity, default, origin, defaultDestination));
            Assert.False(HeadHeaderCodec.TryWrite(default, own, origin, defaultDestination));
            Assert.False(HeadHeaderCodec.TryWrite(identity, HeadId.Create(FormatTestValues.DataIdentity(), 7), origin, defaultDestination));
            Assert.All(defaultDestination, value => Assert.Equal((byte)0xA5, value));
        }
    }
}
