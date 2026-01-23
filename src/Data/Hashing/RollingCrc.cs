
using System.Buffers.Binary;
using System.Numerics;

namespace Atelia.Data.Hashing;

public static partial class RollingCrc {
    public const uint Polynomial = 0x1EDC6F41u, ReflectedPolynomial = 0x82F63B78u;
    public const uint DefaultInitValue = 0xFFFFFFFFu, DefaultFinalXor = 0xFFFFFFFFu;
    public const uint EmptyRollingRaw = 0u;

    public static uint GetFinalResidue(uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor) => BitOperations.Crc32C(initValue, initValue ^ finalXor) ^ finalXor;

    public static partial uint CrcForward(uint crc, Span<byte> payload);
    public static uint CrcForward(Span<byte> payload, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor) => CrcForward(initValue, payload) ^ finalXor;
    public static partial uint CrcBackward(uint crc, Span<byte> payload);
    public static uint CrcBackward(Span<byte> payload, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor) => CrcBackward(initValue, payload) ^ finalXor;

    #region Seal / Check Codeword
    public static uint SealCodewordForward(Span<byte> codeword, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor) {
        uint crc = CrcForward(codeword[..^sizeof(uint)], initValue, finalXor);
        BinaryPrimitives.WriteUInt32LittleEndian(codeword[^sizeof(uint)..], crc);
        return crc;
    }
    public static partial bool CheckCodewordForward(Span<byte> codeword, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor);

    public static partial uint SealCodewordBackward(Span<byte> codeword, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor);
    public static bool CheckCodewordBackward(Span<byte> codeword, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor) {
        uint actualCrc = CrcBackward(codeword[sizeof(uint)..], initValue, finalXor);
        uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(codeword);
        return actualCrc == expectedCrc;
    }
    #endregion
}

partial class RollingCrc {
    public static Scanner<Forward> ForwardScanner(int windowSize) => new(SharedTable(windowSize));
    public static Scanner<Backward> BackwardScanner(int windowSize) => new(SharedTable(windowSize));

    public ref struct CodewordMatch<T>(ReadOnlySpan<T> remainChunk, long processed, ReadOnlySpan<byte> codeword, bool isBackward) {
        public ReadOnlySpan<T> RemainChunk { get; } = remainChunk;
        public long Processed { get; } = processed;
        public ReadOnlySpan<byte> Codeword { get; } = codeword;
        public bool IsBackward { get; } = isBackward;

        public ReadOnlySpan<byte> Payload => IsBackward ? Codeword[sizeof(uint)..] : Codeword[..^sizeof(uint)];
        public uint FinalCrc => IsBackward ? BinaryPrimitives.ReadUInt32BigEndian(Codeword) : BinaryPrimitives.ReadUInt32LittleEndian(Codeword[^sizeof(uint)..]);
    }

    public readonly partial struct Forward : IDirection;
    public readonly partial struct Backward : IDirection;

    public sealed partial class Scanner<D>(Table table) where D : struct, IDirection {
        #region Scan
        public bool IsAtMatch => _isAtMatch;

        public partial bool TryFindCodeword(ReadOnlySpan<byte> remainChunk, out CodewordMatch<byte> match);
        public partial bool TryFindCodeword(ReadOnlySpan<ushort> remainChunk, out CodewordMatch<ushort> match);
        public partial bool TryFindCodeword(ReadOnlySpan<uint> remainChunk, out CodewordMatch<uint> match);
        public partial bool TryFindCodeword(ReadOnlySpan<ulong> remainChunk, out CodewordMatch<ulong> match);
        #endregion
        public Table Table => _table;
        public uint RollingRaw => _rollingRaw;
        public long Processed => _processed;
        public int WindowSize => _table.WindowSize;
        public bool IsFilled => _table.WindowSize <= _processed;

        public partial ReadOnlySpan<byte> BorrowBufferView();
        public partial bool TryCopyTo(Span<byte> destBuffer);
        public partial void Reset(Table? newTable = null);
    }
}

partial class RollingCrc {
    public static Table SharedTable(int windowSize) => s_sharedByWindowSize.GetOrAdd(windowSize, static size => new Table(size));
    public sealed partial class Table(int windowSize, uint initValue = DefaultInitValue, uint finalXor = DefaultFinalXor) {
        public int WindowSize { get; } = 0 < windowSize ? windowSize : throw new ArgumentOutOfRangeException(nameof(windowSize));
        public uint FinalResidue { get; } = GetFinalResidue(initValue, finalXor);

        public uint RollOut(uint rollingRawCrc, byte outgoing) => _remTbl._RollOut8(rollingRawCrc, outgoing);
        public uint RollIn(uint rollingRawCrc, byte incoming) => BitOperations.Crc32C(rollingRawCrc, incoming);
        public uint Roll(uint rollingRawCrc, byte outgoing, byte incoming) => BitOperations.Crc32C(RollOut(rollingRawCrc, outgoing), incoming);

        public uint RollOut(uint rollingRawCrc, ushort outgoing) => _remTbl._RollOut16(rollingRawCrc, outgoing);
        public uint RollIn(uint rollingRawCrc, ushort incoming) => BitOperations.Crc32C(rollingRawCrc, incoming);
        public uint Roll(uint rollingRawCrc, ushort outgoing, ushort incoming) => BitOperations.Crc32C(RollOut(rollingRawCrc, outgoing), incoming);

        public uint RollOut(uint rollingRawCrc, uint outgoing) => _remTbl._RollOut32(rollingRawCrc, (int)outgoing, (int)outgoing >> 16);
        public uint RollIn(uint rollingRawCrc, uint incoming) => BitOperations.Crc32C(rollingRawCrc, incoming);
        public uint Roll(uint rollingRawCrc, uint outgoing, uint incoming) => BitOperations.Crc32C(_remTbl._RollOut32(rollingRawCrc, (int)outgoing, (int)outgoing >> 16), incoming);

        public uint RollOut(uint rollingRawCrc, ulong outgoing) => _remTbl._RollOut64B(rollingRawCrc, (int)outgoing, (int)(outgoing >> 32));
        public uint RollIn(uint rollingRawCrc, ulong incoming) => BitOperations.Crc32C(rollingRawCrc, incoming);
        public uint Roll(uint rollingRawCrc, ulong outgoing, ulong incoming) => BitOperations.Crc32C(_remTbl._RollOut64B(rollingRawCrc, (int)outgoing, (int)(outgoing >> 32)), incoming);

        public uint RawToFinal(uint rollingRawCrc) => rollingRawCrc ^ _initAndFinalEffect;
        public bool Check(uint rollingRawCrc, uint destFinalCrc) => (rollingRawCrc ^ _initAndFinalEffect) == destFinalCrc;
        public bool CheckResidue(uint rollingRawCrc) => (rollingRawCrc ^ _initAndFinalEffect) == FinalResidue;
    }
}
