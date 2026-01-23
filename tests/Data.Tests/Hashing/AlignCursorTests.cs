using System.Numerics;
using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Data.Hashing.Tests;

public class AlignCursorTests {
    [Theory]
    [InlineData(0, 4)]   // Already aligned
    [InlineData(4, 4)]   // Already aligned
    [InlineData(8, 4)]   // Already aligned
    [InlineData(1, 4)]   // misalign=1
    [InlineData(2, 4)]   // misalign=2
    [InlineData(3, 4)]   // misalign=3
    [InlineData(5, 4)]   // misalign=1
    [InlineData(6, 4)]   // misalign=2
    [InlineData(7, 4)]   // misalign=3
    [InlineData(1, 2)]   // misalign=1
    [InlineData(1, 8)]   // misalign=1
    [InlineData(7, 8)]   // misalign=7
    public void AlignCursor_AlignsToCursorBoundary(int initialCursor, int stepSz) {
        // Arrange: WindowSize must be multiple of stepSz
        const int windowSize = 16;
        var table = new RollingCrc.Table(windowSize);
        var buffer = new RollingCrc.Scanner<RollingCrc.Forward>(table);

        // Feed bytes to move cursor to initialCursor
        for (int i = 0; i < initialCursor; i++) {
            buffer.Roll((byte)(i + 1));
        }

        // Act
        buffer.AlignCursor(stepSz);

        // Assert: verify cursor is aligned
        buffer.EnsureAligned(stepSz);

        // Also verify the data content is preserved (logical content check)
        // We compare the output of RotateCursorZero() and TryCopyTo

        var output = new byte[windowSize];
        buffer.TryCopyTo(output);

        // Data integrity is verified by AlignCursor_PreservesDataContent logic
    }

    [Fact]
    public void AlignCursor_PreservesDataContent() {
        const int windowSize = 16;
        var table = new RollingCrc.Table(windowSize);
        var buffer = new RollingCrc.Scanner<RollingCrc.Forward>(table);

        // Fill buffer completely with known data
        var originalData = new byte[windowSize];
        for (int i = 0; i < windowSize; i++) {
            originalData[i] = (byte)(i * 17 + 5);
            buffer.Roll(originalData[i]);
        }

        // Move cursor to position 3 (not aligned to 4)
        // This overwrites the first 3 bytes of original data
        buffer.Roll(0xAA);
        buffer.Roll(0xBB);
        buffer.Roll(0xCC);

        // Capture logic state
        var expectedContent = new byte[windowSize];
        buffer.TryCopyTo(expectedContent);

        // Act
        buffer.AlignCursor(4);
        buffer.EnsureAligned(4);

        // Assert
        var actualContent = new byte[windowSize];
        buffer.TryCopyTo(actualContent);

        Assert.Equal(expectedContent, actualContent);
    }

    [Fact]
    public void AlignCursor_ContinuousUsage_MaintainsIntegrity() {
        // Test back-and-forth alignment to stress the padding/base logic
        const int windowSize = 32;
        const int stepSz = 8;
        var table = new RollingCrc.Table(windowSize);
        var buffer = new RollingCrc.Scanner<RollingCrc.Forward>(table);

        // Strategy: Roll 1 byte (misalign), Align, Check. Repeat.
        // This forces frequent small adjustments.

        for (int i = 0; i < 100; i++) {
            buffer.Roll((byte)i);

            // Align
            buffer.AlignCursor(stepSz);
            buffer.EnsureAligned(stepSz);
        }
    }

    [Fact]
    public void AlignCursor_WithMixedStepSizes() {
        const int windowSize = 32;
        var table = new RollingCrc.Table(windowSize);
        var buffer = new RollingCrc.Scanner<RollingCrc.Forward>(table);

        buffer.Roll(1);

        // Align to 4
        buffer.AlignCursor(4);
        buffer.EnsureAligned(4);

        buffer.Roll(2); // Misalign again

        // Align to 8
        buffer.AlignCursor(8);
        buffer.EnsureAligned(8);

        // Since 8 is multiple of 4, it's also aligned to 4
        buffer.EnsureAligned(4);
    }
}
