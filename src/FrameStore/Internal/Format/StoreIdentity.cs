using System.Buffers.Binary;

namespace Atelia.FrameStore.Internal.Format;

/// <summary>独立拥有的 16B opaque 身份值；不依赖 Guid 字节序或借用 buffer。</summary>
internal readonly struct StoreIdentity : IEquatable<StoreIdentity> {
    internal const int EncodedSize = 16;

    private readonly ulong _first;
    private readonly ulong _second;

    private StoreIdentity(ulong first, ulong second) {
        _first = first;
        _second = second;
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, out StoreIdentity identity) {
        identity = default;
        if (source.Length != EncodedSize) { return false; }
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(source);
        ulong second = BinaryPrimitives.ReadUInt64LittleEndian(source[sizeof(ulong)..]);
        if ((first | second) == 0) { return false; }
        identity = new StoreIdentity(first, second);
        return true;
    }

    internal bool TryWrite(Span<byte> destination) {
        if (this == default || destination.Length < EncodedSize) { return false; }
        BinaryPrimitives.WriteUInt64LittleEndian(destination, _first);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(ulong)..], _second);
        return true;
    }

    public bool Equals(StoreIdentity other) {
        return _first == other._first && _second == other._second;
    }

    public override bool Equals(object? obj) {
        return obj is StoreIdentity other && Equals(other);
    }

    public override int GetHashCode() {
        return HashCode.Combine(_first, _second);
    }

    public static bool operator ==(StoreIdentity left, StoreIdentity right) {
        return left.Equals(right);
    }

    public static bool operator !=(StoreIdentity left, StoreIdentity right) {
        return !left.Equals(right);
    }
}
