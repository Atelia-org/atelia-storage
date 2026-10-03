using System.Buffers.Binary;
using System.Numerics;

namespace Atelia.Data.Binary;

public static partial class XorEscape {
    /// <summary>Copies and XORs the source into the destination's equally sized prefix.</summary>
    /// <remarks>
    /// The key's four little-endian bytes repeat, starting at bytePhase (0..3). Input length
    /// need not be word-aligned. Source and the written destination prefix must be disjoint or
    /// begin at exactly the same address; shifted overlap is rejected, including for key zero.
    /// All arguments are checked before writes. A zero key copies unchanged bytes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Byte phase is outside 0..3.</exception>
    /// <exception cref="ArgumentException">Destination is too short or overlaps at a different address.</exception>
    public static void Copy(ReadOnlySpan<byte> source, Span<byte> destination, uint key, int bytePhase = 0) {
        if ((uint)bytePhase > 3) { throw new ArgumentOutOfRangeException(nameof(bytePhase), bytePhase, "Byte phase must be in 0..3."); }
        if (destination.Length < source.Length) {
            throw new ArgumentException("Destination is shorter than source.", nameof(destination));
        }
        destination = destination[..source.Length];
        bool overlaps = source.Overlaps(destination, out int offset);
        if (overlaps && offset != 0) {
            throw new ArgumentException("Source and destination must be disjoint or exactly aliased.", nameof(destination));
        }
        if (key == 0) {
            if (!overlaps) { source.CopyTo(destination); }
            return;
        }

        uint phaseKey = BitOperations.RotateRight(key, bytePhase * 8);
        int cursor = 0;
        int vectorSize = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && source.Length >= vectorSize) {
            Vector<byte> mask;
            if (BitConverter.IsLittleEndian) {
                mask = Vector.AsVectorByte(new Vector<uint>(phaseKey));
            }
            else {
                Span<byte> maskBytes = stackalloc byte[vectorSize];
                for (int i = 0; i < maskBytes.Length; i++) {
                    maskBytes[i] = (byte)(key >> (((bytePhase + i) & 3) * 8));
                }
                mask = new Vector<byte>(maskBytes);
            }
            while (cursor <= source.Length - vectorSize) {
                var value = new Vector<byte>(source.Slice(cursor, vectorSize));
                (value ^ mask).CopyTo(destination.Slice(cursor, vectorSize));
                cursor += vectorSize;
            }
        }

        // Each vector/ulong/uint step preserves phase; only the final two-byte step advances it.
        ReadOnlySpan<byte> remainingSource = source[cursor..];
        Span<byte> remainingDestination = destination[cursor..];
        ulong mask64 = phaseKey | ((ulong)phaseKey << 32);
        while (remainingSource.Length >= sizeof(ulong)) {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(remainingSource) ^ mask64;
            BinaryPrimitives.WriteUInt64LittleEndian(remainingDestination, value);
            remainingSource = remainingSource[sizeof(ulong)..];
            remainingDestination = remainingDestination[sizeof(ulong)..];
        }
        if (remainingSource.Length >= sizeof(uint)) {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(remainingSource) ^ phaseKey;
            BinaryPrimitives.WriteUInt32LittleEndian(remainingDestination, value);
            remainingSource = remainingSource[sizeof(uint)..];
            remainingDestination = remainingDestination[sizeof(uint)..];
        }
        if (remainingSource.Length >= sizeof(ushort)) {
            ushort value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(remainingSource) ^ (ushort)phaseKey);
            BinaryPrimitives.WriteUInt16LittleEndian(remainingDestination, value);
            remainingSource = remainingSource[sizeof(ushort)..];
            remainingDestination = remainingDestination[sizeof(ushort)..];
            phaseKey >>= 16;
        }
        if (!remainingSource.IsEmpty) {
            remainingDestination[0] = (byte)(remainingSource[0] ^ (byte)phaseKey);
        }
    }

    /// <summary>XORs a span in place using the same little-endian byte phase as <see cref="Copy"/>.</summary>
    /// <remarks>A zero key leaves bytes untouched; bytePhase is still validated.</remarks>
    public static void InPlace(Span<byte> bytes, uint key, int bytePhase = 0) {
        Copy(bytes, bytes, key, bytePhase);
    }
}
