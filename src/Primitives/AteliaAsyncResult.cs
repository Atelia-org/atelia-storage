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
public readonly struct AteliaAsyncResult<T> {
    private readonly AteliaError? _error;
    private readonly T? _value;

    /// <summary>
    /// 获取操作是否成功。从 <c>_error is null</c> 推导。
    /// </summary>
    public bool IsSuccess => _error is null;

    /// <summary>
    /// 获取操作是否失败。从 <c>_error is not null</c> 推导。
    /// </summary>
    public bool IsFailure => _error is not null;

    /// <summary>
    /// 获取成功时的值。当 <see cref="IsSuccess"/> 为 <c>false</c> 时，此属性为 <c>default</c>。
    /// </summary>
    public T? Value => _value;

    /// <summary>
    /// 获取失败时的错误。当 <see cref="IsSuccess"/> 为 <c>true</c> 时，此属性为 <c>null</c>。
    /// </summary>
    public AteliaError? Error => _error;

    private AteliaAsyncResult(T? value, AteliaError? error) {
        _value = value;
        _error = error;
    }

    /// <summary>
    /// 创建一个表示成功的结果。
    /// </summary>
    /// <param name="value">成功的值。允许 <c>null</c>（表示"成功返回了空值"）。</param>
    /// <returns>表示成功的 <see cref="AteliaAsyncResult{T}"/>。</returns>
    public static AteliaAsyncResult<T> Success(T? value) => new(value, null);

    /// <summary>
    /// 创建一个表示失败的结果。
    /// </summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="AteliaAsyncResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static AteliaAsyncResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }

    /// <summary>
    /// 尝试获取成功的值。
    /// </summary>
    /// <param name="value">如果成功，包含值；否则为 <c>default</c>。</param>
    /// <returns>如果成功返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public bool TryGetValue(out T? value) {
        value = _value;
        return IsSuccess;
    }

    /// <summary>
    /// 尝试获取错误。
    /// </summary>
    /// <param name="error">如果失败，包含错误；否则为 <c>null</c>。</param>
    /// <returns>如果失败返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public bool TryGetError(out AteliaError? error) {
        error = _error;
        return IsFailure;
    }

    /// <summary>
    /// 获取成功的值，如果失败则返回指定的默认值。
    /// </summary>
    /// <param name="defaultValue">失败时返回的默认值。</param>
    /// <returns>成功时返回 <see cref="Value"/>；失败时返回 <paramref name="defaultValue"/>。</returns>
    public T? GetValueOrDefault(T? defaultValue = default) => IsSuccess ? _value : defaultValue;

    /// <summary>
    /// 获取成功的值，如果失败则抛出异常。
    /// </summary>
    /// <returns>成功时返回 <see cref="Value"/>。</returns>
    /// <exception cref="InvalidOperationException">当结果表示失败时抛出。</exception>
    public T GetValueOrThrow() {
        if (IsFailure) {
            throw new InvalidOperationException(
                $"Cannot get value from a failed result. Error: [{_error!.ErrorCode}] {_error.Message}"
            );
        }

        return _value!;
    }

    /// <summary>
    /// 隐式转换：从 <typeparamref name="T"/> 值创建成功结果。
    /// </summary>
    public static implicit operator AteliaAsyncResult<T>(T value) => Success(value);
}
