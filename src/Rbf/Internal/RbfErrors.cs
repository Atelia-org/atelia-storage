// RBF 模块专用错误类型
// 规范引用: task.md @Decision 4.B: 错误码分层设计

namespace Atelia.Rbf.Internal;

/// <summary>RBF 参数错误（Offset/Length 非对齐、越界等）。</summary>
internal sealed record RbfArgumentError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.ArgumentError", Message, RecoveryHint, Details, Cause);

/// <summary>RBF Framing 错误（HeadLen/TailLen 不匹配、Status 异常等）。</summary>
internal sealed record RbfFramingError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.FramingError", Message, RecoveryHint, Details, Cause);

/// <summary>RBF CRC 校验失败（数据损坏）。</summary>
internal sealed record RbfCrcMismatchError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.CrcMismatch", Message, RecoveryHint, Details, Cause);

/// <summary>RBF Buffer 长度不足错误。</summary>
internal sealed record RbfBufferTooSmallError : AteliaError {
    public int RequiredBytes { get; init; }
    public int ProvidedBytes { get; init; }

    public RbfBufferTooSmallError(
        string Message,
        int RequiredBytes,
        int ProvidedBytes,
        string? RecoveryHint = null,
        IReadOnlyDictionary<string, string>? Details = null,
        AteliaError? Cause = null
    ) : base(
        "Rbf.BufferTooSmall",
        Message,
        RecoveryHint,
        Details ?? new Dictionary<string, string> {
            ["RequiredBytes"] = RequiredBytes.ToString(),
            ["ProvidedBytes"] = ProvidedBytes.ToString()
        },
        Cause
    ) {
        this.RequiredBytes = RequiredBytes;
        this.ProvidedBytes = ProvidedBytes;
    }
}

/// <summary>RBF 状态违规错误（Builder 生命周期、重复提交等）。</summary>
/// <remarks>
/// 规范引用：方案 D - 将状态违规映射为 Result Failure 而非抛出异常。
/// 使用场景：
/// - 重复调用 EndAppend
/// - Builder 已 Dispose 后调用 EndAppend
/// - 存在未提交的 reservation
/// </remarks>
internal sealed record RbfStateError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("Rbf.StateError", Message, RecoveryHint, Details, Cause);
