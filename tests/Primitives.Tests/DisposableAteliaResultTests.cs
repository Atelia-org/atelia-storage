// Source: Atelia.Primitives.Tests - 基础类型库测试
// Design: atelia/docs/Primitives/AteliaResult.md

using Xunit;

namespace Atelia.Tests;

/// <summary>
/// 用于测试的可释放资源。
/// </summary>
public sealed class TestDisposable : IDisposable {
    public bool IsDisposed { get; private set; }

    public void Dispose() {
        IsDisposed = true;
    }
}

public sealed class NonIdempotentDisposable : IDisposable {
    public int DisposeCount { get; private set; }

    public void Dispose() {
        DisposeCount++;
        if (DisposeCount > 1) { throw new InvalidOperationException("Dispose called more than once."); }
    }
}

public class DisposableAteliaResultTests {
    #region Success 创建

    [Fact]
    public void Success_ShouldCreateSuccessResult() {
        // Arrange
        var resource = new TestDisposable();

        // Act
        var result = DisposableAteliaResult<TestDisposable>.Success(resource);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Same(resource, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Success_WithNullValue_ShouldThrow() {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(
            () =>
            DisposableAteliaResult<TestDisposable>.Success(null!)
        );
    }

    #endregion

    #region Failure 创建

    [Fact]
    public void Failure_ShouldCreateFailureResult() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message", "Try again");

        // Act
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal("TEST.ERROR", result.Error.ErrorCode);
        Assert.Equal("Test error message", result.Error.Message);
        Assert.Equal("Try again", result.Error.RecoveryHint);
    }

    [Fact]
    public void Failure_WithNullError_ShouldThrow() {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(
            () =>
            DisposableAteliaResult<TestDisposable>.Failure(null!)
        );
    }

    #endregion

    #region Dispose 语义

    [Fact]
    public void Dispose_OnSuccess_ShouldDisposeValue() {
        // Arrange
        var resource = new TestDisposable();
        var result = DisposableAteliaResult<TestDisposable>.Success(resource);

        // Act
        result.Dispose();

        // Assert
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void Dispose_OnFailure_ShouldBeNoOp() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

        // Act & Assert (should not throw)
        result.Dispose();

        // 验证失败结果的 Value 为 null，因此 Dispose 是静默的
        Assert.Null(result.Value);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_ShouldDisposeValueOnce() {
        // Arrange
        var resource = new NonIdempotentDisposable();
        var result = DisposableAteliaResult<NonIdempotentDisposable>.Success(resource);

        // Act & Assert (should not throw on multiple dispose calls)
        result.Dispose();
        result.Dispose();
        result.Dispose();

        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void UsingStatement_ShouldDisposeOnScopeExit() {
        // Arrange
        var resource = new TestDisposable();

        // Act
        {
            using var result = DisposableAteliaResult<TestDisposable>.Success(resource);
            Assert.False(resource.IsDisposed);
        }

        // Assert
        Assert.True(resource.IsDisposed);
    }

    #endregion

    #region TryGetValue / TryGetError

    [Fact]
    public void TryGetValue_OnSuccess_ShouldReturnTrueAndValue() {
        // Arrange
        var resource = new TestDisposable();
        var result = DisposableAteliaResult<TestDisposable>.Success(resource);

        // Act
        var success = result.TryGetValue(out var value);

        // Assert
        Assert.True(success);
        Assert.Same(resource, value);
    }

    [Fact]
    public void TryGetValue_OnFailure_ShouldReturnFalseAndNull() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

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
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

        // Act
        var hasError = result.TryGetError(out var outError);

        // Assert
        Assert.True(hasError);
        Assert.Same(error, outError);
    }

    [Fact]
    public void TryGetError_OnSuccess_ShouldReturnFalseAndNull() {
        // Arrange
        var resource = new TestDisposable();
        var result = DisposableAteliaResult<TestDisposable>.Success(resource);

        // Act
        var hasError = result.TryGetError(out var error);

        // Assert
        Assert.False(hasError);
        Assert.Null(error);
    }

    #endregion

    #region GetValueOrThrow

    [Fact]
    public void GetValueOrThrow_OnSuccess_ShouldReturnValue() {
        // Arrange
        var resource = new TestDisposable();
        var result = DisposableAteliaResult<TestDisposable>.Success(resource);

        // Act
        var value = result.GetValueOrThrow();

        // Assert
        Assert.Same(resource, value);
    }

    [Fact]
    public void GetValueOrThrow_OnFailure_ShouldThrowWithErrorInfo() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error message");
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => result.GetValueOrThrow());
        Assert.Contains("TEST.ERROR", ex.Message);
        Assert.Contains("Test error message", ex.Message);
    }

