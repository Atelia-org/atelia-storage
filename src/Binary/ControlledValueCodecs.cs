using System.Buffers;
using System.IO.Compression;

namespace Atelia.Binary;

// Shared framing/size rules and explicit codec dispatch; no public registry or codec lifetime.
// TODO (.NET 11 GA): add BCL ZstandardEncoder/Decoder and DeflateEncoder/Decoder here after
// qualifying independent fixtures. Deflate means raw RFC 1951; an optional ZLib method means
// RFC 1950 and needs a distinct wire control. Preserve existing public enum values/control bytes.
// Each compressed method is a self-contained single stream/block, with no external dictionary/prefix.
// Verify each instance API's final status and actual counts; do not mechanically copy Brotli's
// state handling or use Stream.Position/static TryDecompress as a single-stream boundary proof.
// Bound Zstandard's decoder window separately from stored/decoded body limits before shipping.
internal static partial class ControlledValueCodecs {
    // Initial tuning choices; neither value changes the standard Brotli decoding contract.
    private const int BrotliQuality = 3;
    private const int BrotliWindow = 22;

    internal static ControlledValueStorage GetStorage(ValueCompression compression) => compression switch {
        ValueCompression.None => ControlledValueStorage.Raw,
        ValueCompression.Brotli => ControlledValueStorage.Brotli,
        ValueCompression.Lz4Block => ControlledValueStorage.Lz4Block,
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unknown compression method.")
    };

    // Reject unknown controls before attempting to parse any lengths or allocate a body.
    internal static ControlledValueStorage ReadStorage(byte control) => control switch {
        (byte)ControlledValueStorage.Null => ControlledValueStorage.Null,
        (byte)ControlledValueStorage.Raw => ControlledValueStorage.Raw,
        (byte)ControlledValueStorage.Brotli => ControlledValueStorage.Brotli,
        (byte)ControlledValueStorage.Lz4Block => ControlledValueStorage.Lz4Block,
        _ => throw new InvalidDataException("Unknown controlled value method.")
    };

    internal static bool IsCompressed(ControlledValueStorage storage) => storage switch {
        ControlledValueStorage.Null or ControlledValueStorage.Raw => false,
        ControlledValueStorage.Brotli or ControlledValueStorage.Lz4Block => true,
        _ => throw new InvalidOperationException("Unknown internal controlled value storage method.")
    };

    internal static long GetCompressedEncodedLength(uint storedByteCount, uint decodedByteCount)
        => 1L + BareValueEncoding.MeasureVarUInt32(storedByteCount)
            + BareValueEncoding.MeasureVarUInt32(decodedByteCount) + storedByteCount;

    internal static bool IsSmallerThanRaw(int storedByteCount, int decodedByteCount)
        => GetCompressedEncodedLength((uint)storedByteCount, (uint)decodedByteCount) < 1L + decodedByteCount;

    /// <summary>Writes a complete stream into the bounded candidate, or returns zero after proving it cannot beat Raw.</summary>
    /// <remarks>Zero is a size decision, not a fallback for an encoder exception or lack of progress.</remarks>
    internal static int Compress(ControlledValueStorage storage, ReadOnlySpan<byte> source, Span<byte> candidate)
        => storage switch {
            ControlledValueStorage.Brotli => CompressBrotli(source, candidate),
            ControlledValueStorage.Lz4Block => CompressLz4Block(source, candidate),
            _ => throw new InvalidOperationException("The storage method has no encoder.")
        };

    /// <summary>Decodes one complete stream with exact input/output lengths into owned storage.</summary>
    /// <remarks>The caller checks declared limits before decoding and validates the inner Bare value afterwards.</remarks>
    internal static byte[] Decompress(ControlledValueStorage storage, ReadOnlySpan<byte> input, int decodedByteCount)
        => storage switch {
            ControlledValueStorage.Brotli => DecompressBrotli(input, decodedByteCount),
            ControlledValueStorage.Lz4Block => DecompressLz4Block(input, decodedByteCount),
            _ => throw new InvalidDataException("The storage method has no decoder.")
        };

    private static int CompressBrotli(ReadOnlySpan<byte> source, Span<byte> candidate) {
        var encoder = new BrotliEncoder(BrotliQuality, BrotliWindow);
        try {
            int consumed = 0;
            int written = 0;
            while (true) {
                OperationStatus status = encoder.Compress(
                    source.Slice(consumed), candidate.Slice(written),
                    out int readNow, out int wroteNow, isFinalBlock: true);
                consumed = checked(consumed + readNow);
                written = checked(written + wroteNow);
                if (status == OperationStatus.InvalidData) {
                    throw new InvalidOperationException("The Brotli encoder could not encode the prepared value.");
                }

                if (status == OperationStatus.Done) {
                    if (consumed != source.Length || written == 0) {
                        throw new InvalidOperationException("The Brotli encoder did not finish the complete input.");
                    }
                    return written;
                }
                if (status != OperationStatus.DestinationTooSmall) {
                    throw new InvalidOperationException("The final Brotli encoding did not complete.");
                }
                // Produced bytes establish a monotonic complete-size lower bound even if more
                // output remains. DestinationTooSmall alone does not prove a losing candidate.
                if (!IsSmallerThanRaw(written, source.Length)) { return 0; }
                if (readNow == 0 && wroteNow == 0) {
                    throw new InvalidOperationException("The Brotli encoder made no progress.");
                }
            }
        }
        finally {
            encoder.Dispose();
        }
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> input, int decodedByteCount) {
        byte[] output = new byte[decodedByteCount];
        var decoder = new BrotliDecoder();
        try {
            int consumed = 0;
            int written = 0;
            Span<byte> scratch = stackalloc byte[1];
            while (true) {
                bool checkingOverflow = written == output.Length;
                Span<byte> destination = checkingOverflow ? scratch : output.AsSpan(written);
                OperationStatus status = decoder.Decompress(input.Slice(consumed), destination,
                    out int readNow, out int wroteNow);
                consumed = checked(consumed + readNow);
                if (checkingOverflow && wroteNow != 0) {
                    throw new InvalidDataException("The Brotli value exceeds its declared decoded length.");
                }
                written = checked(written + wroteNow);
                if (status == OperationStatus.Done) {
                    if (consumed != input.Length || written != output.Length) {
                        throw new InvalidDataException("The Brotli value did not exactly consume and produce its declared lengths.");
                    }
                    return output;
                }
                if (status != OperationStatus.DestinationTooSmall || (readNow == 0 && wroteNow == 0)) {
                    throw new InvalidDataException("The Brotli value is invalid or truncated.");
                }
            }
        }
        finally {
            decoder.Dispose();
        }
    }
}
