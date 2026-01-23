using System.Buffers.Binary;
using System.Numerics;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Data.Hashing.Tests;

// TODO: Most tests here use old API (UpdateAndCheck, PayloadOffset, UpdateAndCheckReverse)
// that no longer exists. Need to update to use Roll/RollingCheck API.

public class RollingCrc_ScannerTests {
    [Fact]
    public void Roll_DetectsPayloadWithTrailingCrc32C() {
        const int windowSize = 32;
        const uint initValue = 0xFFFF_FFFFu;
        const uint finalXor = 0xFFFF_FFFFu;
        var table = new RollingCrc.Table(windowSize, initValue, finalXor);
        var ringBuffer = new RollingCrc.Scanner<RollingCrc.Forward>(table);
        var payload = CreateData(windowSize);
        uint crc = Crc32C(payload, initValue) ^ finalXor;

        bool matched = false;
        for (int i = 0; i < payload.Length; i++) {
            if (table.Check(ringBuffer.Roll(payload[i]), crc)) {
                Assert.False(matched, "重复命中应当不发生");
                matched = true;
                Assert.Equal(payload.Length - 1, i);
            }
        }

        Assert.True(matched);
    }

    private static uint Crc32C(ReadOnlySpan<byte> data, uint init) {
        uint crc = init;
        for (int i = 0; i < data.Length; i++) {
            crc = BitOperations.Crc32C(crc, data[i]);
        }
        return crc;
    }

    private static byte[] CreateData(int length) {
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++) {
            data[i] = (byte)(i * 31 + 7);
        }
        return data;
    }
}
