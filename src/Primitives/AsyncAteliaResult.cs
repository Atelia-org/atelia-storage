// Source: Atelia.Primitives - 基础类型库
// Design: atelia/docs/Primitives/AteliaResult.md

using System.Diagnostics.CodeAnalysis;

namespace Atelia;

/// <summary>异步层结果类型，可用于 Task/ValueTask 返回值。</summary>
/// <remarks>
/// 这是一个 <c>readonly struct</c>，可用于 async 方法返回值。
///
/// 设计灵感来自 Rust 的 <c>Result&lt;T, E&gt;</c> 和 C# 的 <seealso cref="Nullable{T}"/>，
/// 但采用单泛型参数（<typeparamref name="T"/>）+ 基类 Error（<see cref="AteliaError"/>）的模式，
/// 以平衡类型安全和使用便利性。
///
/// 对于同步场景（特别是需要 ref struct 值类型），请使用 <see cref="AteliaResult{T}"/>。
/// </remarks>
/// <typeparam name="T">成功值类型。</typeparam>
public readonly struct AsyncAteliaResult<T> where T : notnull {
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

    private AsyncAteliaResult(T? value, AteliaError? error, bool isInitialized) {
        _value = value;
        _error = error;
        _isInitialized = isInitialized;
    }

    /// <summary>创建一个表示成功的结果。</summary>
    /// <param name="value">成功的值。调用方不得传入 null。</param>
    /// <returns>表示成功的 <see cref="AsyncAteliaResult{T}"/>。</returns>
    public static AsyncAteliaResult<T> Success(T value) {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null, true);
    }
    /// <summary>隐式转换：从 <typeparamref name="T"/> 值创建成功结果。</summary>
    public static implicit operator AsyncAteliaResult<T>(T value) => Success(value);

    /// <summary>创建一个表示失败的结果。</summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="AsyncAteliaResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static AsyncAteliaResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error, true);
    }
    /// <summary>隐式转换：从 <see cref="AteliaError"/> 创建失败结果。</summary>
    public static implicit operator AsyncAteliaResult<T>(AteliaError error) => Failure(error);

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
