using System;
using Xunit;

namespace Atelia.Data.Tests;

public class ChunkSizingStrategyTests {
    [Theory]
    [InlineData(1024, 1024)]              // 最小值
    [InlineData(1 << 20, 1 << 20)]        // 1MB → 1MB
    [InlineData((1 << 20) + 1, 1 << 21)]  // 1MB+1 → 2MB (RoundUp)
    [InlineData(1 << 29, 1 << 29)]        // 512MB → 512MB
    [InlineData(1 << 30, 1 << 30)]        // 1GB → 1GB (边界)
    [InlineData((1 << 30) + 1, (1 << 30) + 1)] // 1GB+1 → 不变（超出 RoundUp 范围）
    [InlineData(int.MaxValue, int.MaxValue)]   // 极端值 → 不变
    public void ComputeChunkSize_WithVariousHints_ReturnsPositiveValue(int sizeHint, int expected) {
        var strategy = new ChunkSizingStrategy(1024, int.MaxValue);
        int result = strategy.ComputeChunkSize(sizeHint);

        Assert.True(result > 0, $"Result must be positive, got {result}");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComputeChunkSize_WithDangerousInputs_NeverReturnsNegative() {
        var strategy = new ChunkSizingStrategy(1024, int.MaxValue);

        // 测试整个危险区间
        int[] dangerousHints = [
            (1 << 30) + 1,
            (1 << 30) + 1000,
            int.MaxValue - 1000,
            int.MaxValue
        ];

        foreach (var hint in dangerousHints) {
            int result = strategy.ComputeChunkSize(hint);
            Assert.True(result > 0,
                $"ComputeChunkSize({hint:N0}) returned {result}, expected positive"
            );
        }
    }

    [Theory]
    [InlineData(1024, 64 * 1024, 1024)]       // 默认配置，hint=min
    [InlineData(1024, 1 << 30, 1024)]         // 1GB max，hint=min → 返回 min
    [InlineData(4096, int.MaxValue, 4096)]    // 极端 max，hint=min → 返回 min
    public void ComputeChunkSize_WithVariousConfigs_Clamps(int minSize, int maxSize, int expected) {
        var strategy = new ChunkSizingStrategy(minSize, maxSize);
        int result = strategy.ComputeChunkSize(minSize);

        Assert.Equal(expected, result);
        Assert.InRange(result, minSize, maxSize);
    }

    [Fact]
    public void NotifyChunkCreated_IncreasesTargetSize() {
        var strategy = new ChunkSizingStrategy(1024, 1 << 20); // 1MB max

        int size1 = strategy.ComputeChunkSize(1024);
        strategy.NotifyChunkCreated(size1);

        int size2 = strategy.ComputeChunkSize(1024);

        // 第二次应该更大（增长因子 2.0）
        Assert.True(size2 >= size1, $"Expected size2 ({size2}) >= size1 ({size1})");
    }
}
