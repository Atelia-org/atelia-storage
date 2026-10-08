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

    /// <summary>Reads nullable controlled bytes into owned storage for every supported storage method.</summary>
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
        ControlledValueStorage storage = ControlledValueCodecs.ReadStorage(ReadByte());
        isNull = storage == ControlledValueStorage.Null;
        compressed = ControlledValueCodecs.IsCompressed(storage);
        switch (storage) {
            case ControlledValueStorage.Null:
                return default;
            case ControlledValueStorage.Raw:
                return ReadControlledRawBody(isString, maxStoredByteCount, maxDecodedByteCount);
            default: // Only a recognized compressed method reaches the common C/U envelope.
                uint storedCount = ReadVarUInt32();
                uint decodedCount = ReadVarUInt32();
                if (storedCount == 0 || decodedCount == 0 || storedCount > int.MaxValue
                    || decodedCount > int.MaxValue || storedCount > maxStoredByteCount
                    || decodedCount > maxDecodedByteCount) {
                    throw new InvalidDataException("The controlled value lengths exceed their permitted range.");
                }
                ReadOnlySpan<byte> input = ReadRawBytes((int)storedCount);
                return ControlledValueCodecs.Decompress(storage, input, (int)decodedCount);
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
}
