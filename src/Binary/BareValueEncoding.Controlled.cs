using System.Buffers;

namespace Atelia.Binary;

public static partial class BareValueEncoding {
    /// <summary>Prepares an owned nullable string snapshot and an exact output budget.</summary>
    /// <param name="value">The string, or null.</param>
    /// <param name="compression">The single compression method to try; None is the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The method is unknown, or the complete inner value exceeds int.MaxValue.</exception>
    public static ControlledValueEncodingPlan PrepareControlledString(
        string? value, ValueCompression compression = ValueCompression.None) {
        ControlledValueStorage storage = ControlledValueCodecs.GetStorage(compression);
        if (value is null) { return ControlledValueEncodingPlan.Null; }

        StringEncodingPlan plainPlan = PrepareString(value);
        int decodedByteCount = GetOwnedLength(plainPlan.EncodedLength, nameof(value));
        var sink = new FixedPreparationBuffer(decodedByteCount);
        new BareValueWriter(sink).WriteString(plainPlan);
        return PrepareControlledBody(sink.TakeCompletedBody(), storage);
    }

    /// <summary>Snapshots non-null bytes, including their ordinary Bare length prefix, before trying compression.</summary>
    /// <param name="value">The non-null bytes; an empty span denotes an empty value rather than null.</param>
    /// <param name="compression">The single compression method to try; None is the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The method is unknown, or the complete inner value exceeds int.MaxValue.</exception>
    public static ControlledValueEncodingPlan PrepareControlledBytes(
        ReadOnlySpan<byte> value, ValueCompression compression = ValueCompression.None) {
        ControlledValueStorage storage = ControlledValueCodecs.GetStorage(compression);
        int decodedByteCount = GetOwnedLength(MeasureBytes(value.Length), nameof(value));
        var sink = new FixedPreparationBuffer(decodedByteCount);
        new BareValueWriter(sink).WriteBytes(value);
        return PrepareControlledBody(sink.TakeCompletedBody(), storage);
    }

    private static int GetOwnedLength(long length, string parameterName) {
        if (length > int.MaxValue) {
            throw new ArgumentOutOfRangeException(parameterName, "The complete inner Bare value exceeds the owned preparation limit.");
        }
        return checked((int)length);
    }

    private static ControlledValueEncodingPlan PrepareControlledBody(byte[] body, ControlledValueStorage storage) {
        // A compressed envelope needs control + two length headers + a nonempty stream: at least 4B.
        if (!ControlledValueCodecs.IsCompressed(storage) || body.Length <= 3) {
            return ControlledValueEncodingPlan.CreateRaw(body);
        }

        // A result requiring U or more bytes cannot beat Raw, so U bounds the candidate without
        // GetMaxCompressedLength's smaller input domain or an unbounded growing output buffer.
        byte[] candidate = new byte[body.Length];
        int storedByteCount = ControlledValueCodecs.Compress(storage, body, candidate);
        if (storedByteCount == 0 || !ControlledValueCodecs.IsSmallerThanRaw(storedByteCount, body.Length)) {
            return ControlledValueEncodingPlan.CreateRaw(body);
        }
        return ControlledValueEncodingPlan.CreateCompressed(
            storage, candidate.AsSpan(0, storedByteCount).ToArray(), (uint)body.Length);
    }

    // Exact U storage. Scalar writers may request their maximum width even when the value uses
    // one byte, so a temporary scratch span satisfies that hint without growing/copying the body.
    private sealed class FixedPreparationBuffer(int length) : IBufferWriter<byte> {
        private readonly byte[] _body = new byte[length];
        private byte[]? _scratch;
        private int _position;
        private bool _usingScratch;

        public void Advance(int count) {
            if (count < 0 || count > _body.Length - _position
                || (_usingScratch && count > _scratch!.Length)) {
                throw new InvalidOperationException("The inner writer exceeded its exact prepared budget.");
            }
            if (_usingScratch) { _scratch.AsSpan(0, count).CopyTo(_body.AsSpan(_position)); }
            _position += count;
            _usingScratch = false;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            int requested = Math.Max(sizeHint, 1);
            _usingScratch = requested > _body.Length - _position;
            if (!_usingScratch) { return _body.AsMemory(_position); }
            if (_scratch is null || _scratch.Length < requested) { _scratch = new byte[requested]; }
            return _scratch;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public byte[] TakeCompletedBody() {
            if (_position != _body.Length) {
                throw new InvalidOperationException("The inner writer did not fill its exact prepared budget.");
            }
            return _body;
        }
    }
}
