using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Atelia.Data.Hashing;

namespace RbfCodecCost;

public enum KeyStrategy { FullBitmap, SmallBitmap, ZeroFirst, ZeroThenOne, ZeroThenRandom, ZeroThenRandom2Bitmap, ZeroThenTinyBitmapRandom }

/// <summary>Preprocessed RBF2 frame. Chunks remain borrowed and must not change before writing.</summary>
public sealed class PreparedFrame {
    public byte[][] Chunks { get; }
    public int CoverageLength { get; }
    public int MetaLength { get; }
    public int FrameLength { get; }
    public int PaddingLength { get; }
    public uint Key { get; }
    // Plaintext padding + PayloadCRC (LE) + TrailerCodeword (backward CRC, stored BE).
    public byte[] Footer { get; }
    // Peak temporary bitmap data bytes. Excludes borrowed input, retained Footer,
    // CLR object headers, and the fixed-size scalar/span cursors on the stack.
    public long ScratchBytes { get; }
    // Heap bitmap payload allocated during Prepare; SmallBitmap's 32-byte stack bitmap is excluded.
    public long BitmapBytes { get; }
    // Key-search passes started; targeted scans stop when their candidate is forbidden.
    // Payload CRC is a separate traversal and is not included in this count.
    public int BodyWordScanPasses { get; }
    public int RandomDraws { get; }
    public int RandomSearches { get; }

    internal PreparedFrame(byte[][] chunks, int coverageLength, int metaLength, int frameLength,
        int paddingLength, uint key, byte[] footer, long scratchBytes, long bitmapBytes,
        int bodyWordScanPasses, int randomDraws, int randomSearches) {
        Chunks = chunks;
        CoverageLength = coverageLength;
        MetaLength = metaLength;
        FrameLength = frameLength;
        PaddingLength = paddingLength;
        Key = key;
        Footer = footer;
        ScratchBytes = scratchBytes;
        BitmapBytes = bitmapBytes;
        BodyWordScanPasses = bodyWordScanPasses;
        RandomDraws = randomDraws;
        RandomSearches = randomSearches;
    }
}

/// <summary>In-memory writer experiment only; this does not change the production RBF writer.</summary>
public static class PrototypeCodec {
    public const uint Fence = 0x32464252u;
    public const int MaxFrameLength = (1 << 28) - 4;
    public const int FixedOverhead = 28;
    private const int SmallCandidateCount = 256;
    private const int SmallBitmapWordCount = SmallCandidateCount / 32;

    public static PreparedFrame Prepare(byte[][] chunks, int metaLength, uint tag, KeyStrategy strategy,
        Func<uint>? randomCandidate = null) {
        ArgumentNullException.ThrowIfNull(chunks);
        if ((uint)strategy > (uint)KeyStrategy.ZeroThenTinyBitmapRandom) {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }
        if ((uint)metaLength > ushort.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(metaLength));
        }

        long inputLengthLong = 0;
        foreach (byte[] chunk in chunks) {
            ArgumentNullException.ThrowIfNull(chunk);
            inputLengthLong += chunk.Length;
            if (inputLengthLong > MaxFrameLength - FixedOverhead) {
                throw new ArgumentOutOfRangeException(nameof(chunks), "Payload + TailMeta exceeds the RBF2 frame limit.");
            }
        }
        if (metaLength > inputLengthLong) {
            throw new ArgumentOutOfRangeException(nameof(metaLength), "TailMeta is the suffix of the supplied chunks.");
        }
        int inputLength = (int)inputLengthLong;
        int paddingLength = (-inputLength) & 3;
        int coverageLength = inputLength + paddingLength;
        int frameLength = coverageLength + FixedOverhead;
        int bodyWordCount = (frameLength - 8) / 4;

