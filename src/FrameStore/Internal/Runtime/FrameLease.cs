using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Runtime;

/// <summary>每次 Begin 独立分配的共享身份，不池化、不复用。</summary>
internal sealed class FrameLease(FrameStoreCore owner, FrameFileEntry entry) {
    internal readonly FrameStoreCore Owner = owner;
    internal readonly FrameFileEntry Entry = entry;
    internal RbfFrameBuilder Builder;
    internal bool Active;
    internal bool Counted;
    internal bool HasUnadvancedBorrow;
    private long? _payloadAndMetaLengthOrigin;

    internal bool IsCurrent => Active && ReferenceEquals(Entry.CurrentLease, this);

    // Capture before the first writer escapes, so an initial Length read after writing
    // and every Builder/Writer alias observe the same user-byte origin.
    internal void CapturePayloadAndMetaLengthOrigin() =>
        _payloadAndMetaLengthOrigin ??= Builder.PayloadAndMeta.Length;

    internal long PayloadAndMetaLength => Builder.PayloadAndMeta.Length - _payloadAndMetaLengthOrigin!.Value;

    internal void EnsureWriterUsable() {
        Owner.EnsureUsable();
        if (!IsCurrent) { throw new InvalidOperationException("The FrameStore lease is no longer active."); }
    }
}
