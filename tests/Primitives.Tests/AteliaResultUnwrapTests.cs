// Source: Atelia.Primitives.Tests - Unwrap/TryUnwrap/ValueOr 行为及 NRT 收窄回归
// Design: atelia/docs/Primitives/AteliaResult.md

using Xunit;

namespace Atelia.Tests;

public class AteliaResultUnwrapTests {
    private static AteliaError MakeError(string code = "TEST.ERR", string msg = "boom")
        => new TestError(code, msg, (AteliaError?)null);

    // -------- Unwrap --------

    [Fact]
    public void Unwrap_OnSuccess_ReturnsValue() {
        var r = AteliaResult<string>.Success("hello");
        Assert.Equal("hello", r.Unwrap());
    }

    [Fact]
    public void Unwrap_OnFailure_ThrowsWithErrorInfo() {
        var r = AteliaResult<string>.Failure(MakeError("E.X", "kaboom"));
        InvalidOperationException? ex = null;
        try { r.Unwrap(); }
        catch (InvalidOperationException e) { ex = e; }
        Assert.NotNull(ex);
        Assert.Contains("E.X", ex!.Message);
        Assert.Contains("kaboom", ex.Message);
    }

    [Fact]
    public void Unwrap_OnDefaultResult_ThrowsWithUninitializedErrorInfo() {
        AteliaResult<string> r = default;
        InvalidOperationException? ex = null;
        try { r.Unwrap(); }
        catch (InvalidOperationException e) { ex = e; }
        Assert.NotNull(ex);
        Assert.Contains("Primitives.ResultUninitialized", ex.Message);
    }

    [Fact]
    public void Unwrap_AsyncResult_OnFailure_Throws() {
        var r = AsyncAteliaResult<int>.Failure(MakeError());
        Assert.Throws<InvalidOperationException>(() => r.Unwrap());
    }

    [Fact]
    public void Unwrap_AsyncDefaultResult_ThrowsWithUninitializedErrorInfo() {
        AsyncAteliaResult<string> r = default;
        var ex = Assert.Throws<InvalidOperationException>(() => r.Unwrap());
        Assert.Contains("Primitives.ResultUninitialized", ex.Message);
    }

    [Fact]
    public void Unwrap_DisposableResult_OnFailure_Throws() {
        using var r = DisposableAteliaResult<DummyDisposable>.Failure(MakeError());
        Assert.Throws<InvalidOperationException>(() => r.Unwrap());
    }

    // -------- TryUnwrap --------

    [Fact]
    public void TryUnwrap_OnSuccess_ReturnsTrueAndValue() {
        var r = AteliaResult<string>.Success("ok");
        Assert.True(r.TryUnwrap(out var v, out var err));
        Assert.Equal("ok", v);
        Assert.Null(err);
        int len = v.Length;
        Assert.Equal(2, len);
    }

    [Fact]
    public void TryUnwrap_OnFailure_ReturnsFalseAndError() {
        var err0 = MakeError("E.A", "fail");
        var r = AteliaResult<string>.Failure(err0);
        Assert.False(r.TryUnwrap(out var v, out var err));
        Assert.Null(v);
        Assert.NotNull(err);
        Assert.Same(err0, err);
        Assert.Equal("fail", err.Message);
    }

    [Fact]
    public void TryUnwrap_AsyncResult_OnSuccess() {
        var r = AsyncAteliaResult<int>.Success(7);
        Assert.True(r.TryUnwrap(out var v, out var err));
        Assert.Equal(7, v);
        Assert.Null(err);
    }

    [Fact]
    public void TryUnwrap_AsyncResult_OnFailure() {
        var r = AsyncAteliaResult<int>.Failure(MakeError());
        Assert.False(r.TryUnwrap(out var v, out var err));
        Assert.NotNull(err);
        Assert.Equal(default, v);
    }

    [Fact]
    public void TryUnwrap_OnDefaultResult_ReturnsFalseAndUninitializedError() {
        AteliaResult<string> r = default;
        Assert.False(r.TryUnwrap(out var v, out var err));
        Assert.Null(v);
        Assert.NotNull(err);
        Assert.Equal("Primitives.ResultUninitialized", err.ErrorCode);
    }

    // -------- ValueOr --------

    [Fact]
    public void ValueOr_OnSuccess_ReturnsValue() {
        var r = AteliaResult<int>.Success(10);
        Assert.Equal(10, r.ValueOr(99));
    }

    [Fact]
    public void ValueOr_OnFailure_ReturnsFallback() {
        var r = AteliaResult<int>.Failure(MakeError());
        Assert.Equal(99, r.ValueOr(99));
    }

    [Fact]
    public void ValueOr_OnFailureReferenceType_ReturnsFallback() {
        var r = AsyncAteliaResult<string>.Failure(MakeError());
        Assert.Equal("fallback", r.ValueOr("fallback"));
    }

    // -------- NRT narrowing via IsSuccess / IsFailure --------

    [Fact]
    public void IsFailure_NarrowsErrorToNonNull() {
        var r = AsyncAteliaResult<string>.Failure(MakeError("E.Z", "zz"));
        if (r.IsFailure) {
            // 这里 r.Error 不应该出现 CS8602
            string msg = r.Error.Message;
            Assert.Equal("zz", msg);
        }
        else {
            Assert.Fail("expected failure");
        }
    }

    [Fact]
    public void TryUnwrap_OnFailure_NarrowsErrorToNonNull() {
        var r = AteliaResult<string>.Failure(MakeError("E.T", "try-unwrap failed"));
        if (!r.TryUnwrap(out var v, out var err)) {
            Assert.Null(v);
            string msg = err.Message;
            Assert.Equal("try-unwrap failed", msg);
        }
        else {
            Assert.Fail("expected failure");
        }
    }

    private sealed class DummyDisposable : IDisposable {
        public void Dispose() { }
    }
}
