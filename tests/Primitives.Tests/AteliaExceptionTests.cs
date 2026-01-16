// Source: Atelia.Primitives.Tests - 基础类型库测试
// Design: atelia/docs/Primitives/AteliaResult.md

using Xunit;

namespace Atelia.Tests;

/// <summary>
/// 用于测试的简单异常实现。
/// </summary>
public sealed class TestException : AteliaException {
    public TestException(AteliaError error) : base(error) {
    }

    public TestException(AteliaError error, Exception? innerException) : base(error, innerException) {
    }
}

public class AteliaExceptionTests {
    [Fact]
    public void Exception_ShouldExposeErrorProperties() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message", "Try again");

        // Act
        var exception = new TestException(error);

        // Assert
        Assert.Same(error, exception.Error);
        Assert.Equal("TEST.ERROR", exception.ErrorCode);
        Assert.Equal("Try again", exception.RecoveryHint);
        Assert.Equal("Test error message", exception.Message);
    }

    [Fact]
    public void Exception_WithInnerException_ShouldPreserveBoth() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message");
        var inner = new InvalidOperationException("Inner error");

        // Act
        var exception = new TestException(error, inner);

        // Assert
        Assert.Same(error, exception.Error);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void Exception_WithNullError_ShouldThrow() {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => new TestException(null!));
    }

    [Fact]
    public void Exception_ShouldImplementIAteliaHasError() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message");
        var exception = new TestException(error);

        // Act
        IAteliaHasError hasError = exception;

        // Assert
        Assert.Same(error, hasError.Error);
    }
}
