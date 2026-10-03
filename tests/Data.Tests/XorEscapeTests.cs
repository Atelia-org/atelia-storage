using Atelia.Data.Binary;
using Xunit;

namespace Atelia.Data.Tests;

public class XorEscapeTests {
    private const uint Fence = 0x32464252u;
    private static readonly uint[] Fences = [0x04000000u, 0x04000001u, Fence, uint.MaxValue];

    [Fact]
    public void SelectKey_EmptyBodyValidatesFenceThenReturnsZero() {
        foreach (uint fence in Fences) { Assert.Equal(0u, XorEscape.SelectKey(fence, [])); }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(63u)]
    [InlineData(64u)]
    [InlineData(0x03ffffffu)]
    public void SelectKey_FenceBelowMinimumRejectsEmptyAndNonemptyBody(uint fence) {
        byte[] input = [1, 2, 3, 4];
        byte[] before = (byte[])input.Clone();
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.SelectKey(fence, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.SelectKey(fence, input));
        Assert.Equal(before, input);
    }

    [Fact]
    public void SelectKey_ValidatesCombinedLengthRatherThanEachSpan() {
        byte[] input = [1, 2, 3, 4];
        Assert.Equal(0u, XorEscape.SelectKey(Fence, input.AsSpan(0, 1), input.AsSpan(1, 2), input.AsSpan(3)));
        Assert.Throws<ArgumentException>(() => XorEscape.SelectKey(Fence, input.AsSpan(0, 1), [], input.AsSpan(2)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, input);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue)]
    public void ValidateByteLength_RejectsLongValuesOutsideIntDomainWithoutLargeBuffers(long byteLength) {
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.ValidateByteLength(byteLength));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    [InlineData(2147483647L)]
    public void ValidateByteLength_RejectsIncompleteWords(long byteLength) {
        Assert.Throws<ArgumentException>(() => XorEscape.ValidateByteLength(byteLength));
    }

    [Fact]
    public void ValidateByteLength_AcceptsLargestAlignedInt() {
        Assert.Equal(0, XorEscape.ValidateByteLength(0));
        Assert.Equal(int.MaxValue & ~3, XorEscape.ValidateByteLength(int.MaxValue & ~3));
    }

    [Fact]
    public void SelectKey_ZeroPathNeverDrawsRandomOrModifiesSource() {
        byte[] input = MakeForbiddenWords(Fence, [1u, 2u, 64u, 65u]);
        byte[] before = (byte[])input.Clone();
        var source = new ArraySource([input.AsSpan(0, 1).ToArray(), [], input.AsSpan(1).ToArray()]);
        uint key = XorEscape.SelectKey(Fence, input.Length, source,
            () => throw new InvalidOperationException("The zero path must not request randomness."));
        Assert.Equal(0u, key);
        Assert.Equal(0, source.Index);
        Assert.Equal(before, input);
        AssertMarkerFree(input, key, Fence);
    }

    [Fact]
    public void SelectKey_TinyKeyIsIndependentOfEveryThreeSpanPartition() {
        foreach (uint fence in Fences) {
            byte[] input = MakeForbiddenWords(fence, [0u, 1u, 2u, 5u, 9u, 64u, 0x80000000u]);
            byte[] before = (byte[])input.Clone();
            uint expected = ScalarTinyKey(input, fence);
            for (int firstEnd = 0; firstEnd <= input.Length; firstEnd++) {
                for (int secondEnd = firstEnd; secondEnd <= input.Length; secondEnd++) {
                    uint actual = XorEscape.SelectKey(fence, input.AsSpan(0, firstEnd),
                        input.AsSpan(firstEnd, secondEnd - firstEnd), input.AsSpan(secondEnd));
                    Assert.Equal(expected, actual);
                    AssertMarkerFree(input, actual, fence);
                }
            }
            Assert.Equal(before, input);
        }
    }

