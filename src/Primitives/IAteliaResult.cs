using System.Diagnostics.CodeAnalysis;

namespace Atelia;

/// <summary>
/// 定义统一的结果类型契约。
/// </summary>
/// <typeparam name="T">成功值的类型。</typeparam>
public interface IAteliaResult<T> where T : notnull, allows ref struct {
    /// <summary>
    /// 获取操作是否成功。NRT 流分析下：当返回 <c>true</c> 时，<see cref="Value"/> 视为非 null；当返回 <c>false</c> 时，<see cref="Error"/> 视为非 null。
    /// </summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    bool IsSuccess { get; }

    /// <summary>
    /// 获取操作是否失败。NRT 流分析下：当返回 <c>true</c> 时，<see cref="Error"/> 视为非 null。
    /// </summary>
    [MemberNotNullWhen(true, nameof(Error))]
    bool IsFailure { get; }

    /// <summary>
    /// 获取成功时的值。
    /// <para>当 <see cref="IsSuccess"/> 为 <c>false</c> 时，此属性为 <c>default</c>（结构体）或 <c>null</c>（引用类型）。</para>
    /// <para>当 <see cref="IsSuccess"/> 为 <c>true</c> 时，本字段保证非 null。</para>
    /// </summary>
    T? Value { get; }

    /// <summary>
    /// 获取失败时的错误。
    /// <para>当 <see cref="IsSuccess"/> 为 <c>true</c> 时，此属性应为 <c>null</c>。</para>
    /// <para>对于 struct 结果类型，<c>default(Result&lt;T&gt;)</c> 应视为失败，并通过此属性暴露内部的未初始化错误。</para>
    /// </summary>
    AteliaError? Error { get; }

    /// <summary>
    /// 失败时返回 <paramref name="fallback"/>，成功时返回成功值。
    /// </summary>
    T ValueOr(T fallback);

    /// <summary>
    /// 解包成功值；失败时抛 <see cref="InvalidOperationException"/>，异常信息包含 error code 与 message。
    /// </summary>
    /// <exception cref="InvalidOperationException">当结果表示失败时抛出。</exception>
    T Unwrap();

    /// <summary>
    /// 同时尝试解出值与错误。成功返回 <c>true</c> 并产出非 null 成功值；
    /// 失败返回 <c>false</c> 并产出非 null <paramref name="error"/>。
    /// </summary>
    bool TryUnwrap([MaybeNullWhen(false)] out T value, [NotNullWhen(false)] out AteliaError? error);

    /// <summary>
    /// 尝试获取错误。
    /// </summary>
    /// <param name="error">如果失败，包含错误；否则为 <c>null</c>。</param>
    /// <returns>如果失败（即包含错误）返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    bool TryGetError(out AteliaError? error);

    /// <summary>
    /// 尝试获取成功的值。
    /// </summary>
    /// <param name="value">如果成功，包含值；否则为 <c>default</c>。</param>
    /// <returns>如果成功返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    bool TryGetValue(out T? value);
}
