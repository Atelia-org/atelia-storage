namespace Atelia.Binary;

/// <summary>An immutable owned snapshot of a nullable, optionally compressed Bare value.</summary>
/// <remarks>The plan does not identify the inner schema type or confer publication or durability qualification.</remarks>
public sealed class ControlledValueEncodingPlan {
    private readonly ControlledValueStorage _storage;
    private readonly byte[] _storedBody;
    private readonly uint _decodedByteCount;

    private ControlledValueEncodingPlan(ControlledValueStorage storage, byte[] storedBody, uint decodedByteCount) {
        _storage = storage;
        _storedBody = storedBody;
        _decodedByteCount = decodedByteCount;
    }

    /// <summary>Gets the shared plan that writes the single null control byte.</summary>
    public static ControlledValueEncodingPlan Null { get; } = new(ControlledValueStorage.Null, [], 0);

    /// <summary>Gets the exact number of bytes emitted by <see cref="BareValueWriter.WritePreparedValue"/>.</summary>
    public long EncodedLength => ControlledValueCodecs.IsCompressed(_storage)
        ? ControlledValueCodecs.GetCompressedEncodedLength((uint)_storedBody.Length, _decodedByteCount)
        : 1L + _storedBody.Length;

    // These factories receive newly owned buffers; neither the factories nor WriteTo expose them publicly.
    internal static ControlledValueEncodingPlan CreateRaw(byte[] body) => new(ControlledValueStorage.Raw, body, 0);

    internal static ControlledValueEncodingPlan CreateCompressed(
        ControlledValueStorage storage, byte[] body, uint decodedByteCount) {
        System.Diagnostics.Debug.Assert(ControlledValueCodecs.IsCompressed(storage));
        System.Diagnostics.Debug.Assert(body.Length > 0 && decodedByteCount > 0);
        return new(storage, body, decodedByteCount);
    }

    internal void WriteTo(in BareValueWriter writer) {
        writer.WriteByte((byte)_storage);
        if (ControlledValueCodecs.IsCompressed(_storage)) {
            writer.WriteVarUInt32((uint)_storedBody.Length);
            writer.WriteVarUInt32(_decodedByteCount);
        }
        writer.WriteRawBytes(_storedBody);
    }
}
