using Atelia.Data;
using Atelia.FrameStore.Internal.Runtime;

namespace Atelia.FrameStore;

/// <summary>FrameBuilder 的 owned 写入器。复制此值不会建立新的租借。</summary>
public readonly struct FramePayloadWriter : IReservableBufferWriter {
    private readonly FrameLease? _lease;

    internal FramePayloadWriter(FrameLease lease) => _lease = lease;

    private FrameLease GetLease() {
        var lease = _lease ?? throw new InvalidOperationException("Writer is not initialized.");
        lease.EnsureWriterUsable();
        return lease;
    }

    /// <summary>本次 Builder 已预留和已 Advance 的用户累计 bytes，包含 Payload 与 TailMeta，从 0 开始。</summary>
    /// <remarks>
    /// ReserveSpan 成功后立即计入，Advance 按实际 count 计入；借用 buffer、回填及 Commit 不增加长度。
    /// known-size 的声明容量不计入，内部 framing 不计入；此值不是物理输出量。
    /// </remarks>
    public long Length => GetLease().PayloadAndMetaLength;

    /// <inheritdoc/>
    public void Advance(int count) {
        var lease = GetLease();
        lease.Builder.PayloadAndMeta.Advance(count);
        lease.HasUnadvancedBorrow = false;
    }

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0) {
        var lease = GetLease();
        var memory = lease.Builder.PayloadAndMeta.GetMemory(sizeHint);
        lease.HasUnadvancedBorrow = true;
        return memory;
    }

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0) {
        var lease = GetLease();
        var span = lease.Builder.PayloadAndMeta.GetSpan(sizeHint);
        lease.HasUnadvancedBorrow = true;
        return span;
    }

    /// <inheritdoc/>
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) =>
        GetLease().Builder.PayloadAndMeta.ReserveSpan(count, out reservationToken, tag);

    /// <inheritdoc/>
    public void Commit(int reservationToken) => GetLease().Builder.PayloadAndMeta.Commit(reservationToken);

    /// <inheritdoc/>
    public bool TryGetReservedSpan(int reservationToken, out Span<byte> span) =>
        GetLease().Builder.PayloadAndMeta.TryGetReservedSpan(reservationToken, out span);
}
