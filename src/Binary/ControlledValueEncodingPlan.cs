namespace Atelia.Binary;

/// <summary>An immutable owned snapshot of a nullable, optionally compressed Bare value.</summary>
/// <remarks>The plan does not identify the inner schema type or confer publication or durability qualification.</remarks>
public sealed class ControlledValueEncodingPlan {
    private readonly byte _control;
    private readonly byte[] _storedBody;
    private readonly uint _decodedByteCount;

    private ControlledValueEncodingPlan(byte control, byte[] storedBody, uint decodedByteCount) {
        _control = control;
        _storedBody = storedBody;
        _decodedByteCount = decodedByteCount;
    }

    /// <summary>Gets the shared plan that writes the single null control byte.</summary>
    public static ControlledValueEncodingPlan Null { get; } = new(0, [], 0);

    /// <summary>Gets the exact number of bytes emitted by <see cref="BareValueWriter.WritePreparedValue"/>.</summary>
    public long EncodedLength => _control == 2
        ? 1L + BareValueEncoding.MeasureVarUInt32((uint)_storedBody.Length)
            + BareValueEncoding.MeasureVarUInt32(_decodedByteCount) + _storedBody.Length
        : 1L + _storedBody.Length;

    // These factories receive newly owned buffers; neither the factories nor WriteTo expose them publicly.
    internal static ControlledValueEncodingPlan CreateRaw(byte[] body) => new(1, body, 0);

    internal static ControlledValueEncodingPlan CreateBrotli(byte[] body, uint decodedByteCount)
        => new(2, body, decodedByteCount);

    internal void WriteTo(in BareValueWriter writer) {
        writer.WriteByte(_control);
        if (_control == 2) {
            writer.WriteVarUInt32((uint)_storedBody.Length);
            writer.WriteVarUInt32(_decodedByteCount);
        }
        writer.WriteRawBytes(_storedBody);
    }
}
