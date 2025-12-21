// Source: Atelia.Primitives - 基础类型库
// Design: agent-team/meeting/StateJournal/2025-12-21-hideout-loadobject-naming.md

using System.Diagnostics.CodeAnalysis;

namespace Atelia;

/// <summary>
/// Atelia 项目的统一结果类型，表达操作的成功或失败。
/// </summary>
/// <remarks>
/// <para>
/// 这是一个 <c>readonly struct</c>，避免装箱开销。
/// </para>
/// <para>
/// 设计灵感来自 Rust 的 <c>Result&lt;T, E&gt;</c> 和 C# 的 <c>Nullable&lt;T&gt;</c>，
/// 但采用单泛型参数（<typeparamref name="T"/>）+ 基类 Error（<see cref="AteliaError"/>）的模式，
/// 以平衡类型安全和使用便利性。
/// </para>
/// </remarks>
/// <typeparam name="T">成功时的值类型。</typeparam>
public readonly struct AteliaResult<T> {
    /// <summary>
    /// 获取操作是否成功。
    /// </summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    /// <summary>
    /// 获取成功时的值。当 <see cref="IsSuccess"/> 为 <c>false</c> 时，此属性为 <c>default</c>。
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// 获取失败时的错误。当 <see cref="IsSuccess"/> 为 <c>true</c> 时，此属性为 <c>null</c>。
    /// </summary>
    public AteliaError? Error { get; }

    /// <summary>
    /// 获取操作是否失败。
    /// </summary>
    public bool IsFailure => !IsSuccess;

    private AteliaResult(bool isSuccess, T? value, AteliaError? error) {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    /// <summary>
    /// 创建一个表示成功的结果。
    /// </summary>
    /// <param name="value">成功的值。</param>
    /// <returns>表示成功的 <see cref="AteliaResult{T}"/>。</returns>
    public static AteliaResult<T> Success(T value) => new(true, value, null);

    /// <summary>
    /// 创建一个表示失败的结果。
    /// </summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="AteliaResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static AteliaResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, default, error);
    }

    /// <summary>
    /// 尝试获取成功的值。
    /// </summary>
    /// <param name="value">如果成功，包含值；否则为 <c>default</c>。</param>
    /// <returns>如果成功返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public bool TryGetValue([NotNullWhen(true)] out T? value) {
        value = Value;
        return IsSuccess;
    }

    /// <summary>
    /// 尝试获取错误。
    /// </summary>
    /// <param name="error">如果失败，包含错误；否则为 <c>null</c>。</param>
    /// <returns>如果失败返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public bool TryGetError([NotNullWhen(true)] out AteliaError? error) {
        error = Error;
        return !IsSuccess;
    }

    /// <summary>
    /// 获取成功的值，如果失败则返回指定的默认值。
    /// </summary>
    /// <param name="defaultValue">失败时返回的默认值。</param>
    /// <returns>成功时返回 <see cref="Value"/>；失败时返回 <paramref name="defaultValue"/>。</returns>
    public T? GetValueOrDefault(T? defaultValue = default) => IsSuccess ? Value : defaultValue;

    /// <summary>
    /// 获取成功的值，如果失败则抛出异常。
    /// </summary>
    /// <returns>成功时返回 <see cref="Value"/>。</returns>
    /// <exception cref="InvalidOperationException">当结果表示失败时抛出。</exception>
    public T GetValueOrThrow() {
        if (!IsSuccess) {
            throw new InvalidOperationException(
                $"Cannot get value from a failed result. Error: [{Error!.ErrorCode}] {Error.Message}"
            );
        }

        return Value;
    }

    /// <summary>
    /// 将成功的值映射为另一种类型。
    /// </summary>
    /// <typeparam name="TNew">新的值类型。</typeparam>
    /// <param name="mapper">值映射函数。</param>
    /// <returns>映射后的结果。如果原结果失败，则返回相同错误的失败结果。</returns>
    public AteliaResult<TNew> Map<TNew>(Func<T, TNew> mapper) {
        ArgumentNullException.ThrowIfNull(mapper);
        return IsSuccess
            ? AteliaResult<TNew>.Success(mapper(Value))
            : AteliaResult<TNew>.Failure(Error!);
    }

    /// <summary>
    /// 将成功的值映射为另一个结果。
    /// </summary>
    /// <typeparam name="TNew">新的值类型。</typeparam>
    /// <param name="mapper">值映射函数，返回新的结果。</param>
    /// <returns>映射后的结果。如果原结果失败，则返回相同错误的失败结果。</returns>
    public AteliaResult<TNew> FlatMap<TNew>(Func<T, AteliaResult<TNew>> mapper) {
        ArgumentNullException.ThrowIfNull(mapper);
        return IsSuccess
            ? mapper(Value)
            : AteliaResult<TNew>.Failure(Error!);
    }

    /// <summary>
    /// 执行模式匹配，根据成功或失败调用不同的函数。
    /// </summary>
    /// <typeparam name="TResult">返回值类型。</typeparam>
    /// <param name="onSuccess">成功时调用的函数。</param>
    /// <param name="onFailure">失败时调用的函数。</param>
    /// <returns>对应函数的返回值。</returns>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<AteliaError, TResult> onFailure) {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value) : onFailure(Error!);
    }

    /// <summary>
    /// 隐式转换：从 <typeparamref name="T"/> 值创建成功结果。
    /// </summary>
    public static implicit operator AteliaResult<T>(T value) => Success(value);
}
