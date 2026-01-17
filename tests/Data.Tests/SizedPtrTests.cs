using System;
using Xunit;

namespace Atelia.Data.Tests;

public class SizedPtrTests {
    // ========== P0: Roundtrip ==========

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(0L, 4)]
    [InlineData(4L, 0)]
    [InlineData(4L, 4)]
    [InlineData(1024L, 256)]
    [InlineData(SizedPtr.MaxOffset, 0)]
    [InlineData(0L, SizedPtr.MaxLength)]
    [InlineData(SizedPtr.MaxOffset, SizedPtr.MaxLength)]
    public void Create_Roundtrip_PreservesValues(long offsetBytes, int lengthBytes) {
        var ptr = SizedPtr.Create(offsetBytes, lengthBytes);

        Assert.Equal(offsetBytes, ptr.Offset);
        Assert.Equal(lengthBytes, ptr.Length);
    }

    [Fact]
    public void Create_MaxOffset_Roundtrips() {
        var ptr = SizedPtr.Create(SizedPtr.MaxOffset, 0);
        Assert.Equal(SizedPtr.MaxOffset, ptr.Offset);
        Assert.Equal(0, ptr.Length);
    }

    [Fact]
    public void Create_MaxLength_Roundtrips() {
        var ptr = SizedPtr.Create(0, SizedPtr.MaxLength);
        Assert.Equal(0L, ptr.Offset);
        Assert.Equal(SizedPtr.MaxLength, ptr.Length);
    }

    [Fact]
    public void TryCreate_ValidInput_ReturnsTrue() {
        bool success = SizedPtr.TryCreate(1024, 256, out var ptr);

        Assert.True(success);
        Assert.Equal(1024L, ptr.Offset);
        Assert.Equal(256, ptr.Length);
    }

    // ========== P0: Alignment Checks ==========

    [Theory]
    [InlineData(1L, 0)]   // offset not aligned
    [InlineData(2L, 0)]   // offset not aligned
    [InlineData(3L, 0)]   // offset not aligned
    [InlineData(0L, 1)]   // length not aligned
    [InlineData(0L, 2)]   // length not aligned
    [InlineData(0L, 3)]   // length not aligned
    [InlineData(5L, 7)]   // both not aligned
    public void Create_NonAligned_ThrowsArgumentOutOfRange(long offsetBytes, int lengthBytes) {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SizedPtr.Create(offsetBytes, lengthBytes));
        Assert.Contains("4B-aligned", ex.Message);
    }

    [Theory]
    [InlineData(1L, 0)]
    [InlineData(0L, 1)]
    [InlineData(5L, 7)]
    public void TryCreate_NonAligned_ReturnsFalse(long offsetBytes, int lengthBytes) {
        bool success = SizedPtr.TryCreate(offsetBytes, lengthBytes, out var ptr);

        Assert.False(success);
        Assert.Equal(default, ptr);
    }

    // ========== P0: Boundary Checks ==========

    [Fact]
    public void Create_NegativeOffset_Throws() {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SizedPtr.Create(-4, 0));
        Assert.Contains("non-negative", ex.Message);
    }

    [Fact]
    public void Create_NegativeLength_Throws() {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SizedPtr.Create(0, -4));
        Assert.Contains("non-negative", ex.Message);
    }

    [Fact]
    public void Create_OffsetExceedsMax_Throws() {
        long tooLarge = SizedPtr.MaxOffset + 4; // 下一个对齐值

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SizedPtr.Create(tooLarge, 0));
        Assert.Contains("MaxOffset", ex.Message);
    }

    [Fact]
    public void Create_LengthExceedsMax_Throws() {
        int tooLarge = SizedPtr.MaxLength + 4; // 下一个对齐值

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SizedPtr.Create(0, tooLarge));
        Assert.Contains("MaxLength", ex.Message);
    }

    [Fact]
    public void TryCreate_OffsetExceedsMax_ReturnsFalse() {
        long tooLarge = SizedPtr.MaxOffset + 4;

        Assert.False(SizedPtr.TryCreate(tooLarge, 0, out _));
    }

    [Fact]
    public void TryCreate_LengthExceedsMax_ReturnsFalse() {
        int tooLarge = SizedPtr.MaxLength + 4;

        Assert.False(SizedPtr.TryCreate(0, tooLarge, out _));
    }

    // ========== P0: FromPacked Accepts Any ulong ==========

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x123456789ABCDEF0UL)]
    public void FromPacked_AnyUlong_DoesNotThrow(ulong packed) {
        var ptr = SizedPtr.FromPacked(packed);

        // 验证可以解包，不抛异常
        _ = ptr.Offset;
        _ = ptr.Length;
    }

    [Fact]
    public void FromPacked_ThenPacked_Roundtrips() {
        ulong original = 0xDEADBEEFCAFEBABEUL;
        var ptr = SizedPtr.FromPacked(original);

        Assert.Equal(original, ptr.Packed);
    }

    [Fact]
    public void Create_ThenPacked_ThenFromPacked_Roundtrips() {
        var original = SizedPtr.Create(1024, 256);
        var restored = SizedPtr.FromPacked(original.Packed);

        Assert.Equal(original.Offset, restored.Offset);
        Assert.Equal(original.Length, restored.Length);
        Assert.Equal(original.Packed, restored.Packed);
    }

    // ========== P0: Contains (Half-Open Interval) ==========

    [Fact]
    public void Contains_PositionAtStart_ReturnsTrue() {
        var ptr = SizedPtr.Create(100, 52);

        Assert.True(ptr.Contains(100)); // [100, 152)
    }

    [Fact]
    public void Contains_PositionInMiddle_ReturnsTrue() {
        var ptr = SizedPtr.Create(100, 52);

        Assert.True(ptr.Contains(124));
    }

    [Fact]
    public void Contains_PositionAtEndMinusOne_ReturnsTrue() {
        var ptr = SizedPtr.Create(100, 52); // [100, 152)

        Assert.True(ptr.Contains(151)); // 151 < 152
    }

    [Fact]
    public void Contains_PositionAtEnd_ReturnsFalse() {
        var ptr = SizedPtr.Create(100, 52); // [100, 152)

        Assert.False(ptr.Contains(152)); // 152 == end, exclusive
    }

    [Fact]
    public void Contains_PositionBeyondEnd_ReturnsFalse() {
        var ptr = SizedPtr.Create(100, 52);

        Assert.False(ptr.Contains(200));
    }

    [Fact]
    public void Contains_PositionBeforeStart_ReturnsFalse() {
        var ptr = SizedPtr.Create(100, 52);

        Assert.False(ptr.Contains(50));
        Assert.False(ptr.Contains(99));
    }

    [Fact]
    public void Contains_ZeroLength_AlwaysReturnsFalse() {
        var ptr = SizedPtr.Create(100, 0); // [100, 100) 空区间

        Assert.False(ptr.Contains(99));
        Assert.False(ptr.Contains(100));
        Assert.False(ptr.Contains(101));
    }

    // ========== P1: Overflow Protection ==========

    [Fact]
    public void OffsetPlusLength_CannotOverflow_UnderCurrentBitAllocation() {
        // Under the current 38:26 allocation, MaxOffset and MaxLength are far below long.MaxValue,
        // so (offset+length) overflow is not reachable when inputs are within the allowed range.
        Assert.True(SizedPtr.MaxOffset + (long)SizedPtr.MaxLength < long.MaxValue);
    }

    [Fact]
    public void EndOffsetExclusive_ReturnsOffsetPlusLength() {
        // Under the current bit allocation, overflow is not reachable for valid inputs.
        // This test verifies the value semantics (end-exclusive).
        var ptr = SizedPtr.Create(1000, 500);
        Assert.Equal(1500L, ptr.EndOffsetExclusive);
    }

    [Fact]
    public void EndOffsetExclusive_MaxValues_DoesNotOverflow() {
        // MaxOffset + MaxLength 在值域内不溢出
        var ptr = SizedPtr.Create(SizedPtr.MaxOffset, SizedPtr.MaxLength);
        long end = ptr.EndOffsetExclusive;
        Assert.Equal(SizedPtr.MaxOffset + SizedPtr.MaxLength, end);
    }

    // ========== P1: Constants Verification ==========

    [Fact]
    public void Constants_AreCorrect() {
        Assert.Equal(38, SizedPtr.OffsetPackedBits);
        Assert.Equal(26, SizedPtr.LengthPackedBits);

        // MaxOffset = (2^38 - 1) * 4
        long expectedMaxOffset = (long)(((1UL << 38) - 1) << 2);
        Assert.Equal(SizedPtr.MaxOffset, expectedMaxOffset);

        // MaxLength = (2^26 - 1) * 4
        int expectedMaxLength = (int)(((1UL << 26) - 1) << 2);
        Assert.Equal(SizedPtr.MaxLength, expectedMaxLength);
    }

    [Fact]
    public void MaxOffset_IsApproximately1TB() {
        // 1 TB = 2^40 bytes = 1,099,511,627,776
        // MaxOffset = (2^38 - 1) * 4 = 1,099,511,627,772
        Assert.True(SizedPtr.MaxOffset > 1_000_000_000_000L); // > 1 TB
        Assert.True(SizedPtr.MaxOffset < 1_100_000_000_000L); // < 1.1 TB
    }

    [Fact]
    public void MaxLength_IsApproximately256MB() {
        // 256 MB = 2^28 bytes = 268,435,456
        // MaxLength = (2^26 - 1) * 4 = 268,435,452
        Assert.True(SizedPtr.MaxLength > 250_000_000); // > 250 MB
        Assert.True(SizedPtr.MaxLength < 270_000_000); // < 270 MB
    }

    // ========== P1: Deconstruct ==========

    [Fact]
    public void Deconstruct_ReturnsCorrectValues() {
        var ptr = SizedPtr.Create(2048, 512);

        var (offset, length) = ptr;

        Assert.Equal(2048L, offset);
        Assert.Equal(512, length);
    }

    // ========== P1: Record Struct Equality ==========

    [Fact]
    public void Equality_SamePacked_AreEqual() {
        var ptr1 = SizedPtr.Create(100, 52);
        var ptr2 = SizedPtr.Create(100, 52);

        Assert.Equal(ptr1, ptr2);
        Assert.True(ptr1 == ptr2);
        Assert.Equal(ptr1.GetHashCode(), ptr2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentPacked_AreNotEqual() {
        var ptr1 = SizedPtr.Create(100, 52);
        var ptr2 = SizedPtr.Create(100, 56);

        Assert.NotEqual(ptr1, ptr2);
        Assert.True(ptr1 != ptr2);
    }

    // ========== P1: Default Value ==========

    [Fact]
    public void Default_IsZeroZero() {
        var ptr = default(SizedPtr);

        Assert.Equal(0UL, ptr.Packed);
        Assert.Equal(0L, ptr.Offset);
        Assert.Equal(0, ptr.Length);
    }

    // ========== P1: Contains with Large Values (Overflow Safety) ==========

    [Fact]
    public void Contains_LargeOffset_NoOverflow() {
        var ptr = SizedPtr.Create(SizedPtr.MaxOffset, 100);

        // position 在区间内
        Assert.True(ptr.Contains(SizedPtr.MaxOffset));
        Assert.True(ptr.Contains(SizedPtr.MaxOffset + 50));

        // position 在区间外
        Assert.False(ptr.Contains(SizedPtr.MaxOffset - 1)); // before
        Assert.False(ptr.Contains(SizedPtr.MaxOffset + 100)); // at end (exclusive)
    }

    [Fact]
    public void Contains_PositionNearLongMax_NoOverflow() {
        // 即使 position 很大，差值比较也不会溢出
        var ptr = SizedPtr.Create(0, 100);

        Assert.False(ptr.Contains(long.MaxValue)); // 差值会很大，但不会溢出
    }
}
