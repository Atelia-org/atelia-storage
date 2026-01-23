using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Data.Hashing.Tests;

public class RollingCrcCodewordTests {

    #region D1: Forward Codeword (SealCodewordForward + CheckCodewordForward)

    [Fact]
    public void SealCodewordForward_WritesCorrectCrcAtEnd() {
        const int payloadSize = 12;
        var codeword = new byte[payloadSize + sizeof(uint)];
        FillPayload(codeword.AsSpan(0, payloadSize));

        uint sealedCrc = RollingCrc.SealCodewordForward(codeword);

        // Verify CRC is written at end in LE format
        uint readCrc = BinaryPrimitives.ReadUInt32LittleEndian(codeword.AsSpan(payloadSize));
        Assert.Equal(sealedCrc, readCrc);

        // P0-1 fix: use independent reference implementation
        // RollingCrc uses init=0xFFFFFFFF, finalXor=0xFFFFFFFF (standard CRC-32C params)
        // Verify by computing CRC over the payload byte-by-byte
        uint expectedCrc = Crc32CByteByByte(codeword.AsSpan(0, payloadSize), RollingCrc.DefaultInitValue) ^ RollingCrc.DefaultFinalXor;
        Assert.Equal(expectedCrc, sealedCrc);
    }

    /// <summary>
    /// Verify that CrcForward produces the same result as byte-by-byte computation.
    /// This ensures our independent reference is truly equivalent.
    /// </summary>
    [Fact]
    public void CrcForward_EqualsIndependentByteByByte() {
        const int payloadSize = 12;
        var payload = new byte[payloadSize];
        FillPayload(payload);

        // Using RollingCrc.CrcForward
        uint crcForward = RollingCrc.CrcForward(payload, RollingCrc.DefaultInitValue, RollingCrc.DefaultFinalXor);

        // Using independent byte-by-byte
        uint crcByteByByte = Crc32CByteByByte(payload, RollingCrc.DefaultInitValue) ^ RollingCrc.DefaultFinalXor;

        Assert.Equal(crcByteByByte, crcForward);
    }

    [Fact]
    public void CheckCodewordForward_ReturnsTrueForValidCodeword() {
        const int payloadSize = 12;
        var codeword = new byte[payloadSize + sizeof(uint)];
        FillPayload(codeword.AsSpan(0, payloadSize));
        RollingCrc.SealCodewordForward(codeword);

        bool valid = RollingCrc.CheckCodewordForward(codeword);

        Assert.True(valid);
    }

    [Fact]
    public void CheckCodewordForward_ReturnsFalseForCorruptedPayload() {
        const int payloadSize = 12;
        var codeword = new byte[payloadSize + sizeof(uint)];
        FillPayload(codeword.AsSpan(0, payloadSize));
        RollingCrc.SealCodewordForward(codeword);

        // Corrupt the payload
        codeword[payloadSize / 2] ^= 0xFF;

        bool valid = RollingCrc.CheckCodewordForward(codeword);

        Assert.False(valid);
    }

    [Fact]
    public void CheckCodewordForward_ReturnsFalseForCorruptedCrc() {
        const int payloadSize = 12;
        var codeword = new byte[payloadSize + sizeof(uint)];
        FillPayload(codeword.AsSpan(0, payloadSize));
        RollingCrc.SealCodewordForward(codeword);

        // Corrupt the CRC
        codeword[payloadSize] ^= 0x01;

        bool valid = RollingCrc.CheckCodewordForward(codeword);

        Assert.False(valid);
    }

    #endregion

    #region D2: Backward Codeword (SealCodewordBackward + CheckCodewordBackward)

    [Fact]
    public void SealCodewordBackward_WritesCorrectCrcAtStart() {
        const int payloadSize = 12;
        var codeword = new byte[sizeof(uint) + payloadSize];
        FillPayload(codeword.AsSpan(sizeof(uint), payloadSize));

        uint sealedCrc = RollingCrc.SealCodewordBackward(codeword);

        // Verify CRC is written at start in BE format
        uint readCrc = BinaryPrimitives.ReadUInt32BigEndian(codeword);
        Assert.Equal(sealedCrc, readCrc);

        // P0-1 fix: use independent reference implementation
        // CrcBackward processes payload from end to start, reading as BE integers.
        // For byte-by-byte verification, we need to compute CRC in same order as CrcBackward.
        var payload = codeword.AsSpan(sizeof(uint));
        uint expectedCrc = Crc32CBackwardByteByByte(payload, RollingCrc.DefaultInitValue) ^ RollingCrc.DefaultFinalXor;
        Assert.Equal(expectedCrc, sealedCrc);
    }

