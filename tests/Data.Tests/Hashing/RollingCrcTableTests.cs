using System.Buffers.Binary;
using System.Numerics;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Data.Hashing.Tests;

public class RollingCrc_TableTests {
    [Fact]
    public void RemoveOutgoingByte_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);

        uint crc = Crc32C(data, 0u);
        uint expected = Crc32C(data.AsSpan(1), 0u);
        uint actual = rolling.RollOut(crc, data[0]);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoveOutgoingUShort_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);

        uint crc = Crc32C(data, 0u);
        ushort outgoing = (ushort)(data[0] | (data[1] << 8));
        uint expected = Crc32C(data.AsSpan(2), 0u);
        uint actual = rolling.RollOut(crc, outgoing);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoveOutgoingUInt_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);

        uint crc = Crc32C(data, 0u);
        uint outgoing = ReadUInt32LE(data);
        uint expected = Crc32C(data.AsSpan(4), 0u);
        uint actual = rolling.RollOut(crc, outgoing);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoveOutgoingULong_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);

        uint crc = Crc32C(data, 0u);
        ulong outgoing = ReadUInt64LE(data);
        uint expected = Crc32C(data.AsSpan(8), 0u);
        uint actual = rolling.RollOut(crc, outgoing);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RollAcrossWindow_MatchesRecompute() {
        const int windowSize = 16;
        const int steps = 8;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize + steps);

        uint crc = Crc32C(data.AsSpan(0, windowSize), 0u);
        for (int i = 0; i < steps; i++) {
            crc = rolling.Roll(crc, data[i], data[i + windowSize]);
            uint expected = Crc32C(data.AsSpan(i + 1, windowSize), 0u);
            Assert.Equal(expected, crc);
        }
    }

    [Fact]
    public void RollAcrossWindow_Ulong_MatchesRecompute() {
        const int windowSize = 24;
        const int steps = 3;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize + steps * sizeof(ulong));
        data[7] = 0x80;
        data[15] = 0xFE;
        data[23] = 0x81;

        uint crc = Crc32C(data.AsSpan(0, windowSize), 0u);
        for (int i = 0; i < steps; i++) {
            int offset = i * sizeof(ulong);
            ulong outgoing = ReadUInt64LE(data.AsSpan(offset, sizeof(ulong)));
            ulong incoming = ReadUInt64LE(data.AsSpan(offset + windowSize, sizeof(ulong)));
            crc = rolling.Roll(crc, outgoing, incoming);
            uint expected = Crc32C(data.AsSpan(offset + sizeof(ulong), windowSize), 0u);
            Assert.Equal(expected, crc);
        }
    }

    [Fact]
    public void RemoveOutgoingByte_HighBitSet_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);
        data[0] = 0x80;

        uint crc = Crc32C(data, 0u);
        uint expected = Crc32C(data.AsSpan(1), 0u);
        uint actual = rolling.RollOut(crc, data[0]);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoveOutgoingUShort_HighBitSet_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);
        data[0] = 0x11;
        data[1] = 0x80;

        uint crc = Crc32C(data, 0u);
        ushort outgoing = ReadUInt16LE(data);
        uint expected = Crc32C(data.AsSpan(2), 0u);
        uint actual = rolling.RollOut(crc, outgoing);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoveOutgoingUInt_HighBitSet_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);
        data[0] = 0x11;
        data[1] = 0x22;
        data[2] = 0x33;
        data[3] = 0x80;

        uint crc = Crc32C(data, 0u);
        uint outgoing = ReadUInt32LE(data);
        uint expected = Crc32C(data.AsSpan(4), 0u);
        uint actual = rolling.RollOut(crc, outgoing);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoveOutgoingULong_HighBitSet_MatchesRecompute() {
        const int windowSize = 16;
        var rolling = new RollingCrc.Table(windowSize, 0u, 0u);
        var data = CreateData(windowSize);
        data[0] = 0x10;
        data[1] = 0x20;
        data[2] = 0x30;
        data[3] = 0x40;
        data[4] = 0x50;
        data[5] = 0x60;
        data[6] = 0x70;
        data[7] = 0x80;

        uint crc = Crc32C(data, 0u);
        ulong outgoing = ReadUInt64LE(data);
        uint expected = Crc32C(data.AsSpan(8), 0u);
        uint actual = rolling.RollOut(crc, outgoing);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Check_WithInitAndFinalXor_MatchesRollingRaw() {
        const int windowSize = 16;
        const uint initValue = 0xFFFF_FFFFu;
        const uint finalXor = 0xFFFF_FFFFu;
        var rolling = new RollingCrc.Table(windowSize, initValue, finalXor);
        var data = CreateData(windowSize);

        uint rollingCrc = Crc32C(data, 0u);
        uint destCrc = Crc32C(data, initValue) ^ finalXor;

        Assert.True(rolling.Check(rollingCrc, destCrc));
    }

    private static uint Crc32C(ReadOnlySpan<byte> data, uint init) {
        uint crc = init;
        for (int i = 0; i < data.Length; i++) {
            crc = BitOperations.Crc32C(crc, data[i]);
        }
        return crc;
    }

    private static ushort ReadUInt16LE(ReadOnlySpan<byte> data) => (ushort)(data[0] | (data[1] << 8));

    private static uint ReadUInt32LE(ReadOnlySpan<byte> data)
        => (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

    private static ulong ReadUInt64LE(ReadOnlySpan<byte> data)
        => (ulong)data[0]
            | ((ulong)data[1] << 8)
            | ((ulong)data[2] << 16)
            | ((ulong)data[3] << 24)
            | ((ulong)data[4] << 32)
            | ((ulong)data[5] << 40)
            | ((ulong)data[6] << 48)
            | ((ulong)data[7] << 56);

    private static byte[] CreateData(int length) {
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++) {
            data[i] = (byte)(i * 31 + 7);
        }
        return data;
    }
}
