// Source: Atelia.Primitives.Tests - 基础类型库测试
// Design: atelia/docs/Primitives/AteliaResult.md

using Xunit;

namespace Atelia.Tests;

public class AsyncAteliaResultTests {
    [Fact]
    public void Success_ShouldCreateSuccessResult() {
        // Arrange & Act
        var result = AsyncAteliaResult<int>.Success(42);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_ShouldCreateFailureResult() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message", "Try again");

        // Act
        var result = AsyncAteliaResult<int>.Failure(error);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(default, result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal("TEST.ERROR", result.Error.ErrorCode);
    }

    [Fact]
    public void Failure_WithNullError_ShouldThrow() {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => AsyncAteliaResult<int>.Failure(null!));
    }

    [Fact]
    public void Success_WithNullValue_ShouldThrow() {
        var ex = Assert.Throws<ArgumentNullException>(() => AsyncAteliaResult<string>.Success(null!));
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void TryGetValue_OnSuccess_ShouldReturnTrueAndValue() {
        // Arrange
        var result = AsyncAteliaResult<string>.Success("hello");

        // Act
        var success = result.TryGetValue(out var value);

        // Assert
        Assert.True(success);
        Assert.Equal("hello", value);
    }

    [Fact]
    public void TryGetValue_OnFailure_ShouldReturnFalse() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = AsyncAteliaResult<string>.Failure(error);

        // Act
        var success = result.TryGetValue(out var value);

        // Assert
        Assert.False(success);
        Assert.Null(value);
    }

    [Fact]
    public void TryGetError_OnFailure_ShouldReturnTrueAndError() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = AsyncAteliaResult<string>.Failure(error);

        // Act
        var hasError = result.TryGetError(out var outError);

        // Assert
        Assert.True(hasError);
        Assert.Same(error, outError);
    }

    [Fact]
    public void TryGetError_OnSuccess_ShouldReturnFalse() {
        // Arrange
        var result = AsyncAteliaResult<string>.Success("hello");

        // Act
        var hasError = result.TryGetError(out var error);

        // Assert
        Assert.False(hasError);
        Assert.Null(error);
    }

    [Fact]
    public void ValueOr_OnSuccess_ShouldReturnValue() {
        // Arrange
        var result = AsyncAteliaResult<int>.Success(42);

        // Act
        var value = result.ValueOr(0);

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void ValueOr_OnFailure_ShouldReturnFallback() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = AsyncAteliaResult<int>.Failure(error);

        // Act
        var value = result.ValueOr(99);

        // Assert
        Assert.Equal(99, value);
    }

    [Fact]
    public void Unwrap_OnSuccess_ShouldReturnValue() {
        // Arrange
        var result = AsyncAteliaResult<int>.Success(42);

        // Act
        var value = result.Unwrap();

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void Unwrap_OnFailure_ShouldThrow() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message");
        var result = AsyncAteliaResult<int>.Failure(error);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => result.Unwrap());
        Assert.Contains("TEST.ERROR", ex.Message);
        Assert.Contains("Test error message", ex.Message);
    }

    [Fact]
    public void ImplicitConversion_FromValue_ShouldCreateSuccess() {
        // Arrange & Act
        AsyncAteliaResult<int> result = 42;

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Default_ShouldBeFailureWithUninitializedError() {
        // Arrange & Act
        AsyncAteliaResult<string> result = default;

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal("Primitives.ResultUninitialized", result.Error.ErrorCode);
    }
}
