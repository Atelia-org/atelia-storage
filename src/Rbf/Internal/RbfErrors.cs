// RBF 模块专用错误类型
// 规范引用: task.md @Decision 4.B: 错误码分层设计

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 参数错误（Offset/Length 非对齐、越界等）。
/// </summary>
internal sealed record RbfArgumentError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.ArgumentError", Message, RecoveryHint, Details, Cause);

/// <summary>
/// RBF Framing 错误（HeadLen/TailLen 不匹配、Status 异常等）。
/// </summary>
internal sealed record RbfFramingError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.FramingError", Message, RecoveryHint, Details, Cause);

/// <summary>
/// RBF CRC 校验失败（数据损坏）。
/// </summary>
internal sealed record RbfCrcMismatchError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.CrcMismatch", Message, RecoveryHint, Details, Cause);
