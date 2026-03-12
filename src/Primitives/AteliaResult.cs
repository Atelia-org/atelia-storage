// Source: Atelia.Primitives - 基础类型库
// Design: atelia/docs/Primitives/AteliaResult.md

namespace Atelia;

/// <summary>同步层结果类型，支持 ref struct 值。</summary>
/// <remarks>
/// 这是一个 <c>ref struct</c>，允许 <typeparamref name="T"/> 为 ref struct（如 <c>Span&lt;T&gt;</c>）。
///
/// 设计灵感来自 Rust 的 <c>Result&lt;T, E&gt;</c> 和 C# 的 <c>Nullable&lt;T&gt;</c>，
/// 但采用单泛型参数（<typeparamref name="T"/>）+ 基类 Error（<see cref="AteliaError"/>）的模式，
/// 以平衡类型安全和使用便利性。
///
/// 对于异步场景，请使用 <see cref="AsyncAteliaResult{T}"/>。
/// </remarks>
/// <typeparam name="T">成功值类型，允许 ref struct。</typeparam>
public ref struct AteliaResult<T> : IAteliaResult<T> where T : allows ref struct {
    private readonly AteliaError? _error;
    private readonly T? _value;

    /// <inheritdoc/>
    public bool IsSuccess => _error is null;

    /// <inheritdoc/>
    public bool IsFailure => _error is not null;

    /// <inheritdoc/>
    public T? Value => _value;

    /// <inheritdoc/>
    public AteliaError? Error => _error;

    private AteliaResult(T? value, AteliaError? error) {
        _value = value;
        _error = error;
    }

    /// <summary>创建一个表示成功的结果。</summary>
    /// <param name="value">成功的值。允许 <c>null</c>（表示"成功返回了空值"）。</param>
    /// <returns>表示成功的 <see cref="AteliaResult{T}"/>。</returns>
    public static AteliaResult<T> Success(T? value) => new(value, null);
    /// <summary>隐式转换：从 <typeparamref name="T"/> 值创建成功结果。</summary>
    public static implicit operator AteliaResult<T>(T value) => Success(value);

    /// <summary>创建一个表示失败的结果。</summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="AteliaResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static AteliaResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
    /// <summary>隐式转换：从 <see cref="AteliaError"/> 创建失败结果。</summary>
    public static implicit operator AteliaResult<T>(AteliaError error) => Failure(error);

    /// <inheritdoc/>
    public bool TryGetValue(out T? value) {
        value = _value;
        return IsSuccess;
    }

    /// <inheritdoc/>
    public bool TryGetError(out AteliaError? error) {
        error = _error;
        return IsFailure;
    }

    /// <inheritdoc/>
    public T? GetValueOrDefault(T? defaultValue = default) => IsSuccess ? _value : defaultValue;

    /// <inheritdoc/>
    public T GetValueOrThrow() {
        if (IsFailure) { throw new InvalidOperationException($"Cannot get value from a failed result. Error: [{_error!.ErrorCode}] {_error.Message}"); }
        return _value!;
    }
}

/// <summary><see cref="AteliaResult{T}"/> 的扩展方法。</summary>
public static class AteliaResultExtensions {
    /// <summary>转换为异步层类型。</summary>
    /// <typeparam name="T">成功值类型。此方法不支持 ref struct 类型。</typeparam>
    /// <param name="result">要转换的同步结果。</param>
    /// <remarks>
    /// 当 <typeparamref name="T"/> 为 ref struct 时，调用此方法将产生编译错误——这是期望行为。
    /// </remarks>
    /// <returns>等价的 <see cref="AsyncAteliaResult{T}"/>。</returns>
    public static AsyncAteliaResult<T> ToAsync<T>(this AteliaResult<T> result) {
        return result.IsSuccess
            ? AsyncAteliaResult<T>.Success(result.Value)
            : AsyncAteliaResult<T>.Failure(result.Error!);
    }
}
