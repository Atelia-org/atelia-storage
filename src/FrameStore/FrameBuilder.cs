using Atelia.FrameStore.Internal.Runtime;

namespace Atelia.FrameStore;

/// <summary>绑定 owner 与一次性共享租借的单帧构建器；所有值副本共享结束状态。</summary>
public readonly struct FrameBuilder : IDisposable {
    private readonly FrameLease? _lease;

    internal FrameBuilder(FrameLease lease) => _lease = lease;

    /// <summary>帧 Payload 与 TailMeta 的 owned 写入器。</summary>
    public FramePayloadWriter PayloadAndMeta {
        get {
            var lease = _lease ?? throw new InvalidOperationException("Builder is not initialized.");
            lease.EnsureWriterUsable();
            return new FramePayloadWriter(lease);
        }
    }

    /// <summary>提交帧；已知尺寸模式沿用 Begin 时声明的 TailMeta 长度。</summary>
    public AteliaResult<FrameAddress> EndAppend(uint tag) => EndAppendCore(tag, null);

    /// <summary>提交帧；已知尺寸模式要求 TailMeta 长度与 Begin 声明相同。</summary>
    public AteliaResult<FrameAddress> EndAppend(uint tag, int tailMetaLength) => EndAppendCore(tag, tailMetaLength);

    private AteliaResult<FrameAddress> EndAppendCore(uint tag, int? tailMetaLength) {
        if (_lease is null) { return new FrameStoreStateError("Builder is not initialized."); }
        return _lease.Owner.EndAppend(_lease, tag, tailMetaLength);
    }

    /// <summary>健康取消未完成构建；旧副本和已结束租借不重复取消。</summary>
    public void Dispose() {
        if (_lease is not null) { _lease.Owner.Cancel(_lease); }
    }
}
