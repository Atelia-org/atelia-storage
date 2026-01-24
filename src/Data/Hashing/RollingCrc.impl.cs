using System.Numerics;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Concurrent;
namespace Atelia.Data.Hashing;

partial class RollingCrc {
    #region Util
    [Conditional("DEBUG")]
    internal static void AssertCursorAligned(int cursor, int stepSz, int winSz) {
        Debug.Assert(BitOperations.IsPow2(stepSz));
        Debug.Assert((winSz & (stepSz - 1)) == 0);
        Debug.Assert((cursor & (stepSz - 1)) == 0);
    }
    public static partial uint CrcForward(uint crc, ReadOnlySpan<byte> payload) {
        var remain = payload;
        while (remain.Length >= sizeof(ulong)) {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(remain));
            remain = remain[sizeof(ulong)..];
        }
        if (remain.Length >= sizeof(uint)) {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt32LittleEndian(remain));
            remain = remain[sizeof(uint)..];
        }
        if (remain.Length >= sizeof(ushort)) {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt16LittleEndian(remain));
            remain = remain[sizeof(ushort)..];
        }
        if (remain.Length > 0) {
            crc = BitOperations.Crc32C(crc, remain[0]);
        }
        return crc;
    }
    public static partial bool CheckCodewordForward(ReadOnlySpan<byte> codeword, uint initValue, uint finalXor) {
        uint actualCrc = CrcForward(codeword[..^sizeof(uint)], initValue, finalXor);
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(codeword[^sizeof(uint)..]);
        return actualCrc == expectedCrc;
    }

    public static partial uint CrcBackward(uint crc, ReadOnlySpan<byte> payload) {
        var remainLen = payload.Length;
        while (remainLen >= sizeof(ulong)) {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64BigEndian(payload[(remainLen -= sizeof(ulong))..]));
        }
        if (remainLen >= sizeof(uint)) {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt32BigEndian(payload[(remainLen -= sizeof(uint))..]));
        }
        if (remainLen >= sizeof(ushort)) {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt16BigEndian(payload[(remainLen -= sizeof(ushort))..]));
        }
        if (remainLen > 0) {
            crc = BitOperations.Crc32C(crc, payload[0]);
        }
        return crc;
    }
    public static partial uint SealCodewordBackward(Span<byte> codeword, uint initValue, uint finalXor) {
        uint crc = CrcBackward(codeword[sizeof(uint)..], initValue, finalXor);
        BinaryPrimitives.WriteUInt32BigEndian(codeword, crc);
        return crc;
    }
    #endregion
    #region Table Pool
    private static readonly ConcurrentDictionary<int, Table> s_sharedByWindowSize = new();
    #endregion
}
