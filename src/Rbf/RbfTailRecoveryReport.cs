using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>Successful ordinary open's single terminal-frame recovery action.</summary>
public enum RbfTailRecoveryAction {
    None,
    CompletedFence,
    CompletedTombstone,
    Truncated
}

/// <summary>One successful open's physical repair result; it does not establish business publication.</summary>
/// <param name="Action">Performed action, or None for a closed file.</param>
/// <param name="OriginalLength">Physical length captured before qualification.</param>
/// <param name="FinalLength">Validated closed physical length returned to the caller.</param>
/// <param name="AffectedFrameOffset">Terminal frame start; null when Action is None.</param>
/// <param name="FrameTicket">Preserved or completed frame; null for None or Truncated.</param>
public readonly record struct RbfTailRecoveryReport(
    RbfTailRecoveryAction Action,
    long OriginalLength,
    long FinalLength,
    long? AffectedFrameOffset,
    SizedPtr? FrameTicket);