    /// <summary>
    /// Verify that CrcBackward produces the same result as byte-by-byte backward computation.
    /// </summary>
    [Fact]
    public void CrcBackward_EqualsIndependentByteByByte() {
        const int payloadSize = 12;
        var payload = new byte[payloadSize];
        FillPayload(payload);

        // Using RollingCrc.CrcBackward
        uint crcBackward = RollingCrc.CrcBackward(payload, RollingCrc.DefaultInitValue, RollingCrc.DefaultFinalXor);

        // Using independent byte-by-byte backward
        uint crcByteByByte = Crc32CBackwardByteByByte(payload, RollingCrc.DefaultInitValue) ^ RollingCrc.DefaultFinalXor;

        Assert.Equal(crcByteByByte, crcBackward);
    }

    [Fact]
    public void CheckCodewordBackward_ReturnsTrueForValidCodeword() {
        const int payloadSize = 12;
        var codeword = new byte[sizeof(uint) + payloadSize];
        FillPayload(codeword.AsSpan(sizeof(uint), payloadSize));
        RollingCrc.SealCodewordBackward(codeword);

        bool valid = RollingCrc.CheckCodewordBackward(codeword);

        Assert.True(valid);
    }

    [Fact]
    public void CheckCodewordBackward_ReturnsFalseForCorruptedPayload() {
        const int payloadSize = 12;
        var codeword = new byte[sizeof(uint) + payloadSize];
        FillPayload(codeword.AsSpan(sizeof(uint), payloadSize));
        RollingCrc.SealCodewordBackward(codeword);

        // Corrupt the payload
        codeword[sizeof(uint) + payloadSize / 2] ^= 0xFF;

        bool valid = RollingCrc.CheckCodewordBackward(codeword);

        Assert.False(valid);
    }

    [Fact]
    public void CheckCodewordBackward_ReturnsFalseForCorruptedCrc() {
        const int payloadSize = 12;
        var codeword = new byte[sizeof(uint) + payloadSize];
        FillPayload(codeword.AsSpan(sizeof(uint), payloadSize));
        RollingCrc.SealCodewordBackward(codeword);

        // Corrupt the CRC (at start, BE)
        codeword[0] ^= 0x01;

        bool valid = RollingCrc.CheckCodewordBackward(codeword);

        Assert.False(valid);
    }

    #endregion

