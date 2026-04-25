// Source: Atelia.Primitives - 基础类型库
// Design: atelia/docs/Primitives/AteliaResult.md

using System.Diagnostics.CodeAnalysis;

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
public ref struct AteliaResult<T> where T : notnull, allows ref struct {
    private readonly bool _isInitialized;
    private readonly AteliaError? _error;
    private readonly T? _value;

    /// <summary>是否为成功结果。</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => _isInitialized && _error is null;

    /// <summary>是否为失败结果。</summary>
    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    /// <summary>成功时返回值；失败时返回 <c>default</c>。</summary>
    public T? Value => _value;

    /// <summary>失败时返回错误；成功时返回 <c>null</c>。</summary>
    public AteliaError? Error => IsSuccess ? null : _error ?? ResultContractErrors.UninitializedResult;

    private AteliaResult(T? value, AteliaError? error, bool isInitialized) {
        _value = value;
        _error = error;
        _isInitialized = isInitialized;
    }

    /// <summary>创建一个表示成功的结果。</summary>
    /// <param name="value">成功的值。调用方不得传入 null。</param>
    /// <returns>表示成功的 <see cref="AteliaResult{T}"/>。</returns>
    public static AteliaResult<T> Success(T value) {
        if (value is null) { throw new ArgumentNullException(nameof(value)); }
        return new(value, null, true);
    }
    /// <summary>隐式转换：从 <typeparamref name="T"/> 值创建成功结果。</summary>
    public static implicit operator AteliaResult<T>(T value) => Success(value);

    /// <summary>创建一个表示失败的结果。</summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="AteliaResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static AteliaResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error, true);
    }
    /// <summary>隐式转换：从 <see cref="AteliaError"/> 创建失败结果。</summary>
    public static implicit operator AteliaResult<T>(AteliaError error) => Failure(error);

    /// <summary>尝试获取成功值。</summary>
    public bool TryGetValue(out T? value) {
        value = _value;
        return IsSuccess;
    }

    /// <summary>尝试获取错误。</summary>
    public bool TryGetError(out AteliaError? error) {
        error = Error;
        return IsFailure;
    }

    /// <summary>成功时返回值；失败时返回 <paramref name="fallback"/>。</summary>
    public T ValueOr(T fallback) => IsSuccess ? _value! : fallback;

    /// <summary>解包成功值；失败时抛出异常。</summary>
    public T Unwrap() {
        if (IsFailure) {
            throw ResultContractErrors.CreateUnwrapFailure(Error!);
        }
        return _value!;
    }

    /// <summary>同时尝试解出成功值与错误。</summary>
    public bool TryUnwrap([MaybeNullWhen(false)] out T value, [NotNullWhen(false)] out AteliaError? error) {
        value = _value!;
        error = Error;
        return IsSuccess;
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
    public static AsyncAteliaResult<T> ToAsync<T>(this AteliaResult<T> result) where T : notnull {
        return result.IsSuccess
            ? AsyncAteliaResult<T>.Success(result.Value)
            : AsyncAteliaResult<T>.Failure(result.Error!);
    }
}