    [Fact]
    public void SelectKey_TinyIgnoresCandidatesWhoseShiftWouldAliasLowerBits() {
        byte[] input = MakeForbiddenWords(Fence, [0u, 64u, 65u, 129u, uint.MaxValue]);
        Assert.Equal(1u, XorEscape.SelectKey(Fence, input));
    }

    [Fact]
    public void SelectKey_63WordsCanForbidZeroThrough62WithoutRandomFallback() {
        foreach (uint fence in Fences) {
            byte[] input = MakeForbiddenWords(fence, Enumerable.Range(0, 63).Select(i => (uint)i).ToArray());
            var source = new ArraySource(SplitIntoSingleBytesAndEmptyChunks(input));
            uint key = XorEscape.SelectKey(fence, input.Length, source,
                () => throw new InvalidOperationException("The tiny path must not request randomness."));
            Assert.Equal(63u, key);
            AssertMarkerFree(input, key, fence);
            Assert.Equal(63u, XorEscape.SelectKey(fence, input.AsSpan(0, 1), input.AsSpan(1, 127), input.AsSpan(128)));
        }
    }

    [Fact]
    public void SelectKey_64WordsSwitchToRandomSearch() {
        byte[] input = MakeForbiddenWords(Fence, Enumerable.Range(0, 64).Select(i => (uint)i).ToArray());
        int draws = 0;
        uint key = XorEscape.SelectKey(Fence, input.Length, new ArraySource([input]), () => {
            draws++;
            return 64u;
        });
        Assert.Equal(64u, key);
        Assert.Equal(1, draws);
        AssertMarkerFree(input, key, Fence);
    }

    [Fact]
    public void SelectKey_RandomRejectsZeroFenceAndSuccessiveForbiddenKeysThenAcceptsHighBitKey() {
        foreach (uint fence in Fences) {
            uint[] forbidden = Enumerable.Range(0, 64).Select(i => (uint)(i % 3)).ToArray();
            forbidden[^1] = 0x80000000u;
            byte[] input = MakeForbiddenWords(fence, forbidden);
            byte[] before = (byte[])input.Clone();
            uint[] draws = [0u, fence, 1u, 2u, 0x80000000u, 0xf1234567u];
            int index = 0;
            var source = new ArraySource(SplitIntoSingleBytesAndEmptyChunks(input));
            uint key = XorEscape.SelectKey(fence, input.Length, source, () => draws[index++]);
            Assert.Equal(0xf1234567u, key);
            Assert.Equal(draws.Length, index);
            Assert.Equal(0, source.Index);
            Assert.Equal(before, input);
            AssertMarkerFree(input, key, fence);
        }
    }

    [Fact]
    public void SelectKey_RandomFailurePropagatesSameExceptionWithoutChangingInput() {
        byte[] input = MakeForbiddenWords(Fence, Enumerable.Repeat(0u, 64).ToArray());
        byte[] before = (byte[])input.Clone();
        var failure = new InvalidOperationException("Injected random-source failure.");
        var source = new ArraySource([input.AsSpan(0, 3).ToArray(), [], input.AsSpan(3).ToArray()]);
        Exception actual = Assert.Throws<InvalidOperationException>(
            () => XorEscape.SelectKey(Fence, input.Length, source, () => throw failure));
        Assert.Same(failure, actual);
        Assert.Equal(before, input);
        Assert.Equal(0, source.Index);
    }

    [Fact]
    public void SelectKey_SystemRandomPathProducesMarkerFreeBody() {
        foreach (uint fence in Fences) {
            byte[] input = MakeForbiddenWords(fence, Enumerable.Range(0, 64).Select(i => (uint)i).ToArray());
            byte[] before = (byte[])input.Clone();
            uint key = XorEscape.SelectKey(fence, input.AsSpan(0, 1), input.AsSpan(1, 127), input.AsSpan(128));
            Assert.True(key >= 64);
            AssertMarkerFree(input, key, fence);
            Assert.Equal(before, input);
        }
    }

