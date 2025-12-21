// Source: Atelia.Primitives - 基础类型库
// Design: agent-team/meeting/StateJournal/2025-12-21-hideout-loadobject-naming.md

namespace Atelia;

/// <summary>
/// Atelia 项目的异常基类，桥接 <see cref="AteliaError"/> 和 .NET 异常体系。
/// </summary>
/// <remarks>
/// <para>
/// 此类实现 <see cref="IAteliaHasError"/> 接口，确保异常和结构化 Result 使用同一套错误协议。
/// </para>
/// <para>
/// 设计原则：
/// <list type="bullet">
///   <item><description>异常的 <see cref="Exception.Message"/> 来自 <see cref="Error"/>.<see cref="AteliaError.Message"/></description></item>
///   <item><description><see cref="ErrorCode"/> 便捷属性直接暴露错误码</description></item>
///   <item><description>满足 [A-ERROR-CODE-MUST] / [A-ERROR-MESSAGE-MUST] / [A-ERROR-RECOVERY-HINT-SHOULD] 条款</description></item>
/// </list>
/// </para>
/// </remarks>
public abstract class AteliaException : Exception, IAteliaHasError {
    /// <summary>
    /// 获取关联的错误对象。
    /// </summary>
    public AteliaError Error { get; }

    /// <summary>
    /// 获取错误码。
    /// </summary>
    /// <remarks>
    /// 便捷属性，等价于 <c>Error.ErrorCode</c>。
    /// </remarks>
    public string ErrorCode => Error.ErrorCode;

    /// <summary>
    /// 获取恢复建议。
    /// </summary>
    /// <remarks>
    /// 便捷属性，等价于 <c>Error.RecoveryHint</c>。
    /// </remarks>
    public string? RecoveryHint => Error.RecoveryHint;

    /// <summary>
    /// 使用指定的 <see cref="AteliaError"/> 初始化异常。
    /// </summary>
    /// <param name="error">关联的错误对象。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    protected AteliaException(AteliaError error)
        : base(error?.Message ?? throw new ArgumentNullException(nameof(error))) {
        Error = error;
    }

    /// <summary>
    /// 使用指定的 <see cref="AteliaError"/> 和内部异常初始化异常。
    /// </summary>
    /// <param name="error">关联的错误对象。</param>
    /// <param name="innerException">导致此异常的内部异常。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="error"/> 为 <c>null</c> 时抛出。</exception>
    protected AteliaException(AteliaError error, Exception? innerException)
        : base(error?.Message ?? throw new ArgumentNullException(nameof(error)), innerException) {
        Error = error;
    }
}
