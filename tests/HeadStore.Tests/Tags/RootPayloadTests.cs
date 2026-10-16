using System.Buffers;
using System.Reflection;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class RootPayloadTests {
    [Fact]
    public void OwnedContainerRetainsOnlyCapturedSliceAfterInputPoolReturn() {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        RootPayload payload;
        byte[] expected = [0, 0xff, 0x81];
        try {
            expected.CopyTo(rented, 7);
            payload = new RootPayload(rented.AsSpan(7, expected.Length).ToArray());
        }
        finally {
            Array.Fill(rented, (byte)0xa5);
            ArrayPool<byte>.Shared.Return(rented);
        }
        Assert.Equal(3, payload.Length);
        Assert.Equal(expected, payload.Bytes.ToArray());
        RootPayload another = new([0]);
        Assert.NotEqual(payload, another);
        Assert.Equal(expected, payload.Bytes.ToArray());
    }

    [Fact]
    public void PublicSurfaceHasOnlyLengthAndReadonlyBytesWithNoLeaseOrIdentity() {
        Type type = typeof(RootPayload);
        Assert.True(type.IsSealed);
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "Bytes", "Length" }, properties.Select(property => property.Name).OrderBy(name => name));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(ReadOnlySpan<byte>), type.GetProperty("Bytes")!.PropertyType);
        Assert.Null(type.GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Null(type.GetMethod("GetHashCode", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }
}
