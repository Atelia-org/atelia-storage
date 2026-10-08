using System.Buffers;
using System.IO.Compression;

namespace Atelia.Binary;

public static partial class BareValueEncoding {
    // Initial tuning choices; neither value changes the standard Brotli decoding contract.
    private const int BrotliQuality = 3;
    private const int BrotliWindow = 22;

    /// <summary>Prepares an owned nullable string snapshot and an exact output budget.</summary>
    /// <param name="value">The string, or null.</param>
    /// <param name="compression">The single compression method to try; None is the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The method is unknown, or the complete inner value exceeds int.MaxValue.</exception>
    public static ControlledValueEncodingPlan PrepareControlledString(
        string? value, ValueCompression compression = ValueCompression.None) {
        ValidateCompression(compression);
        if (value is null) { return ControlledValueEncodingPlan.Null; }

        StringEncodingPlan plainPlan = PrepareString(value);
        int decodedByteCount = GetOwnedLength(plainPlan.EncodedLength, nameof(value));
        var sink = new FixedPreparationBuffer(decodedByteCount);
        new BareValueWriter(sink).WriteString(plainPlan);
        return PrepareControlledBody(sink.TakeCompletedBody(), compression);
    }

    /// <summary>Snapshots non-null bytes, including their ordinary Bare length prefix, before trying compression.</summary>
    /// <param name="value">The non-null bytes; an empty span denotes an empty value rather than null.</param>
    /// <param name="compression">The single compression method to try; None is the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The method is unknown, or the complete inner value exceeds int.MaxValue.</exception>
    public static ControlledValueEncodingPlan PrepareControlledBytes(
        ReadOnlySpan<byte> value, ValueCompression compression = ValueCompression.None) {
        ValidateCompression(compression);
        int decodedByteCount = GetOwnedLength(MeasureBytes(value.Length), nameof(value));
        var sink = new FixedPreparationBuffer(decodedByteCount);
        new BareValueWriter(sink).WriteBytes(value);
        return PrepareControlledBody(sink.TakeCompletedBody(), compression);
    }

    private static void ValidateCompression(ValueCompression compression) {
        if (compression is not ValueCompression.None and not ValueCompression.Brotli) {
            throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unknown compression method.");
        }
    }

    private static int GetOwnedLength(long length, string parameterName) {
        if (length > int.MaxValue) {
            throw new ArgumentOutOfRangeException(parameterName, "The complete inner Bare value exceeds the owned preparation limit.");
        }
        return checked((int)length);
    }

    private static ControlledValueEncodingPlan PrepareControlledBody(byte[] body, ValueCompression compression) {
        // A compressed envelope needs control + two length headers + a nonempty stream: at least 4B.
        if (compression == ValueCompression.None || body.Length <= 3) {
            return ControlledValueEncodingPlan.CreateRaw(body);
        }

        // A result requiring U or more bytes cannot beat Raw, so U bounds the candidate without
        // GetMaxCompressedLength's smaller input domain or an unbounded growing output buffer.
        byte[] candidate = new byte[body.Length];
        var encoder = new BrotliEncoder(BrotliQuality, BrotliWindow);
        try {
            int consumed = 0;
            int written = 0;
            while (true) {
                OperationStatus status = encoder.Compress(
                    body.AsSpan(consumed), candidate.AsSpan(written),
                    out int readNow, out int wroteNow, isFinalBlock: true);
                consumed = checked(consumed + readNow);
                written = checked(written + wroteNow);
                if (status == OperationStatus.InvalidData) {
                    throw new InvalidOperationException("The Brotli encoder could not encode the prepared value.");
                }

                long compressedSizeLowerBound = 1L + MeasureVarUInt32((uint)written)
                    + MeasureVarUInt32((uint)body.Length) + written;
                long rawSize = 1L + body.Length;
                if (status == OperationStatus.Done) {
                    if (consumed != body.Length || written == 0) {
                        throw new InvalidOperationException("The Brotli encoder did not finish the complete input.");
                    }
                    return compressedSizeLowerBound < rawSize
                        ? ControlledValueEncodingPlan.CreateBrotli(candidate.AsSpan(0, written).ToArray(), (uint)body.Length)
                        : ControlledValueEncodingPlan.CreateRaw(body);
                }
                if (status != OperationStatus.DestinationTooSmall) {
                    throw new InvalidOperationException("The final Brotli encoding did not complete.");
                }
                // A DestinationTooSmall status alone is not the proof: produced bytes already
                // establish a monotonic complete-size lower bound, even if more output remains.
                if (compressedSizeLowerBound >= rawSize) {
                    return ControlledValueEncodingPlan.CreateRaw(body);
                }
                if (readNow == 0 && wroteNow == 0) {
                    throw new InvalidOperationException("The Brotli encoder made no progress.");
                }
            }
        }
        finally {
            encoder.Dispose();
        }
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
