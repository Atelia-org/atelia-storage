// Source: Atelia.Primitives - 基础类型库
// Design: atelia/docs/Primitives/AteliaResult.md

namespace Atelia;

/// <summary>
/// 异步层结果类型，可用于 Task/ValueTask 返回值。
/// </summary>
/// <remarks>
/// <para>
/// 这是一个 <c>readonly struct</c>，可用于 async 方法返回值。
/// </para>
/// <para>
/// 设计灵感来自 Rust 的 <c>Result&lt;T, E&gt;</c> 和 C# 的 <c>Nullable&lt;T&gt;</c>，
/// 但采用单泛型参数（<typeparamref name="T"/>）+ 基类 Error（<see cref="AteliaError"/>）的模式，
/// 以平衡类型安全和使用便利性。
/// </para>
/// <para>
/// 对于同步场景（特别是需要 ref struct 值类型），请使用 <see cref="AteliaResult{T}"/>。
/// </para>
/// </remarks>
/// <typeparam name="T">成功值类型。</typeparam>
public readonly struct AsyncAteliaResult<T> : IAteliaResult<T> {
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

    private AsyncAteliaResult(T? value, AteliaError? error) {
        _value = value;
        _error = error;
    }

    /// <summary>
    /// 创建一个表示成功的结果。
    /// </summary>
    /// <param name="value">成功的值。允许 <c>null</c>（表示"成功返回了空值"）。</param>
    /// <returns>表示成功的 <see cref="AsyncAteliaResult{T}"/>。</returns>
    public static AsyncAteliaResult<T> Success(T? value) => new(value, null);

    /// <summary>
    /// 创建一个表示失败的结果。
    /// </summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="AsyncAteliaResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static AsyncAteliaResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }

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

    /// <summary>
    /// 隐式转换：从 <typeparamref name="T"/> 值创建成功结果。
    /// </summary>
    public static implicit operator AsyncAteliaResult<T>(T value) => Success(value);
}
