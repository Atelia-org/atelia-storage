using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Atelia.Data.Binary;

public static partial class XorEscape {
    private static bool Contains<TSource>(TSource source, int byteLength, uint word)
        where TSource : struct, IXorEscapeSource, allows ref struct {
        var carry = new WordCarry();
        int consumed = 0;
        while (source.TryGetNext(out ReadOnlySpan<byte> bytes)) {
            CountChunk(bytes.Length, byteLength, ref consumed);
            if (ContainsChunk(bytes, word, ref carry)) { return true; }
        }
        EnsureComplete(byteLength, consumed, in carry);
        return false;
    }

    private static uint SelectTinyKey<TSource>(TSource source, int byteLength, uint fence)
        where TSource : struct, IXorEscapeSource, allows ref struct {
        ulong forbidden = 0;
        var carry = new WordCarry();
        int consumed = 0;
        while (source.TryGetNext(out ReadOnlySpan<byte> bytes)) {
            CountChunk(bytes.Length, byteLength, ref consumed);
            MarkTinyChunk(bytes, fence, ref forbidden, ref carry);
        }
        EnsureComplete(byteLength, consumed, in carry);
        // A validated body of at most 63 words cannot prohibit all 64 candidates. Fence is
        // outside this set, so there is no additional raw-key bit to reserve.
        if (forbidden == ulong.MaxValue) {
            throw new InvalidOperationException("Tiny key selection exceeded its validated word count.");
        }
        return (uint)BitOperations.TrailingZeroCount(~forbidden);
    }

    private static bool ContainsChunk(ReadOnlySpan<byte> bytes, uint word, ref WordCarry carry) {
        int offset = 0;
        if (carry.Count != 0) {
            if (!carry.Complete(bytes, ref offset, out uint carriedWord)) { return false; }
            if (carriedWord == word) { return true; }
        }

        int wordBytes = (bytes.Length - offset) & ~3;
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes.Slice(offset, wordBytes));
        uint hostWord = BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);
        // The BCL span search supplies SIMD for the interior; only edges need carry assembly.
        if (words.Contains(hostWord)) { return true; }
        offset += wordBytes;
        carry.StoreSuffix(bytes[offset..]);
        return false;
    }

    private static void MarkTinyChunk(ReadOnlySpan<byte> bytes, uint fence, ref ulong forbidden, ref WordCarry carry) {
        int offset = 0;
        ulong bits = forbidden;
        if (carry.Count != 0) {
            if (!carry.Complete(bytes, ref offset, out uint carriedWord)) { return; }
            uint candidate = carriedWord ^ fence;
            if (candidate < 64) { bits |= 1UL << (int)candidate; }
        }

        int wordBytes = (bytes.Length - offset) & ~3;
        foreach (uint hostWord in MemoryMarshal.Cast<byte, uint>(bytes.Slice(offset, wordBytes))) {
            uint word = BitConverter.IsLittleEndian ? hostWord : BinaryPrimitives.ReverseEndianness(hostWord);
            uint candidate = word ^ fence;
            // C# masks shift counts, so checking the candidate before shifting is essential.
            if (candidate < 64) { bits |= 1UL << (int)candidate; }
        }
        forbidden = bits;
        offset += wordBytes;
        carry.StoreSuffix(bytes[offset..]);
    }

    private static void CountChunk(int chunkLength, int byteLength, ref int consumed) {
        if (chunkLength > byteLength - consumed) {
            throw new InvalidOperationException("The internal source exceeds its captured byte length.");
        }
        consumed += chunkLength;
    }

    private static void EnsureComplete(int byteLength, int consumed, in WordCarry carry) {
        if (consumed != byteLength || carry.Count != 0) {
            throw new InvalidOperationException("The internal source did not complete its captured word-aligned range.");
        }
    }

    private struct WordCarry {
        private uint _word;
        public int Count;

        public bool Complete(ReadOnlySpan<byte> bytes, ref int offset, out uint word) {
            while (offset < bytes.Length && Count < sizeof(uint)) {
                _word |= (uint)bytes[offset++] << (Count++ * 8);
            }
            if (Count < sizeof(uint)) {
                word = 0;
                return false;
            }
            word = _word;
            _word = 0;
            Count = 0;
            return true;
        }

        public void StoreSuffix(ReadOnlySpan<byte> bytes) {
            foreach (byte value in bytes) { _word |= (uint)value << (Count++ * 8); }
        }
    }
}
