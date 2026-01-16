// Source: Atelia.Primitives.Tests - 基础类型库测试
// Design: atelia/docs/Primitives/AteliaResult.md

using Xunit;

namespace Atelia.Tests;

/// <summary>
/// 用于测试的简单错误实现。
/// </summary>
public sealed record TestError : AteliaError {
    public TestError(string errorCode, string message, string? recoveryHint = null)
        : base(errorCode, message, recoveryHint) {
    }

    public TestError(string errorCode, string message, AteliaError? cause)
        : base(errorCode, message, Cause: cause) {
    }
}

public class AteliaErrorTests {
    [Fact]
    public void Error_ShouldStoreAllProperties() {
        // Arrange
        var details = new Dictionary<string, string> { ["key"] = "value" };
        var cause = new TestError("CAUSE.ERROR", "Cause message");

        // Act
        var error = new TestError("TEST.ERROR", "Test message", "Recovery hint") {
            Details = details,
            Cause = cause
        };

        // Assert
        Assert.Equal("TEST.ERROR", error.ErrorCode);
        Assert.Equal("Test message", error.Message);
        Assert.Equal("Recovery hint", error.RecoveryHint);
        Assert.Same(details, error.Details);
        Assert.Same(cause, error.Cause);
    }

    [Fact]
    public void GetCauseChainDepth_WithNoCause_ShouldReturnZero() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test message");

        // Act
        var depth = error.GetCauseChainDepth();

        // Assert
        Assert.Equal(0, depth);
    }

    [Fact]
    public void GetCauseChainDepth_WithCauseChain_ShouldReturnCorrectDepth() {
        // Arrange
        var level3 = new TestError("LEVEL3", "Level 3");
        var level2 = new TestError("LEVEL2", "Level 2", level3);
        var level1 = new TestError("LEVEL1", "Level 1", level2);
        var root = new TestError("ROOT", "Root", level1);

        // Act
        var depth = root.GetCauseChainDepth();

        // Assert
        Assert.Equal(3, depth);
    }

    [Fact]
    public void IsCauseChainTooDeep_WithinLimit_ShouldReturnFalse() {
        // Arrange
        var level2 = new TestError("LEVEL2", "Level 2");
        var level1 = new TestError("LEVEL1", "Level 1", level2);
        var root = new TestError("ROOT", "Root", level1);

        // Act
        var tooDeep = root.IsCauseChainTooDeep(maxDepth: 5);

        // Assert
        Assert.False(tooDeep);
    }

    [Fact]
    public void IsCauseChainTooDeep_ExceedingLimit_ShouldReturnTrue() {
        // Arrange - create a chain of 7 errors (depth = 6, exceeds maxDepth=5)
        var e7 = new TestError("E7", "Error 7");
        var e6 = new TestError("E6", "Error 6", e7);
        var e5 = new TestError("E5", "Error 5", e6);
        var e4 = new TestError("E4", "Error 4", e5);
        var e3 = new TestError("E3", "Error 3", e4);
        var e2 = new TestError("E2", "Error 2", e3);
        var e1 = new TestError("E1", "Error 1", e2);

        // Act
        var tooDeep = e1.IsCauseChainTooDeep(maxDepth: 5);

        // Assert
        Assert.True(tooDeep);
    }
}
