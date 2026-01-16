namespace Atelia;

/// <summary>
/// 定义统一的结果类型契约。
/// </summary>
/// <typeparam name="T">成功值的类型。</typeparam>
public interface IAteliaResult<T> where T : allows ref struct {
    /// <summary>
    /// 获取操作是否成功。
    /// </summary>
    bool IsSuccess { get; }

    /// <summary>
    /// 获取操作是否失败。
    /// </summary>
    bool IsFailure { get; }

    /// <summary>
    /// 获取成功时的值。
    /// <para>当 <see cref="IsSuccess"/> 为 <c>false</c> 时，此属性的行为取决于具体实现（通常为 <c>default</c> 或 <c>null</c>）。</para>
    /// </summary>
    T? Value { get; }

    /// <summary>
    /// 获取失败时的错误。
    /// <para>当 <see cref="IsSuccess"/> 为 <c>true</c> 时，此属性应为 <c>null</c>。</para>
    /// </summary>
    AteliaError? Error { get; }

    /// <summary>
    /// 获取成功的值，如果失败则返回指定的默认值。
    /// </summary>
    /// <param name="defaultValue">失败时返回的默认值。</param>
    /// <returns>成功时返回 <see cref="Value"/>；失败时返回 <paramref name="defaultValue"/>。</returns>
    T? GetValueOrDefault(T? defaultValue = default);

    /// <summary>
    /// 获取成功的值，如果失败则抛出异常。
    /// </summary>
    /// <returns>成功时返回 <see cref="Value"/>。</returns>
    /// <exception cref="InvalidOperationException">当结果表示失败时抛出。</exception>
    T GetValueOrThrow();

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
