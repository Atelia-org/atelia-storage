using K4os.Compression.LZ4;

namespace Atelia.Binary;

internal static partial class ControlledValueCodecs {
    internal static void ValidatePreparationLength(ControlledValueStorage storage, int length, string parameterName) {
        // K4os 1.3.8 supports encoder input up to 0x7E000000 bytes. Check before materializing U;
        // otherwise Encode's -1 would conflate unsupported input with an unprofitable candidate.
        if (storage == ControlledValueStorage.Lz4Block && LZ4Codec.MaximumOutputSize(length) == 0) {
            throw new ArgumentOutOfRangeException(parameterName, "The complete inner Bare value exceeds the LZ4 block encoder's input limit.");
        }
    }

    private static int CompressLz4Block(ReadOnlySpan<byte> source, Span<byte> candidate) {
        int written = LZ4Codec.Encode(source, candidate, LZ4Level.L00_FAST);
        // For prevalidated nonempty input and the FAST encoder, -1 means insufficient output
        // capacity (audited at upstream tag 1.3.8, LL32/LL64.fast.cs). A block that cannot fit in
        // the U-byte candidate cannot beat Raw after its C/U headers. No general exception catch.
        if (written == -1) { return 0; }
        if (written <= 0) { throw new InvalidOperationException("The LZ4 block encoder did not finish the complete input."); }
        return written;
    }

    private static byte[] DecompressLz4Block(ReadOnlySpan<byte> input, int decodedByteCount) {
        ValidateLz4Block(input, decodedByteCount);
        byte[] output = new byte[decodedByteCount];
        // Decode, not PartialDecode: the pinned safe decoder also requires the full input end.
        if (LZ4Codec.Decode(input, output) != decodedByteCount) {
            throw new InvalidDataException("The LZ4 block did not decode to its declared length.");
        }
        return output;
    }

    // Structural preflight, not a second data decoder: skip literal content and count matches.
    // It proves the C boundary and U, rejects dictionary references and offset=0 (which safe
    // decoders may tolerate), and enforces the standard 5-literal/12-byte ending constraints.
    // The block format, not K4os Pickler or LZ4 Frame, owns these rules:
    // https://github.com/lz4/lz4/blob/v1.9.4/doc/lz4_Block_format.md
    private static void ValidateLz4Block(ReadOnlySpan<byte> input, int decodedByteCount) {
        int consumed = 0;
        int written = 0;
        int lastMatchStart = -1;
        while (consumed < input.Length) {
            byte token = input[consumed++];
            int literals = ReadLz4Length(input, ref consumed, token >> 4, decodedByteCount - written);
            if (literals > input.Length - consumed) { throw new InvalidDataException("The LZ4 literals are truncated."); }
            consumed += literals;
            written += literals;
            if (consumed == input.Length) {
                if (written != decodedByteCount || (lastMatchStart >= 0
                    && (literals < 5 || decodedByteCount - lastMatchStart < 12))) {
                    throw new InvalidDataException("The LZ4 block has an invalid final sequence or decoded length.");
                }
                return;
            }

            if (input.Length - consumed < 2) { throw new InvalidDataException("The LZ4 match offset is truncated."); }
            int offset = input[consumed] | (input[consumed + 1] << 8);
            consumed += 2;
            if (offset == 0 || offset > written) {
                throw new InvalidDataException("The LZ4 match requires an invalid offset or external dictionary.");
            }
            int matchLength = ReadLz4Length(input, ref consumed, token & 15, decodedByteCount - written - 4) + 4;
            lastMatchStart = written;
            written += matchLength;
        }
        throw new InvalidDataException("The LZ4 block has no final literal sequence.");
    }

    private static int ReadLz4Length(ReadOnlySpan<byte> input, ref int consumed, int initial, int limit) {
        if (initial > limit) { throw new InvalidDataException("The LZ4 sequence exceeds its declared decoded length."); }
        int length = initial;
        if (initial != 15) { return length; }
        byte extension;
        do {
            if (consumed == input.Length) { throw new InvalidDataException("The LZ4 length extension is truncated."); }
            extension = input[consumed++];
            if (extension > limit - length) { throw new InvalidDataException("The LZ4 sequence exceeds its declared decoded length."); }
            length += extension;
        } while (extension == byte.MaxValue);
        return length;
    }
}