    [Fact]
    public void SelectKey_TinyMatchesIndependentScalarOracleOnVariedInputs() {
        var random = new Random(73809);
        foreach (uint fence in Fences) {
            for (int wordCount = 0; wordCount <= 63; wordCount++) {
                byte[] input = new byte[wordCount * 4];
                random.NextBytes(input);
                if (wordCount != 0 && (wordCount & 1) == 0) {
                    WriteWord(input.AsSpan((wordCount - 1) * 4), fence);
                }
                int firstEnd = random.Next(input.Length + 1);
                int secondEnd = random.Next(firstEnd, input.Length + 1);
                uint key = XorEscape.SelectKey(fence, input.AsSpan(0, firstEnd),
                    input.AsSpan(firstEnd, secondEnd - firstEnd), input.AsSpan(secondEnd));
                Assert.Equal(ScalarTinyKey(input, fence), key);
                AssertMarkerFree(input, key, fence);
            }
        }
    }

    [Fact]
    public void SelectKey_InternalSuccessfulPassMustConsumeExactCompleteRange() {
        Assert.Throws<InvalidOperationException>(() => XorEscape.SelectKey(Fence, 4, new ArraySource([[1, 2, 3]])));
        Assert.Throws<InvalidOperationException>(() => XorEscape.SelectKey(Fence, 4, new ArraySource([[1, 2, 3, 4, 5]])));
        byte[] containsFence = MakeForbiddenWords(Fence, [0u]);
        Assert.Throws<InvalidOperationException>(() => XorEscape.SelectKey(Fence, 8, new ArraySource([containsFence])));
    }

    [Fact]
    public void CopyAndInPlace_MatchIndependentByteOracleAcrossLengthsPhasesAndKeys() {
        uint[] keys = [0u, 1u, 0x01020304u, 0x80000000u, Fence, uint.MaxValue];
        int[] lengths = Enumerable.Range(0, 130).Concat([255, 256, 257, 1023]).ToArray();
        foreach (uint key in keys) {
            for (int phase = 0; phase < 4; phase++) {
                foreach (int length in lengths) {
                    byte[] input = Enumerable.Range(0, length).Select(i => (byte)(i * 113 + 17)).ToArray();
                    byte[] before = (byte[])input.Clone();
                    byte[] expected = ScalarTransform(input, key, phase);
                    byte[] destination = Enumerable.Repeat((byte)0xcc, length + 7).ToArray();
                    XorEscape.Copy(input, destination, key, phase);
                    Assert.Equal(expected, destination.AsSpan(0, length).ToArray());
                    Assert.All(destination.Skip(length), value => Assert.Equal((byte)0xcc, value));
                    Assert.Equal(before, input);

                    byte[] inPlace = (byte[])input.Clone();
                    XorEscape.InPlace(inPlace, key, phase);
                    Assert.Equal(expected, inPlace);
                    XorEscape.InPlace(inPlace, key, phase);
                    Assert.Equal(input, inPlace);
                }
            }
        }
    }

