namespace Atelia.Binary;

/// <summary>Measures and prepares default bare-value encodings.</summary>
public static partial class BareValueEncoding {
    public static int MeasureVarUInt16(ushort value) => MeasureVarUInt(value);
    public static int MeasureVarUInt32(uint value) => MeasureVarUInt(value);
    public static int MeasureVarUInt64(ulong value) => MeasureVarUInt(value);
    public static int MeasureVarInt16(short value) => MeasureVarUInt16(unchecked((ushort)((value << 1) ^ (value >> 15))));
    public static int MeasureVarInt32(int value) => MeasureVarUInt32(unchecked((uint)((value << 1) ^ (value >> 31))));
    public static int MeasureVarInt64(long value) => MeasureVarUInt64(unchecked((ulong)((value << 1) ^ (value >> 63))));

    private static int MeasureVarUInt(ulong value) {
        int count = 1;
        while (value >= 0x80) {
            value >>= 7;
            count++;
        }

        return count;
    }
}