        // CRC and forbidden-word scans are intentionally separate loops. Neither copies input.
        byte[] footer = new byte[paddingLength + 20];
        uint rollingCrc = RollingCrc.DefaultInitValue;
        foreach (byte[] chunk in chunks) {
            rollingCrc = RollingCrc.CrcForward(rollingCrc, chunk);
        }
        rollingCrc = RollingCrc.CrcForward(rollingCrc, footer.AsSpan(0, paddingLength));
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(paddingLength), rollingCrc ^ RollingCrc.DefaultFinalXor);
        Span<byte> trailer = footer.AsSpan(paddingLength + 4, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], ((uint)paddingLength << 29) | (uint)metaLength);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], tag);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], (uint)frameLength);
        RollingCrc.SealCodewordBackward(trailer);

        uint key;
        long heapBitmapBytes = 0;
        long stackBitmapBytes = 0;
        int scanPasses = 1;
        int randomDraws = 0, randomSearches = 0;
        if (strategy == KeyStrategy.FullBitmap) {
            key = SelectFullBitmap(chunks, footer, bodyWordCount, out heapBitmapBytes);
        }
        else if (strategy == KeyStrategy.SmallBitmap) {
            Span<uint> smallBitmap = stackalloc uint[SmallBitmapWordCount];
            smallBitmap.Clear();
            stackBitmapBytes = SmallBitmapWordCount * sizeof(uint);
            var scan = new WordScan(smallBitmap, Math.Min(bodyWordCount, SmallCandidateCount - 1), searchOnly: false);
            ScanBody(chunks, footer, ref scan);
            if (!TrySmallestUnmarked(smallBitmap, scan.MaxCandidate, out key)) {
                scanPasses++;
                key = SelectFullBitmap(chunks, footer, bodyWordCount, out heapBitmapBytes);
            }
        }
        else {
            var scan = new WordScan(Span<uint>.Empty, 0, searchOnly: true, searchCandidate: 0);
            ScanBody(chunks, footer, ref scan);
            if (!scan.SearchForbidden) {
                key = 0;
            }
            else {
                scanPasses++;
                // EscapePayload is the interval after the left Fence and before the raw Key.
                // Its raw HeadLen occupies 4B, leaving at most 63 encoded words at 256B.
                if (strategy == KeyStrategy.ZeroThenTinyBitmapRandom && frameLength - 4 <= 256) {
                    key = SelectTinyBitmap(chunks, footer);
                }
                else if (strategy is KeyStrategy.ZeroThenRandom or KeyStrategy.ZeroThenRandom2Bitmap or KeyStrategy.ZeroThenTinyBitmapRandom) {
                    // The raw tail Key must not equal Fence. Zero was already proven forbidden.
                    // A bounded variant limits valid searches, not rejection draws from the RNG.
                    while (true) {
                        do {
                            key = randomCandidate is null ? NextRandomKey() : randomCandidate();
                            randomDraws++;
                        } while (key is 0 or Fence);
                        randomSearches++;
                        scan = new WordScan(Span<uint>.Empty, 0, searchOnly: true, searchCandidate: key);
                        ScanBody(chunks, footer, ref scan);
                        if (!scan.SearchForbidden) break;
                        scanPasses++;
                        if (strategy == KeyStrategy.ZeroThenRandom2Bitmap && randomSearches == 2) {
                            key = SelectFullBitmap(chunks, footer, bodyWordCount, out heapBitmapBytes);
                            break;
                        }
                    }
                }
                else if (strategy == KeyStrategy.ZeroThenOne) {
                    // The body always contains at least five words, so candidate 1 is in 0..m.
                    // Search only this one additional candidate; there is no per-Key retry loop.
                    scan = new WordScan(Span<uint>.Empty, 0, searchOnly: true, searchCandidate: 1);
                    ScanBody(chunks, footer, ref scan);
                    if (!scan.SearchForbidden) {
                        key = 1;
                    }
                    else {
                        scanPasses++;
                        key = SelectFullBitmap(chunks, footer, bodyWordCount, out heapBitmapBytes);
                    }
                }
                else {
                    key = SelectFullBitmap(chunks, footer, bodyWordCount, out heapBitmapBytes);
                }
            }
        }

        return new PreparedFrame(chunks, coverageLength, metaLength, frameLength, paddingLength,
            key, footer, heapBitmapBytes + stackBitmapBytes, heapBitmapBytes, scanPasses, randomDraws, randomSearches);
    }

    internal static uint NextRandomKey() {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static uint SelectTinyBitmap(byte[][] chunks, byte[] footer) {
        // 63 words exclude at most 63 of these 64 candidates. No heap/stack array.
        ulong forbidden = 0;
        uint pendingWord = 0;
        int pendingCount = 0;
        foreach (byte[] chunk in chunks) TinyBitmapChunk(chunk, ref forbidden, ref pendingWord, ref pendingCount);
        TinyBitmapChunk(footer, ref forbidden, ref pendingWord, ref pendingCount);
        if (pendingCount != 0 || forbidden == ulong.MaxValue) {
            throw new InvalidOperationException("Tiny bitmap requires a complete body of at most 63 words.");
        }
        return (uint)BitOperations.TrailingZeroCount(~forbidden);
    }

    private static void TinyBitmapChunk(ReadOnlySpan<byte> bytes, ref ulong forbidden, ref uint pendingWord, ref int pendingCount) {
        int offset = 0;
        ulong bits = forbidden;
        if (pendingCount != 0) {
            while (offset < bytes.Length && pendingCount < 4) pendingWord |= (uint)bytes[offset++] << (pendingCount++ * 8);
            if (pendingCount < 4) return;
            uint candidate = pendingWord ^ Fence;
            if (candidate < 64) bits |= 1UL << (int)candidate;
            pendingWord = 0;
            pendingCount = 0;
        }
        int wordBytes = (bytes.Length - offset) & ~3;
        foreach (uint hostWord in MemoryMarshal.Cast<byte, uint>(bytes.Slice(offset, wordBytes))) {
            uint word = BitConverter.IsLittleEndian ? hostWord : BinaryPrimitives.ReverseEndianness(hostWord);
            uint candidate = word ^ Fence;
            // C# masks ulong shift counts, so this range check is required.
            if (candidate < 64) bits |= 1UL << (int)candidate;
        }
        forbidden = bits;
        offset += wordBytes;
        while (offset < bytes.Length) pendingWord |= (uint)bytes[offset++] << (pendingCount++ * 8);
    }

    /// <summary>Optional contiguous-output check. Streaming callers can encode chunks independently
    /// with XorTransform.Copy and the cumulative byte offset relative to the body start.</summary>
    public static void Serialize(PreparedFrame frame, Span<byte> destination) {
        ArgumentNullException.ThrowIfNull(frame);
        if (destination.Length != frame.FrameLength + 4) {
            throw new ArgumentException("Destination must hold exactly FrameBytes plus its trailing Fence.", nameof(destination));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)frame.FrameLength);
        int bodyOffset = 0;
        foreach (byte[] chunk in frame.Chunks) {
            XorTransform.Copy(chunk, destination.Slice(4 + bodyOffset, chunk.Length), frame.Key, bodyOffset, XorMode.Vector);
            bodyOffset += chunk.Length;
        }
        XorTransform.Copy(frame.Footer, destination.Slice(4 + bodyOffset, frame.Footer.Length),
            frame.Key, bodyOffset, XorMode.Vector);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(frame.FrameLength - 4, 4), frame.Key);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(frame.FrameLength, 4), Fence);
    }

    private static uint SelectFullBitmap(byte[][] chunks, byte[] footer, int bodyWordCount, out long bitmapBytes) {
        // m body words exclude at most m distinct Keys. Candidates 0..m therefore suffice.
        uint[] bitmap = new uint[(bodyWordCount + 1 + 31) / 32];
        bitmapBytes = (long)bitmap.Length * sizeof(uint);
        var scan = new WordScan(bitmap, bodyWordCount, searchOnly: false);
        ScanBody(chunks, footer, ref scan);
        if (!TrySmallestUnmarked(bitmap, bodyWordCount, out uint key)) {
            throw new InvalidOperationException("No Key in the mathematically sufficient candidate range.");
        }
        return key;
    }

    private static bool TrySmallestUnmarked(ReadOnlySpan<uint> bitmap, int maxCandidate, out uint key) {
        for (int i = 0; i < bitmap.Length; i++) {
            uint available = ~bitmap[i];
            if (available != 0) {
                uint candidate = (uint)(i * 32 + BitOperations.TrailingZeroCount(available));
                if (candidate <= (uint)maxCandidate) {
                    key = candidate;
                    return true;
                }
                break;
            }
        }
        key = 0;
        return false;
    }

    private static void ScanBody(byte[][] chunks, byte[] footer, ref WordScan scan) {
        foreach (byte[] chunk in chunks) {
            ScanChunk(chunk, ref scan);
            if (scan.SearchForbidden) { return; }
        }
        ScanChunk(footer, ref scan);
        if (!scan.SearchForbidden && scan.PendingCount != 0) {
            throw new InvalidOperationException("The complete encoded body must contain whole 32-bit words.");
        }
    }

    private static void ScanChunk(ReadOnlySpan<byte> bytes, ref WordScan scan) {
        int offset = 0;
        if (scan.PendingCount != 0) {
            while (offset < bytes.Length && scan.PendingCount < 4) {
                scan.PendingWord |= (uint)bytes[offset++] << (scan.PendingCount++ * 8);
            }
            if (scan.PendingCount != 4) { return; }
            ObserveWord(scan.PendingWord, ref scan);
            scan.PendingCount = 0;
            scan.PendingWord = 0;
            if (scan.SearchForbidden) { return; }
        }

        int wordBytes = (bytes.Length - offset) & ~3;
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes.Slice(offset, wordBytes));
        if (scan.SearchOnly) {
            // .NET span search supplies its vectorized implementation. Conversion matters only
            // for the host-endian uint view; the format and carry assembly remain little-endian.
            uint forbiddenWord = Fence ^ scan.SearchCandidate;
            uint hostWord = BitConverter.IsLittleEndian ? forbiddenWord : BinaryPrimitives.ReverseEndianness(forbiddenWord);
            if (words.Contains(hostWord)) {
                scan.SearchForbidden = true;
                return;
            }
        }
        else {
            foreach (uint hostWord in words) {
                uint word = BitConverter.IsLittleEndian ? hostWord : BinaryPrimitives.ReverseEndianness(hostWord);
                ObserveWord(word, ref scan);
            }
        }
        offset += wordBytes;
        while (offset < bytes.Length) {
            scan.PendingWord |= (uint)bytes[offset++] << (scan.PendingCount++ * 8);
        }
    }

    private static void ObserveWord(uint word, ref WordScan scan) {
        uint forbidden = word ^ Fence;
        if (scan.SearchOnly) {
            scan.SearchForbidden |= forbidden == scan.SearchCandidate;
        }
        else if (forbidden <= (uint)scan.MaxCandidate) {
            scan.Bitmap[(int)(forbidden >> 5)] |= 1u << (int)(forbidden & 31);
        }
    }

    private ref struct WordScan {
        public Span<uint> Bitmap;
        public int MaxCandidate;
        public bool SearchOnly;
        public uint SearchCandidate;
        public bool SearchForbidden;
        public uint PendingWord;
        public int PendingCount;

        public WordScan(Span<uint> bitmap, int maxCandidate, bool searchOnly, uint searchCandidate = 0) {
            Bitmap = bitmap;
            MaxCandidate = maxCandidate;
            SearchOnly = searchOnly;
            SearchCandidate = searchCandidate;
            SearchForbidden = false;
            PendingWord = 0;
            PendingCount = 0;
        }
    }
}
