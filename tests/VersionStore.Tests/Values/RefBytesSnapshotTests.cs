using System.Buffers;
using System.Reflection;
using Atelia.Data;
using Atelia.VersionStore.Tests.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.Values;

public class RefBytesSnapshotTests {
    [Fact]
    public void Snapshot_RetainsTransferredGcBytesAfterBorrowedBufferReturnAndLaterResults() {
        byte[] pooled = ArrayPool<byte>.Shared.Rent(64);
        RefBytesSnapshot first;
        byte[] expected = "{\"root\":\"independent\"}"u8.ToArray();
        try {
            expected.CopyTo(pooled, 3);
            // Models the caller's required exact copy before pooled frame return; the ctor consumes only this owned array.
            byte[] owned = pooled.AsSpan(3, expected.Length).ToArray();
            first = new RefBytesSnapshot(Revision(7, 60), long.MinValue, owned);
        }
        finally {
            pooled.AsSpan().Fill(0xFF);
            ArrayPool<byte>.Shared.Return(pooled, clearArray: true);
        }

        RefBytesSnapshot second = new(Revision(7, 104), long.MaxValue, "later"u8.ToArray());
        Assert.Equal(expected, first.Payload.ToArray());
        Assert.Equal("later"u8.ToArray(), second.Payload.ToArray());
        Assert.Equal(long.MinValue, first.RecordedAtUnixMs);
        Assert.Equal(long.MaxValue, second.RecordedAtUnixMs);
        Assert.NotEqual(first.Revision, second.Revision);
    }

    [Fact]
    public void Snapshot_CapturesOnlyTheOwnedSliceAndDoesNotInterpretOpaqueContent() {
        byte[] backing = [0xAA, 0xBB, 0x00, 0xFF, 0x81, 0xCC];
        RefBytesSnapshot snapshot = new(Revision(7, 60), -1, backing.AsSpan(2, 3).ToArray());
        Array.Clear(backing);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x81 }, snapshot.Payload.ToArray());
        Assert.Equal(-1, snapshot.RecordedAtUnixMs);
        Assert.Equal(Revision(7, 60), snapshot.Revision);
    }

    [Fact]
    public void Snapshot_HasReferenceEqualityAndAReadonlySpanOnlyPayloadSurface() {
        RefRevision revision = Revision(7, 60);
        RefBytesSnapshot first = new(revision, 0, [1]);
        RefBytesSnapshot second = new(revision, 0, [1]);
        Assert.NotEqual(first, second);
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(RefBytesSnapshot)));
        Assert.True(typeof(RefBytesSnapshot).IsSealed);
        Assert.Empty(typeof(RefBytesSnapshot).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(RefBytesSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = typeof(RefBytesSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "Payload", "RecordedAtUnixMs", "Revision" }, properties.Select(property => property.Name).OrderBy(name => name));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(ReadOnlySpan<byte>), typeof(RefBytesSnapshot).GetProperty("Payload")!.PropertyType);
        Assert.Null(typeof(RefBytesSnapshot).GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Null(typeof(RefBytesSnapshot).GetMethod("GetHashCode", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    private static RefRevision Revision(uint localId, long offset) {
        return RefRevision.Create(RefId.Create(FormatTestValues.Identity(), localId), SizedPtr.Create(offset, 40));
    }
}