    #endregion

    #region GetValueOrDefault

    [Fact]
    public void GetValueOrDefault_OnSuccess_ShouldReturnValue() {
        // Arrange
        var resource = new TestDisposable();
        var defaultResource = new TestDisposable();
        var result = DisposableAteliaResult<TestDisposable>.Success(resource);

        // Act
        var value = result.GetValueOrDefault(defaultResource);

        // Assert
        Assert.Same(resource, value);
    }

    [Fact]
    public void GetValueOrDefault_OnFailure_ShouldReturnDefault() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var defaultResource = new TestDisposable();
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

        // Act
        var value = result.GetValueOrDefault(defaultResource);

        // Assert
        Assert.Same(defaultResource, value);
    }

    [Fact]
    public void GetValueOrDefault_OnFailure_WithNullDefault_ShouldReturnNull() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Failed");
        var result = DisposableAteliaResult<TestDisposable>.Failure(error);

        // Act
        var value = result.GetValueOrDefault(null);

        // Assert
        Assert.Null(value);
    }

    #endregion

    #region ToDisposable 扩展方法

    [Fact]
    public void ToDisposable_OnSuccess_ShouldConvertCorrectly() {
        // Arrange
        var resource = new TestDisposable();
        var ateliaResult = AteliaResult<TestDisposable>.Success(resource);

        // Act
        var disposableResult = ateliaResult.ToDisposable();

        // Assert
        Assert.True(disposableResult.IsSuccess);
        Assert.Same(resource, disposableResult.Value);
        Assert.Null(disposableResult.Error);
    }

    [Fact]
    public void ToDisposable_OnFailure_ShouldConvertCorrectly() {
        // Arrange
        var error = new TestError("TEST.ERROR", "Test error");
        var ateliaResult = AteliaResult<TestDisposable>.Failure(error);

        // Act
        var disposableResult = ateliaResult.ToDisposable();

        // Assert
        Assert.False(disposableResult.IsSuccess);
        Assert.True(disposableResult.IsFailure);
        Assert.Same(error, disposableResult.Error);
        Assert.Null(disposableResult.Value);
    }

    [Fact]
    public void ToDisposable_OnSuccess_ShouldDisposeValueOnScopeExit() {
        // Arrange
        var resource = new TestDisposable();
        var ateliaResult = AteliaResult<TestDisposable>.Success(resource);

        // Act
        {
            using var disposableResult = ateliaResult.ToDisposable();

            // 在 using 块内资源不应被释放
            Assert.False(resource.IsDisposed);
        }

        // Assert
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void ToDisposable_OnSuccessWithNullValue_ShouldThrow() {
        // Arrange
        // Note: AteliaResult<T> is a ref struct, so it cannot be used inside lambda expressions.

        // Act & Assert
        try {
            _ = AteliaResult<TestDisposable>.Success(null).ToDisposable();
            Assert.Fail("Expected ArgumentNullException");
        }
        catch (ArgumentNullException ex) {
            Assert.Equal("value", ex.ParamName);
        }
    }

    #endregion
}
