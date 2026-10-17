using Xunit;

namespace Atelia.Data.Tests;

public class ZeroPreservingXor32Tests {
    [Theory]
    [InlineData(0u, 0xffffffffu, 0u)]
    [InlineData(0x12345678u, 0u, 0x12345678u)]
    [InlineData(0x12345678u, 0x12345678u, 0x12345678u)]
    [InlineData(1u, 0xffffffffu, 0xfffffffeu)]
    [InlineData(0xffffffffu, 0xffffffffu, 0xffffffffu)]
    [InlineData(0xffffffffu, 0x80000000u, 0x7fffffffu)]
    [InlineData(0x12345678u, 0xa5a5a5a5u, 0xb791f3ddu)]
    public void FixedVectorsAreStableAndReversible(uint value, uint key, uint encoded) {
        Assert.Equal(encoded, ZeroPreservingXor32.Apply(value, key));
        Assert.Equal(value, ZeroPreservingXor32.Apply(encoded, key));
    }

    [Fact]
    public void EightBitSubdomainExhaustivelyPermutesNonzeroValuesAndFixesZero() {
        for (uint key = 0; key < 256; key++) {
            var seen = new bool[256];
            for (uint value = 0; value < 256; value++) {
                uint encoded = ZeroPreservingXor32.Apply(value, key);
                Assert.InRange(encoded, 0u, 255u);
                Assert.False(seen[encoded]);
                seen[encoded] = true;
                Assert.Equal(value == 0, encoded == 0);
                Assert.Equal(value, ZeroPreservingXor32.Apply(encoded, key));
            }
            Assert.All(seen, value => Assert.True(value));
        }
    }

    [Fact]
    public void EightBitKeyHistogramCountsSelfTwiceAndEveryOtherNonzeroOnce() {
        for (uint value = 1; value < 256; value++) {
            var counts = new int[256];
            for (uint key = 0; key < 256; key++) {
                counts[ZeroPreservingXor32.Apply(value, key)]++;
            }
            Assert.Equal(0, counts[0]);
            for (uint result = 1; result < 256; result++) {
                Assert.Equal(result == value ? 2 : 1, counts[result]);
            }
        }
    }

    [Fact]
    public void LowDomainImagesHaveKeyPrefixIncludingZeroAndLowKeys() {
        // Exhaust all 8bit keys and nonzero 4bit IDs, including both special fixed points.
        for (uint key = 0; key < 256; key++) {
            for (uint value = 1; value < 16; value++) {
                Assert.Equal(key >> 4, ZeroPreservingXor32.Apply(value, key) >> 4);
            }
        }
    }

    [Theory]
    [InlineData(0u, 0x01000000u)]
    [InlineData(1u, 0xff000001u)]
    [InlineData(0x00ffffffu, 0xffffffffu)]
    [InlineData(0x12345678u, 0x13345678u)]
    public void DifferentHighBytesRejectTwentyFourBitIdsIncludingBoundaryValues(uint sourceKey, uint wrongKey) {
        uint[] values = [1, 2, 0x7fffff, 0x800000, 0xfffffe, 0xffffff];
        foreach (uint value in values) {
            uint encoded = ZeroPreservingXor32.Apply(value, sourceKey);
            uint decoded = ZeroPreservingXor32.Apply(encoded, wrongKey);
            Assert.True(decoded > 0xffffff);
        }
    }

    [Fact]
    public void NearbyKeysCanMisdirectManyLowIdsRatherThanGuaranteeDetection() {
        const uint sourceKey = 0x12345678;
        const uint wrongKey = 0x12345679;
        for (uint value = 2; value < 1000; value++) {
            uint decoded = ZeroPreservingXor32.Apply(ZeroPreservingXor32.Apply(value, sourceKey), wrongKey);
            Assert.Equal(value ^ 1u, decoded);
            Assert.InRange(decoded, 1u, 999u);
        }
    }
}