    [Fact]
    public void Copy_ChunkPhasesMatchOneContinuousLogicalByteStream() {
        byte[] input = Enumerable.Range(0, 137).Select(i => (byte)(i * 79 + 41)).ToArray();
        const uint key = 0x81234567u;
        byte[] expected = ScalarTransform(input, key, 0);
        byte[] actual = new byte[input.Length];
        int offset = 0;
        int[] chunkLengths = [0, 1, 0, 3, 17, 2, 31, 1, 0, 82];
        foreach (int length in chunkLengths) {
            XorEscape.Copy(input.AsSpan(offset, length), actual.AsSpan(offset, length), key, offset & 3);
            offset += length;
        }
        Assert.Equal(input.Length, offset);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x01020304u)]
    public void Copy_ShiftedOverlapInEitherDirectionRejectsBeforeWrites(uint key) {
        byte[] bytes = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        byte[] before = (byte[])bytes.Clone();
        Assert.Throws<ArgumentException>(() => XorEscape.Copy(bytes.AsSpan(0, 16), bytes.AsSpan(1, 16), key));
        Assert.Equal(before, bytes);
        Assert.Throws<ArgumentException>(() => XorEscape.Copy(bytes.AsSpan(1, 16), bytes.AsSpan(0, 16), key));
        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Transform_InvalidPhaseRejectsBeforeWritesEvenForZeroAndEmptyInput(int phase) {
        byte[] bytes = [1, 2, 3, 4];
        byte[] before = (byte[])bytes.Clone();
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.Copy(bytes, bytes, 0, phase));
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.InPlace(bytes, 0, phase));
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.Copy([], [], 0, phase));
        Assert.Throws<ArgumentOutOfRangeException>(() => XorEscape.InPlace([], 0, phase));
        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xffffffffu)]
    public void Copy_ShortDestinationRejectsBeforeWrites(uint key) {
        byte[] input = [1, 2, 3, 4];
        byte[] destination = [5, 6, 7];
        Assert.Throws<ArgumentException>(() => XorEscape.Copy(input, destination, key));
        Assert.Equal(new byte[] { 5, 6, 7 }, destination);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, input);
    }

    [Fact]
    public void Copy_ExactAliasAndUnusedDestinationSuffixKeepTheDeclaredWriteRange() {
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] expected = ScalarTransform(bytes.AsSpan(0, 4), 0x01020304u, 3);
        XorEscape.Copy(bytes.AsSpan(0, 4), bytes, 0x01020304u, 3);
        Assert.Equal(expected, bytes.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, bytes.AsSpan(4).ToArray());

        // Only the written prefix participates in overlap: these equal-length regions are disjoint.
        XorEscape.Copy(bytes.AsSpan(4), bytes, 0);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 5, 6, 7, 8 }, bytes);
    }

    private static uint ScalarTinyKey(ReadOnlySpan<byte> input, uint fence) {
        for (uint key = 0; key < 64; key++) {
            bool forbidden = key == fence;
            for (int offset = 0; offset < input.Length; offset += 4) {
                if ((ReadWord(input[offset..]) ^ key) == fence) { forbidden = true; }
            }
            if (!forbidden) { return key; }
        }
        throw new InvalidOperationException("The independent tiny oracle found no key.");
    }

    private static void AssertMarkerFree(ReadOnlySpan<byte> input, uint key, uint fence) {
        Assert.NotEqual(fence, key);
        byte[] transformed = ScalarTransform(input, key, 0);
        for (int offset = 0; offset < transformed.Length; offset += 4) {
            Assert.NotEqual(fence, ReadWord(transformed.AsSpan(offset)));
        }
    }

    private static byte[] ScalarTransform(ReadOnlySpan<byte> input, uint key, int phase) {
        byte[] result = new byte[input.Length];
        for (int i = 0; i < input.Length; i++) {
            result[i] = (byte)(input[i] ^ (byte)(key >> (((phase + i) & 3) * 8)));
        }
        return result;
    }

    private static uint ReadWord(ReadOnlySpan<byte> bytes) {
        return bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16) | ((uint)bytes[3] << 24);
    }

    private static void WriteWord(Span<byte> destination, uint value) {
        for (int i = 0; i < 4; i++) { destination[i] = (byte)(value >> (i * 8)); }
    }

    private static byte[] MakeForbiddenWords(uint fence, uint[] keys) {
        byte[] input = new byte[keys.Length * 4];
        for (int i = 0; i < keys.Length; i++) { WriteWord(input.AsSpan(i * 4), fence ^ keys[i]); }
        return input;
    }

    private static byte[][] SplitIntoSingleBytesAndEmptyChunks(byte[] input) {
        byte[][] chunks = new byte[input.Length * 2 + 1][];
        for (int i = 0; i < input.Length; i++) {
            chunks[i * 2] = [];
            chunks[i * 2 + 1] = [input[i]];
        }
        chunks[^1] = [];
        return chunks;
    }

    private struct ArraySource(byte[][] chunks) : IXorEscapeSource {
        public int Index;

        public bool TryGetNext(out ReadOnlySpan<byte> bytes) {
            if (Index >= chunks.Length) {
                bytes = default;
                return false;
            }
            bytes = chunks[Index++];
            return true;
        }
    }
}
