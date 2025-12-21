// Source: Atelia.Primitives - 基础类型库
// Design: agent-team/meeting/StateJournal/2025-12-21-hideout-loadobject-naming.md

namespace Atelia;

/// <summary>
/// 表示携带 <see cref="AteliaError"/> 的对象的接口。
/// </summary>
/// <remarks>
/// 用于统一异常和结构化错误的访问方式。
/// </remarks>
public interface IAteliaHasError {
    /// <summary>
    /// 获取关联的错误对象。
    /// </summary>
    AteliaError Error { get; }
}
