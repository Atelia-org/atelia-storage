using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.Binary;

/// <summary>Writes schema-selected bare values to a borrowed buffer writer.</summary>
public readonly partial struct BareValueWriter {
    internal const int MaxSizeHint = 16 * 1024;
    private readonly IBufferWriter<byte>? _downstream;

    public BareValueWriter(IBufferWriter<byte> downstream) {
        ArgumentNullException.ThrowIfNull(downstream);
        _downstream = downstream;
    }

    private IBufferWriter<byte> GetDownstream() => _downstream
        ?? throw new InvalidOperationException("The writer has not been initialized.");

    public void WriteRawBytes(ReadOnlySpan<byte> value) {
        IBufferWriter<byte> downstream = GetDownstream();
        while (!value.IsEmpty) {
            int count = Math.Min(value.Length, MaxSizeHint);
            Span<byte> output = downstream.GetSpan(count);
            value[..count].CopyTo(output);
            downstream.Advance(count);
            value = value[count..];
        }
    }

    public void WriteByte(byte value) {
        IBufferWriter<byte> downstream = GetDownstream();
        downstream.GetSpan(1)[0] = value;
        downstream.Advance(1);
    }

    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16LE(ushort value) {
        IBufferWriter<byte> downstream = GetDownstream();
        BinaryPrimitives.WriteUInt16LittleEndian(downstream.GetSpan(2), value);
        downstream.Advance(2);
    }

    public void WriteInt16LE(short value) {
        IBufferWriter<byte> downstream = GetDownstream();
        BinaryPrimitives.WriteInt16LittleEndian(downstream.GetSpan(2), value);
        downstream.Advance(2);
    }

    public void WriteUInt32LE(uint value) {
        IBufferWriter<byte> downstream = GetDownstream();
        BinaryPrimitives.WriteUInt32LittleEndian(downstream.GetSpan(4), value);
        downstream.Advance(4);
    }

    public void WriteInt32LE(int value) {
        IBufferWriter<byte> downstream = GetDownstream();
        BinaryPrimitives.WriteInt32LittleEndian(downstream.GetSpan(4), value);
        downstream.Advance(4);
    }

    public void WriteUInt64LE(ulong value) {
        IBufferWriter<byte> downstream = GetDownstream();
        BinaryPrimitives.WriteUInt64LittleEndian(downstream.GetSpan(8), value);
        downstream.Advance(8);
    }

    public void WriteInt64LE(long value) {
        IBufferWriter<byte> downstream = GetDownstream();
        BinaryPrimitives.WriteInt64LittleEndian(downstream.GetSpan(8), value);
        downstream.Advance(8);
    }

    public void WriteCharLE(char value) => WriteUInt16LE(value);
    public void WriteHalfLE(Half value) => WriteUInt16LE(BitConverter.HalfToUInt16Bits(value));
    public void WriteSingleLE(float value) => WriteUInt32LE(BitConverter.SingleToUInt32Bits(value));
    public void WriteDoubleLE(double value) => WriteUInt64LE(BitConverter.DoubleToUInt64Bits(value));
    public void WriteVarUInt16(ushort value) => WriteVarUInt(value, 3);
    public void WriteVarUInt32(uint value) => WriteVarUInt(value, 5);
    public void WriteVarUInt64(ulong value) => WriteVarUInt(value, 10);
    public void WriteVarInt16(short value) => WriteVarUInt16(unchecked((ushort)((value << 1) ^ (value >> 15))));
    public void WriteVarInt32(int value) => WriteVarUInt32(unchecked((uint)((value << 1) ^ (value >> 31))));
    public void WriteVarInt64(long value) => WriteVarUInt64(unchecked((ulong)((value << 1) ^ (value >> 63))));

    private void WriteVarUInt(ulong value, int maxByteCount) {
        IBufferWriter<byte> downstream = GetDownstream();
        Span<byte> output = downstream.GetSpan(maxByteCount);
        int count = 0;
        while (value >= 0x80) {
            output[count++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        output[count++] = (byte)value;
        downstream.Advance(count);
    }
}
