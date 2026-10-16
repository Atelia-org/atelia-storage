using System.Buffers;
using System.Reflection;
using Atelia.Data;
using Atelia.HeadStore.Tests.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Values;

public class HeadEntryTests {
    [Fact]
    public void Entry_RetainsTransferredGcBytesAfterBorrowedBufferReturnAndLaterResults() {
        byte[] pooled = ArrayPool<byte>.Shared.Rent(64);
        HeadEntry first;
        byte[] expected = "{\"root\":\"independent\"}"u8.ToArray();
        try {
            expected.CopyTo(pooled, 3);
            // Models the caller's required exact copy before pooled frame return; the ctor consumes only this owned array.
            byte[] owned = pooled.AsSpan(3, expected.Length).ToArray();
            first = new HeadEntry(Id(7, 60), long.MinValue, owned);
        }
        finally {
            pooled.AsSpan().Fill(0xFF);
            ArrayPool<byte>.Shared.Return(pooled, clearArray: true);
        }

        HeadEntry second = new(Id(7, 104), long.MaxValue, "later"u8.ToArray());
        Assert.Equal(expected, first.Payload.ToArray());
        Assert.Equal("later"u8.ToArray(), second.Payload.ToArray());
        Assert.Equal(long.MinValue, first.RecordedAtUnixMs);
        Assert.Equal(long.MaxValue, second.RecordedAtUnixMs);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Entry_CapturesOnlyTheOwnedSliceAndDoesNotInterpretOpaqueContent() {
        byte[] backing = [0xAA, 0xBB, 0x00, 0xFF, 0x81, 0xCC];
        HeadEntry entry = new(Id(7, 60), -1, backing.AsSpan(2, 3).ToArray());
        Array.Clear(backing);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x81 }, entry.Payload.ToArray());
        Assert.Equal(-1, entry.RecordedAtUnixMs);
        Assert.Equal(Id(7, 60), entry.Id);
    }

    [Fact]
    public void Entry_HasReferenceEqualityAndAReadonlySpanOnlyPayloadSurface() {
        HeadEntryId entryId = Id(7, 60);
        HeadEntry first = new(entryId, 0, [1]);
        HeadEntry second = new(entryId, 0, [1]);
        Assert.NotEqual(first, second);
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(HeadEntry)));
        Assert.True(typeof(HeadEntry).IsSealed);
        Assert.Empty(typeof(HeadEntry).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(HeadEntry).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = typeof(HeadEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "Id", "Payload", "RecordedAtUnixMs" }, properties.Select(property => property.Name).OrderBy(name => name));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(ReadOnlySpan<byte>), typeof(HeadEntry).GetProperty("Payload")!.PropertyType);
        Assert.Null(typeof(HeadEntry).GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Null(typeof(HeadEntry).GetMethod("GetHashCode", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    private static HeadEntryId Id(uint localId, long offset) {
        return HeadEntryId.Create(HeadId.Create(FormatTestValues.Identity(), localId), SizedPtr.Create(offset, 40));
    }
}
