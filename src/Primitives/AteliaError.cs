// Source: Atelia.Primitives - 基础类型库
// Design: agent-team/meeting/StateJournal/2025-12-21-hideout-loadobject-naming.md

namespace Atelia;

/// <summary>
/// Atelia 项目的错误基类，用于统一跨组件的错误表达。
/// </summary>
/// <remarks>
/// <para>
/// 设计原则：
/// <list type="bullet">
///   <item><description><see cref="ErrorCode"/> 是机器可判定的稳定键，用于代码分支和文档索引</description></item>
///   <item><description><see cref="Message"/> 默认面向 Agent（LLM 可读），包含因果链和上下文</description></item>
///   <item><description><see cref="RecoveryHint"/> 提供恢复建议，告诉 Agent 下一步可以做什么</description></item>
///   <item><description><see cref="Cause"/> 支持错误链，最多 5 层深度</description></item>
/// </list>
/// </para>
/// <para>
/// 派生类用于库内部的强类型便利；对外协议面只依赖基类字段（可序列化、跨语言）。
/// </para>
/// </remarks>
/// <param name="ErrorCode">
/// [MUST] 机器可判定的错误码，格式建议为 <c>{Component}.{ErrorName}</c>。
/// </param>
/// <param name="Message">
/// [MUST] Agent-Friendly 的错误描述，包含因果链和上下文信息。
/// </param>
/// <param name="RecoveryHint">
/// [SHOULD] 恢复建议，告诉调用方（尤其是 LLM Agent）下一步可以尝试什么。
/// </param>
/// <param name="Details">
/// [MAY] 键值对形式的上下文信息。复杂结构可使用 JSON-in-string。
/// </param>
/// <param name="Cause">
/// [MAY] 导致此错误的原因错误，支持错误链。建议最多 5 层深度。
/// </param>
public abstract record AteliaError(
    string ErrorCode,
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) {
    /// <summary>
    /// 验证 <see cref="Cause"/> 链的深度是否超过限制。
    /// </summary>
    /// <param name="maxDepth">允许的最大深度，默认为 5。</param>
    /// <returns>如果链深度超过限制返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public bool IsCauseChainTooDeep(int maxDepth = 5) {
        var depth = 0;
        var current = Cause;
        while (current is not null) {
            depth++;
            if (depth > maxDepth) { return true; }

            current = current.Cause;
        }

        return false;
    }

    /// <summary>
    /// 获取 <see cref="Cause"/> 链的深度。
    /// </summary>
    /// <returns>链的深度，0 表示没有 Cause。</returns>
    public int GetCauseChainDepth() {
        var depth = 0;
        var current = Cause;
        while (current is not null) {
            depth++;
            current = current.Cause;
        }

        return depth;
    }
}
