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

/// <summary>
/// 用于测试的简单异常实现。
/// </summary>
public sealed class TestException : AteliaException {
    public TestException(AteliaError error) : base(error) {
    }

    public TestException(AteliaError error, Exception? innerException) : base(error, innerException) {
    }
}

public class AteliaResultTests {
    [Fact]
    public void Success_ShouldCreateSuccessResult() {
        // Arrange & Act
        var result = AteliaResult<int>.Success(42);

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
        var result = AteliaResult<int>.Failure(error);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(default, result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal("TEST.ERROR", result.Error.ErrorCode);
        Assert.Equal("Test error message", result.Error.Message);
        Assert.Equal("Try again", result.Error.RecoveryHint);
    }

    [Fact]
    public void Failure_WithNullError_ShouldThrow() {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => AteliaResult<int>.Failure(null!));
    }

    [Fact]
    public void TryGetValue_OnSuccess_ShouldReturnTrueAndValue() {
        // Arrange
        var result = AteliaResult<string>.Success("hello");

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
        var result = AteliaResult<string>.Failure(error);

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
        var result = AteliaResult<string>.Failure(error);

        // Act
        var hasError = result.TryGetError(out var outError);

        // Assert
        Assert.True(hasError);
        Assert.Same(error, outError);
    }

    [Fact]
    public void TryGetError_OnSuccess_ShouldReturnFalse() {
        // Arrange
        var result = AteliaResult<string>.Success("hello");

        // Act
        var hasError = result.TryGetError(out var error);

        // Assert
        Assert.False(hasError);
        Assert.Null(error);
    }

    [Fact]
    public void GetValueOrDefault_OnSuccess_ShouldReturnValue() {
        // Arrange
        var result = AteliaResult<int>.Success(42);

        // Act
        var value = result.GetValueOrDefault(0);

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void GetValueOrDefault_OnFailure_ShouldReturnDefault() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = AteliaResult<int>.Failure(error);

        // Act
        var value = result.GetValueOrDefault(99);

        // Assert
        Assert.Equal(99, value);
    }

    [Fact]
    public void GetValueOrThrow_OnSuccess_ShouldReturnValue() {
        // Arrange
        var result = AteliaResult<int>.Success(42);

        // Act
        var value = result.GetValueOrThrow();

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void GetValueOrThrow_OnFailure_ShouldThrow() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message");
        var result = AteliaResult<int>.Failure(error);

        // Act & Assert
        // Note: ref struct 不能在 lambda 中使用，所以用 try-catch
        try {
            result.GetValueOrThrow();
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException ex) {
            Assert.Contains("TEST.ERROR", ex.Message);
            Assert.Contains("Test error message", ex.Message);
        }
    }

    [Fact]
    public void ImplicitConversion_FromValue_ShouldCreateSuccess() {
        // Arrange & Act
        AteliaResult<int> result = 42;

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Success_WithNullValue_ShouldCreateSuccessResult() {
        // Arrange & Act
        var result = AteliaResult<string?>.Success(null);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Default_ShouldBeSuccess() {
        // Arrange & Act
        AteliaResult<int> result = default;

        // Assert
        Assert.True(result.IsSuccess);  // _error is null → success
        Assert.Equal(0, result.Value);  // default(int)
        Assert.Null(result.Error);
    }

    [Fact]
    public void ToAsync_OnSuccess_ShouldConvertCorrectly() {
        // Arrange
        var result = AteliaResult<int>.Success(42);

        // Act
        var asyncResult = result.ToAsync();

        // Assert
        Assert.True(asyncResult.IsSuccess);
        Assert.Equal(42, asyncResult.Value);
        Assert.Null(asyncResult.Error);
    }

    [Fact]
    public void ToAsync_OnFailure_ShouldConvertCorrectly() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error");
        var result = AteliaResult<int>.Failure(error);

        // Act
        var asyncResult = result.ToAsync();

        // Assert
        Assert.False(asyncResult.IsSuccess);
        Assert.True(asyncResult.IsFailure);
        Assert.Same(error, asyncResult.Error);
    }
}

public class AteliaAsyncResultTests {
    [Fact]
    public void Success_ShouldCreateSuccessResult() {
        // Arrange & Act
        var result = AteliaAsyncResult<int>.Success(42);

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
        var result = AteliaAsyncResult<int>.Failure(error);

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
        Assert.Throws<ArgumentNullException>(() => AteliaAsyncResult<int>.Failure(null!));
    }

    [Fact]
    public void TryGetValue_OnSuccess_ShouldReturnTrueAndValue() {
        // Arrange
        var result = AteliaAsyncResult<string>.Success("hello");

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
        var result = AteliaAsyncResult<string>.Failure(error);

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
        var result = AteliaAsyncResult<string>.Failure(error);

        // Act
        var hasError = result.TryGetError(out var outError);

        // Assert
        Assert.True(hasError);
        Assert.Same(error, outError);
    }

    [Fact]
    public void TryGetError_OnSuccess_ShouldReturnFalse() {
        // Arrange
        var result = AteliaAsyncResult<string>.Success("hello");

        // Act
        var hasError = result.TryGetError(out var error);

        // Assert
        Assert.False(hasError);
        Assert.Null(error);
    }

    [Fact]
    public void GetValueOrDefault_OnSuccess_ShouldReturnValue() {
        // Arrange
        var result = AteliaAsyncResult<int>.Success(42);

        // Act
        var value = result.GetValueOrDefault(0);

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void GetValueOrDefault_OnFailure_ShouldReturnDefault() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = AteliaAsyncResult<int>.Failure(error);

        // Act
        var value = result.GetValueOrDefault(99);

        // Assert
        Assert.Equal(99, value);
    }

    [Fact]
    public void GetValueOrThrow_OnSuccess_ShouldReturnValue() {
        // Arrange
        var result = AteliaAsyncResult<int>.Success(42);

        // Act
        var value = result.GetValueOrThrow();

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void GetValueOrThrow_OnFailure_ShouldThrow() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message");
        var result = AteliaAsyncResult<int>.Failure(error);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => result.GetValueOrThrow());
        Assert.Contains("TEST.ERROR", ex.Message);
        Assert.Contains("Test error message", ex.Message);
    }

    [Fact]
    public void ImplicitConversion_FromValue_ShouldCreateSuccess() {
        // Arrange & Act
        AteliaAsyncResult<int> result = 42;

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Success_WithNullValue_ShouldCreateSuccessResult() {
        // Arrange & Act
        var result = AteliaAsyncResult<string?>.Success(null);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Default_ShouldBeSuccess() {
        // Arrange & Act
        AteliaAsyncResult<int> result = default;

        // Assert
        Assert.True(result.IsSuccess);  // _error is null → success
        Assert.Equal(0, result.Value);  // default(int)
        Assert.Null(result.Error);
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
