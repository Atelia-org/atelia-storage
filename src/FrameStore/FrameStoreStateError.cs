namespace Atelia.FrameStore;

/// <summary>FrameStore 拥有的对象或地址不满足当前操作的状态要求。</summary>
public sealed record FrameStoreStateError(string Message, string? RecoveryHint = null)
    : AteliaError("FrameStore.StateError", Message, RecoveryHint);