    #region D3: Forward-Backward Symmetry (监护人特别强调)

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void ForwardCodeword_ReversedBytes_PassesBackwardCheck(int payloadSize) {
        // Create and seal a forward codeword: [payload...][CRC-LE]
        var forwardCodeword = new byte[payloadSize + sizeof(uint)];
        FillPayload(forwardCodeword.AsSpan(0, payloadSize));
        RollingCrc.SealCodewordForward(forwardCodeword);

        // Reverse the entire codeword bytes
        // This transforms [payload...][CRC-LE] -> [CRC-BE][payload-reversed...]
        var reversedCodeword = forwardCodeword.ToArray();
        Array.Reverse(reversedCodeword);

        // The reversed codeword should pass backward check
        bool valid = RollingCrc.CheckCodewordBackward(reversedCodeword);

        Assert.True(valid, $"Forward codeword (size={payloadSize + 4}) reversed should pass backward check");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void BackwardCodeword_ReversedBytes_PassesForwardCheck(int payloadSize) {
        // Create and seal a backward codeword: [CRC-BE][payload...]
        var backwardCodeword = new byte[sizeof(uint) + payloadSize];
        FillPayload(backwardCodeword.AsSpan(sizeof(uint), payloadSize));
        RollingCrc.SealCodewordBackward(backwardCodeword);

        // Reverse the entire codeword bytes
        // This transforms [CRC-BE][payload...] -> [payload-reversed...][CRC-LE]
        var reversedCodeword = backwardCodeword.ToArray();
        Array.Reverse(reversedCodeword);

        // The reversed codeword should pass forward check
        bool valid = RollingCrc.CheckCodewordForward(reversedCodeword);

        Assert.True(valid, $"Backward codeword (size={payloadSize + 4}) reversed should pass forward check");
    }

    #endregion

    #region D4: Granularity Equivalence (byte-by-byte == uint-by-uint when aligned)

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    public void ByteByByte_EqualsUintByUint_WhenAligned(int windowSize) {
        // Require windowSize to be multiple of 4 for uint alignment
        Assert.Equal(0, windowSize % sizeof(uint));

        var table = RollingCrc.SharedTable(windowSize);
        var data = CreateData(windowSize * 2);

        // Compute rolling CRC byte-by-byte
        uint rollingByByte = RollingCrc.EmptyRollingRaw;
        for (int i = 0; i < windowSize; i++) {
            rollingByByte = table.RollIn(rollingByByte, data[i]);
        }
        for (int i = 0; i < windowSize; i++) {
            rollingByByte = table.Roll(rollingByByte, data[i], data[i + windowSize]);
        }

        // Compute rolling CRC uint-by-uint
        uint rollingByUint = RollingCrc.EmptyRollingRaw;
        var dataAsUint = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
        int uintWindowSize = windowSize / sizeof(uint);
        for (int i = 0; i < uintWindowSize; i++) {
            rollingByUint = table.RollIn(rollingByUint, dataAsUint[i]);
        }
        for (int i = 0; i < uintWindowSize; i++) {
            rollingByUint = table.Roll(rollingByUint, dataAsUint[i], dataAsUint[i + uintWindowSize]);
        }

        Assert.Equal(rollingByByte, rollingByUint);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    public void ByteByByte_EqualsUshortByUshort_WhenAligned(int windowSize) {
        // Require windowSize to be multiple of 2 for ushort alignment
        Assert.Equal(0, windowSize % sizeof(ushort));

        var table = RollingCrc.SharedTable(windowSize);
        var data = CreateData(windowSize * 2);

        // Compute rolling CRC byte-by-byte
        uint rollingByByte = RollingCrc.EmptyRollingRaw;
        for (int i = 0; i < windowSize; i++) {
            rollingByByte = table.RollIn(rollingByByte, data[i]);
        }
        for (int i = 0; i < windowSize; i++) {
            rollingByByte = table.Roll(rollingByByte, data[i], data[i + windowSize]);
        }

        // Compute rolling CRC ushort-by-ushort
        uint rollingByUshort = RollingCrc.EmptyRollingRaw;
        var dataAsUshort = MemoryMarshal.Cast<byte, ushort>(data.AsSpan());
        int ushortWindowSize = windowSize / sizeof(ushort);
        for (int i = 0; i < ushortWindowSize; i++) {
            rollingByUshort = table.RollIn(rollingByUshort, dataAsUshort[i]);
        }
        for (int i = 0; i < ushortWindowSize; i++) {
            rollingByUshort = table.Roll(rollingByUshort, dataAsUshort[i], dataAsUshort[i + ushortWindowSize]);
        }

        Assert.Equal(rollingByByte, rollingByUshort);
    }

    #endregion

    #region D5: Forward Scanner

    [Fact]
    public void ForwardScanner_TryFindCodeword_FindsValidCodewordInStream() {
        const int payloadSize = 12;
        const int prefixJunk = 10;
        const int suffixJunk = 10;
        int codewordSize = payloadSize + sizeof(uint);

        // Build stream: [junk...][codeword][junk...]
        var stream = new byte[prefixJunk + codewordSize + suffixJunk];
        FillPayload(stream); // fill all with junk first

        // Plant a valid forward codeword at offset prefixJunk
        var codewordSpan = stream.AsSpan(prefixJunk, codewordSize);
        FillPayload(codewordSpan[..payloadSize]);
        RollingCrc.SealCodewordForward(codewordSpan);

        var scanner = RollingCrc.ForwardScanner(codewordSize);
        bool found = scanner.TryFindCodeword(stream.AsSpan(), out var match);

        Assert.True(found);
        Assert.Equal(prefixJunk + codewordSize, scanner.Processed);
    }

    [Fact]
    public void ForwardScanner_TryFindCodeword_ReturnsCorrectPayloadAndCrc() {
        const int payloadSize = 12;
        int codewordSize = payloadSize + sizeof(uint);

        // Create exact codeword (no extra junk)
        var codeword = new byte[codewordSize];
        FillPayload(codeword.AsSpan(0, payloadSize));
        uint expectedCrc = RollingCrc.SealCodewordForward(codeword);

        var scanner = RollingCrc.ForwardScanner(codewordSize);
        bool found = scanner.TryFindCodeword(codeword.AsSpan(), out var match);

        Assert.True(found);
        Assert.Equal(payloadSize, match.Payload.Length);
        Assert.Equal(expectedCrc, match.FinalCrc);
        Assert.False(match.IsBackward);

        // Verify payload content matches
        Assert.True(match.Payload.SequenceEqual(codeword.AsSpan(0, payloadSize)));
    }

    [Fact]
    public void ForwardScanner_TryFindCodeword_ByteSpan_EqualsUintSpan_WhenAligned() {
        const int payloadSize = 12; // Must be multiple of 4
        const int prefixJunk = 8;   // Must be multiple of 4
        int codewordSize = payloadSize + sizeof(uint);

        // Build stream: [junk aligned to 4][codeword][junk...]
        var stream = new byte[prefixJunk + codewordSize + 8];
        FillPayload(stream);

        var codewordSpan = stream.AsSpan(prefixJunk, codewordSize);
        FillPayload(codewordSpan[..payloadSize]);
        RollingCrc.SealCodewordForward(codewordSpan);

        // Find with byte span
        var scannerByte = RollingCrc.ForwardScanner(codewordSize);
        scannerByte.TryFindCodeword(stream.AsSpan(), out var matchByte);

        // Find with uint span
        var scannerUint = RollingCrc.ForwardScanner(codewordSize);
        var streamAsUint = MemoryMarshal.Cast<byte, uint>(stream.AsSpan());
        scannerUint.TryFindCodeword(streamAsUint, out var matchUint);

        // Both should find at same processed position (in bytes)
        Assert.Equal(matchByte.Processed, matchUint.Processed);
        Assert.Equal(matchByte.FinalCrc, matchUint.FinalCrc);
    }

    [Fact]
    public void ForwardScanner_TryFindCodeword_NoMatch_ReturnsFalse() {
        // P0-2 fix: deterministic no-match test
        // Use exactly one window (stream.Length == codewordSize) to avoid collision probability
        const int payloadSize = 12;
        int codewordSize = payloadSize + sizeof(uint);

        // Create a valid codeword first
        var codeword = new byte[codewordSize];
        FillPayload(codeword.AsSpan(0, payloadSize));
        RollingCrc.SealCodewordForward(codeword);

        // Corrupt one byte of the payload to make it invalid
        codeword[payloadSize / 2] ^= 0xFF;

        // Since there's only one window, scanner must return false
        var scanner = RollingCrc.ForwardScanner(codewordSize);
        bool found = scanner.TryFindCodeword(codeword.AsSpan(), out _);

        Assert.False(found);
    }

    #endregion

    #region D6: Backward Scanner

    [Fact]
    public void BackwardScanner_TryFindCodeword_FindsValidCodewordInReversedStream() {
        const int payloadSize = 12;
        const int prefixJunk = 10;
        const int suffixJunk = 10;
        int codewordSize = payloadSize + sizeof(uint);

        // Build original stream with a forward codeword: [junk...][forward-codeword][junk...]
        var originalStream = new byte[prefixJunk + codewordSize + suffixJunk];
        FillPayload(originalStream);

        var codewordSpan = originalStream.AsSpan(prefixJunk, codewordSize);
        FillPayload(codewordSpan[..payloadSize]);
        RollingCrc.SealCodewordForward(codewordSpan);

        // Reverse the entire stream to simulate backward reading
        var reversedStream = originalStream.ToArray();
        Array.Reverse(reversedStream);

        // BackwardScanner should find the reversed forward codeword
        var scanner = RollingCrc.BackwardScanner(codewordSize);
        bool found = scanner.TryFindCodeword(reversedStream.AsSpan(), out var match);

        Assert.True(found);
        Assert.True(match.IsBackward);
        // The codeword is found after scanning past the suffix junk (which is now prefix)
        Assert.Equal(suffixJunk + codewordSize, scanner.Processed);
    }

    [Fact]
    public void BackwardScanner_TryFindCodeword_ReturnsCorrectPayloadAndCrc() {
        const int payloadSize = 12;
        int codewordSize = sizeof(uint) + payloadSize;

        // BackwardScanner is designed to find FORWARD codewords that have been byte-reversed.
        // Create a forward codeword, reverse it, and feed to backward scanner.
        var forwardCodeword = new byte[codewordSize];
        FillPayload(forwardCodeword.AsSpan(0, payloadSize));
        uint forwardCrc = RollingCrc.SealCodewordForward(forwardCodeword);

        // Reverse the forward codeword
        var reversedCodeword = forwardCodeword.ToArray();
        Array.Reverse(reversedCodeword);

        var scanner = RollingCrc.BackwardScanner(codewordSize);
        bool found = scanner.TryFindCodeword(reversedCodeword.AsSpan(), out var match);

        Assert.True(found);
        Assert.Equal(payloadSize, match.Payload.Length);
        Assert.True(match.IsBackward);
        Assert.Equal(codewordSize, scanner.Processed);

        // P0-3 fix: verify FinalCrc matches the CRC read from the codeword view.
        // The Codeword property returns the scanner's internal buffer view.
        // FinalCrc for backward mode reads first 4 bytes as BE.
        // Verify this is consistent with how the CRC bytes are stored.
        uint codewordCrc = BinaryPrimitives.ReadUInt32BigEndian(match.Codeword);
        Assert.Equal(codewordCrc, match.FinalCrc);

        // Also verify the Codeword has correct length
        Assert.Equal(codewordSize, match.Codeword.Length);
    }

    [Fact]
    public void BackwardScanner_TryFindCodeword_NoMatch_ReturnsFalse() {
        // P0-2 fix: deterministic no-match test
        // Use exactly one window (stream.Length == codewordSize) to avoid collision probability
        const int payloadSize = 12;
        int codewordSize = payloadSize + sizeof(uint);

        // Create a valid forward codeword and reverse it (valid for backward scanner)
        var forwardCodeword = new byte[codewordSize];
        FillPayload(forwardCodeword.AsSpan(0, payloadSize));
        RollingCrc.SealCodewordForward(forwardCodeword);

        var reversedCodeword = forwardCodeword.ToArray();
        Array.Reverse(reversedCodeword);

        // Corrupt one byte of the CRC region (now at end after reversal) to make it invalid
        reversedCodeword[codewordSize - 2] ^= 0xFF;

        // Since there's only one window, scanner must return false
        var scanner = RollingCrc.BackwardScanner(codewordSize);
        bool found = scanner.TryFindCodeword(reversedCodeword.AsSpan(), out _);

        Assert.False(found);
    }

    #endregion

    #region Helper Methods

    private static void FillPayload(Span<byte> buffer) {
        for (int i = 0; i < buffer.Length; i++) {
            buffer[i] = (byte)(i * 31 + 7);
        }
    }

    private static byte[] CreateData(int length) {
        var data = new byte[length];
        FillPayload(data);
        return data;
    }

    /// <summary>
    /// Independent CRC32C reference implementation using BitOperations.Crc32C byte-by-byte.
    /// This is NOT using RollingCrc, so it can serve as an oracle for testing.
    /// </summary>
    private static uint Crc32CByteByByte(ReadOnlySpan<byte> data, uint init) {
        uint crc = init;
        for (int i = 0; i < data.Length; i++) {
            crc = BitOperations.Crc32C(crc, data[i]);
        }
        return crc;
    }

    /// <summary>
    /// Independent backward CRC32C reference implementation.
    /// Processes bytes from end to start, same order as RollingCrc.CrcBackward.
    /// </summary>
    private static uint Crc32CBackwardByteByByte(ReadOnlySpan<byte> data, uint init) {
        uint crc = init;
        for (int i = data.Length - 1; i >= 0; i--) {
            crc = BitOperations.Crc32C(crc, data[i]);
        }
        return crc;
    }

    #endregion
}
