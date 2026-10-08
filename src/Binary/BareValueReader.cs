using System.Buffers.Binary;

namespace Atelia.Binary;

/// <summary>Reads schema-selected bare values from borrowed input.</summary>
public ref partial struct BareValueReader {
    private readonly ReadOnlySpan<byte> _source;
    private int _consumed;

    public BareValueReader(ReadOnlySpan<byte> source) {
        _source = source;
        _consumed = 0;
    }

    public int ConsumedCount => _consumed;
    public int RemainingCount => _source.Length - _consumed;
    public bool End => _consumed == _source.Length;

    public void EnsureFullyConsumed() {
        if (!End) {
            throw new InvalidDataException("The input contains unconsumed bytes.");
        }
    }

    public ReadOnlySpan<byte> ReadRawBytes(int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > RemainingCount) {
            throw new EndOfStreamException("The input does not contain the requested bytes.");
        }

        ReadOnlySpan<byte> result = _source.Slice(_consumed, length);
        _consumed += length;
        return result;
    }

    public byte ReadByte() => ReadRawBytes(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public bool ReadBoolean() {
        if (RemainingCount == 0) {
            throw new EndOfStreamException("The Boolean value is truncated.");
        }

        byte value = _source[_consumed];
        if (value > 1) {
            throw new InvalidDataException("A Boolean must be encoded as 00 or 01.");
        }

        _consumed++;
        return value != 0;
    }

    public ushort ReadUInt16LE() => BinaryPrimitives.ReadUInt16LittleEndian(ReadRawBytes(2));
    public short ReadInt16LE() => BinaryPrimitives.ReadInt16LittleEndian(ReadRawBytes(2));
    public uint ReadUInt32LE() => BinaryPrimitives.ReadUInt32LittleEndian(ReadRawBytes(4));
    public int ReadInt32LE() => BinaryPrimitives.ReadInt32LittleEndian(ReadRawBytes(4));
    public ulong ReadUInt64LE() => BinaryPrimitives.ReadUInt64LittleEndian(ReadRawBytes(8));
    public long ReadInt64LE() => BinaryPrimitives.ReadInt64LittleEndian(ReadRawBytes(8));
    public char ReadCharLE() => (char)ReadUInt16LE();
    public Half ReadHalfLE() => BitConverter.UInt16BitsToHalf(ReadUInt16LE());
    public float ReadSingleLE() => BitConverter.UInt32BitsToSingle(ReadUInt32LE());
    public double ReadDoubleLE() => BitConverter.UInt64BitsToDouble(ReadUInt64LE());

    public ushort ReadVarUInt16() => (ushort)ReadVarUInt(3, 3);
    public uint ReadVarUInt32() => (uint)ReadVarUInt(5, 15);
    public ulong ReadVarUInt64() => ReadVarUInt(10, 1);

    public short ReadVarInt16() {
        ushort value = ReadVarUInt16();
        return unchecked((short)((value >> 1) ^ -(value & 1)));
    }

    public int ReadVarInt32() {
        uint value = ReadVarUInt32();
        return unchecked((int)(value >> 1)) ^ -unchecked((int)(value & 1));
    }

    public long ReadVarInt64() {
        ulong value = ReadVarUInt64();
        return unchecked((long)(value >> 1)) ^ -unchecked((long)(value & 1));
    }

    private ulong ReadVarUInt(int maxByteCount, byte finalPayloadMaximum) {
        int start = _consumed;
        ulong result = 0;
        for (int index = 0; index < maxByteCount; index++) {
            // Subtraction keeps the availability check safe near int.MaxValue.
            if (index >= _source.Length - start) {
                throw new EndOfStreamException("The Base128 value is truncated.");
            }

            byte current = _source[start + index];
            byte payload = (byte)(current & 0x7F);
            if (index == maxByteCount - 1 && (payload > finalPayloadMaximum || (current & 0x80) != 0)) {
                throw new InvalidDataException("The Base128 value exceeds its integer width.");
            }

            result |= (ulong)payload << (index * 7);
            if ((current & 0x80) == 0) {
                _consumed = start + index + 1;
                return result;
            }
        }

        throw new InvalidDataException("The Base128 value exceeds its integer width.");
    }
}
