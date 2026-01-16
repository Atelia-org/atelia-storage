// Source: Atelia.Primitives - 基础类型库
// Design: atelia/docs/Primitives/AteliaResult.md

namespace Atelia;

/// <summary>
/// 带资源所有权的结果类型。成功时持有需要 Dispose 的资源。
/// </summary>
/// <remarks>
/// <para>
/// 这是 <see cref="AteliaResult{T}"/> 的"资源所有权"变体。
/// 当 <typeparamref name="T"/> 是 <see cref="IDisposable"/> 类型时，
/// 可以使用 <c>using var result = ...</c> 语法，自动在作用域结束时释放资源。
/// </para>
/// <para>
/// <b>Dispose 语义</b>：
/// <list type="bullet">
/// <item>成功时：调用 <c>Value.Dispose()</c></item>
/// <item>失败时：静默无操作（Value 为 null）</item>
/// </list>
/// </para>
/// <para>
/// <b>使用方式</b>：
/// <code>
/// using var result = someApi.GetResource().ToDisposable();
/// if (result.IsFailure) { /* handle error */ }
/// var resource = result.Value;  // 安全使用
/// // result.Dispose() 自动调用 resource.Dispose()
/// </code>
/// </para>
/// </remarks>
/// <typeparam name="T">成功值类型，必须实现 <see cref="IDisposable"/>。</typeparam>
public sealed class DisposableAteliaResult<T> : IAteliaResult<T>, IDisposable
where T : class, IDisposable {
    private readonly T? _value;
    private readonly AteliaError? _error;
    private int _disposeState;

    /// <inheritdoc/>
    public bool IsSuccess => _error is null;

    /// <inheritdoc/>
    public bool IsFailure => _error is not null;

    /// <inheritdoc/>
    public T? Value => _value;

    /// <inheritdoc/>
    public AteliaError? Error => _error;

    private DisposableAteliaResult(T? value, AteliaError? error) {
        _value = value;
        _error = error;
    }

    /// <summary>
    /// 创建一个表示成功的结果。
    /// </summary>
    /// <param name="value">成功的值（不应为 null）。</param>
    /// <returns>表示成功的 <see cref="DisposableAteliaResult{T}"/>。</returns>
    public static DisposableAteliaResult<T> Success(T value) {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    /// <summary>
    /// 创建一个表示失败的结果。
    /// </summary>
    /// <param name="error">失败的错误。</param>
    /// <returns>表示失败的 <see cref="DisposableAteliaResult{T}"/>。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    public static DisposableAteliaResult<T> Failure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(null, error);
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
    public T GetValueOrThrow() {
        if (IsFailure) { throw new InvalidOperationException($"Cannot get value from a failed result. Error: [{_error!.ErrorCode}] {_error.Message}"); }
        return _value!;
    }

    /// <inheritdoc/>
    public T? GetValueOrDefault(T? defaultValue = default) => IsSuccess ? _value : defaultValue;

    /// <summary>
    /// 释放内部资源。成功时调用 Value.Dispose()，失败时无操作。
    /// </summary>
    public void Dispose() {
        if (System.Threading.Interlocked.Exchange(ref _disposeState, 1) != 0) { return; }
        _value?.Dispose();
    }
}

/// <summary>
/// <see cref="DisposableAteliaResult{T}"/> 的扩展方法。
/// </summary>
public static class DisposableResultExtensions {
    /// <summary>
    /// 将 <see cref="AteliaResult{T}"/> 转换为 <see cref="DisposableAteliaResult{T}"/>。
    /// </summary>
    /// <typeparam name="T">成功值类型，必须实现 <see cref="IDisposable"/>。</typeparam>
    /// <param name="result">要转换的结果。</param>
    /// <returns>等价的 <see cref="DisposableAteliaResult{T}"/>。</returns>
    /// <remarks>
    /// <para>转换后，资源所有权转移到 <see cref="DisposableAteliaResult{T}"/>。</para>
    /// <para>调用方应使用 <c>using var disposable = result.ToDisposable();</c> 确保资源释放。</para>
    /// </remarks>
    public static DisposableAteliaResult<T> ToDisposable<T>(this AteliaResult<T> result)
        where T : class, IDisposable {
        return result.IsSuccess
            ? DisposableAteliaResult<T>.Success(result.Value!)
            : DisposableAteliaResult<T>.Failure(result.Error!);
    }
}
