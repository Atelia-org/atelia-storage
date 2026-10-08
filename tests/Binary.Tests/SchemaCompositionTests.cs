using System.Buffers;
using Xunit;

namespace Atelia.Binary.Tests;

public class SchemaCompositionTests {
    [Fact]
    public void AddressAndKey_RecordHasIndependentGoldenAndExactBudget() {
        // A consumer-owned schema: key, uint FileId, ulong opaque Packed.
        byte[] golden = [0x03, 0x41, 0x78, 0x56, 0x34, 0x12,
            0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01];
        var key = BareValueEncoding.PrepareString("A");
        long budget = checked(key.EncodedLength + 12);
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        writer.WriteString(key);
        writer.WriteUInt32LE(0x12345678);
        writer.WriteUInt64LE(0x0123456789ABCDEF);
        Assert.Equal(golden, sink.WrittenSpan.ToArray());
        Assert.Equal(budget, sink.WrittenCount);

        var reader = new BareValueReader(golden);
        Assert.Equal("A", reader.ReadString());
        Assert.Equal(0x12345678U, reader.ReadUInt32LE());
        Assert.Equal(0x0123456789ABCDEFUL, reader.ReadUInt64LE());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void CopiedReader_ProvidesConsumerOwnedCompositeRollback() {
        byte[] input = [0x03, 0x41, 0x78]; // valid key, truncated FileId
        var original = new BareValueReader(input);
        var speculative = original;
        Assert.Equal("A", speculative.ReadString());
        Exception? failure = null;
        try { speculative.ReadUInt32LE(); }
        catch (Exception error) { failure = error; }
        Assert.IsType<EndOfStreamException>(failure);
        Assert.Equal(2, speculative.ConsumedCount); // only individual Read is atomic
        Assert.Equal(0, original.ConsumedCount); // consumer has not installed the copy
        Assert.Equal("A", original.ReadString());
    }

    [Fact]
    public void NullableScalarComposition_PreservesNegativeZeroAndRejectsCompressedControl() {
        byte[] golden = [0x01, 0x00, 0x00, 0x00, 0x80, 0x00];
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        writer.WriteBoolean(true);
        writer.WriteSingleLE(BitConverter.UInt32BitsToSingle(0x80000000));
        writer.WriteBoolean(false);
        Assert.Equal(golden, sink.WrittenSpan.ToArray());
        var reader = new BareValueReader(golden);
        Assert.True(reader.ReadBoolean());
        Assert.Equal(0x80000000U, BitConverter.SingleToUInt32Bits(reader.ReadSingleLE()));
        Assert.False(reader.ReadBoolean());
        reader.EnsureFullyConsumed();

        var wrongControl = new BareValueReader(new byte[] { 0x02 });
        Exception? failure = null;
        try { wrongControl.ReadBoolean(); }
        catch (Exception error) { failure = error; }
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(0, wrongControl.ConsumedCount);
    }

    [Fact]
    public void BinaryPublicSurface_IsIndependentOfStorageAndHasSixTypes() {
        var assembly = typeof(BareValueEncoding).Assembly;
        string[] expected = ["BareValueEncoding", "BareValueReader", "BareValueWriter",
            "ControlledValueEncodingPlan", "StringEncodingPlan", "ValueCompression"];
        Assert.Equal(expected.Order(), assembly.GetExportedTypes().Select(t => t.Name).Order());
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Atelia.", StringComparison.Ordinal));
        Assert.True(typeof(BareValueReader).IsByRefLike);
        Assert.False(typeof(BareValueWriter).IsByRefLike);
        Assert.Empty(typeof(ControlledValueEncodingPlan).GetConstructors());
        Assert.DoesNotContain(typeof(ControlledValueEncodingPlan).GetProperties(),
            p => p.PropertyType.IsArray || p.PropertyType == typeof(ReadOnlyMemory<byte>));
    }
}
