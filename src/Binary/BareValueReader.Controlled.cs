using System.Buffers;
using System.IO.Compression;

namespace Atelia.Binary;

public ref partial struct BareValueReader {
    /// <summary>Reads a nullable, explicitly controlled string and commits the cursor only after complete validation.</summary>
    /// <param name="maxStoredByteCount">Maximum stored body bytes; Raw includes the actual inner header.</param>
    /// <param name="maxDecodedByteCount">Maximum complete inner Bare codeword bytes, including its header.</param>
    /// <returns>The decoded string, or null.</returns>
    public string? ReadControlledString(
        int maxStoredByteCount = int.MaxValue, int maxDecodedByteCount = int.MaxValue) {
        ValidateControlledLimits(maxStoredByteCount, maxDecodedByteCount);
        var working = this;
        ReadOnlySpan<byte> body = working.ReadControlledBody(
            isString: true, maxStoredByteCount, maxDecodedByteCount, out bool isNull, out bool compressed);
        string? result = null;
        if (!isNull) {
            var inner = new BareValueReader(body);
            try {
                result = inner.ReadString();
                inner.EnsureFullyConsumed();
            }
            catch (EndOfStreamException error) when (compressed) {
                throw new InvalidDataException("The decompressed string codeword is truncated.", error);
            }
        }
        _consumed = working._consumed;
        return result;
    }

    /// <summary>Reads nullable controlled bytes into owned storage in both Raw and Brotli forms.</summary>
    /// <param name="maxStoredByteCount">Maximum stored body bytes; Raw includes the actual inner header.</param>
    /// <param name="maxDecodedByteCount">Maximum complete inner Bare codeword bytes, including its header.</param>
    /// <returns>An owned array, or null.</returns>
    public byte[]? ReadControlledBytes(
        int maxStoredByteCount = int.MaxValue, int maxDecodedByteCount = int.MaxValue) {
        ValidateControlledLimits(maxStoredByteCount, maxDecodedByteCount);
        var working = this;
        ReadOnlySpan<byte> body = working.ReadControlledBody(
            isString: false, maxStoredByteCount, maxDecodedByteCount, out bool isNull, out bool compressed);
        byte[]? result = null;
        if (!isNull) {
            var inner = new BareValueReader(body);
            try {
                ReadOnlySpan<byte> bytes = inner.ReadBytes();
                inner.EnsureFullyConsumed();
                result = bytes.ToArray();
            }
            catch (EndOfStreamException error) when (compressed) {
                throw new InvalidDataException("The decompressed bytes codeword is truncated.", error);
            }
        }
        _consumed = working._consumed;
        return result;
    }

    private static void ValidateControlledLimits(int maxStoredByteCount, int maxDecodedByteCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxStoredByteCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedByteCount);
    }

    private ReadOnlySpan<byte> ReadControlledBody(bool isString, int maxStoredByteCount,
        int maxDecodedByteCount, out bool isNull, out bool compressed) {
        byte control = ReadByte();
        isNull = control == 0;
        compressed = control == 2;
        switch (control) {
            case 0:
                return default;
            case 1:
                return ReadControlledRawBody(isString, maxStoredByteCount, maxDecodedByteCount);
            case 2:
                uint storedCount = ReadVarUInt32();
                uint decodedCount = ReadVarUInt32();
                if (storedCount == 0 || decodedCount == 0 || storedCount > int.MaxValue
                    || decodedCount > int.MaxValue || storedCount > maxStoredByteCount
                    || decodedCount > maxDecodedByteCount) {
                    throw new InvalidDataException("The controlled value lengths exceed their permitted range.");
                }
                ReadOnlySpan<byte> input = ReadRawBytes((int)storedCount);
                return DecodeControlledBrotli(input, (int)decodedCount);
            default:
                throw new InvalidDataException("Unknown controlled value method.");
        }
    }

    private ReadOnlySpan<byte> ReadControlledRawBody(bool isString, int maxStoredByteCount, int maxDecodedByteCount) {
        int start = _consumed;
        uint header = ReadVarUInt32();
        uint payloadCount = isString && (header & 1) != 0 ? header >> 1 : header;
        long actualInnerLength = (long)(_consumed - start) + payloadCount;
        if (payloadCount > int.MaxValue || actualInnerLength > maxStoredByteCount
            || actualInnerLength > maxDecodedByteCount) {
            throw new InvalidDataException("The Raw controlled value exceeds its permitted range.");
        }
        _ = ReadRawBytes((int)payloadCount);
        return _source.Slice(start, checked((int)actualInnerLength));
    }

    private static byte[] DecodeControlledBrotli(ReadOnlySpan<byte> input, int decodedByteCount) {
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
