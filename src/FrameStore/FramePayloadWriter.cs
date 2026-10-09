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

    /// <inheritdoc/>
    /// <remarks>
    /// 保留 RBF writer 已写/预留的逻辑累计长度投影（包括内部 HeadLen），不是物理输出量。
    /// 需要统计本次写入/预留的逻辑增量时，应比较同一 Builder 操作前后的 Length 差值。
    /// </remarks>
    public long Length => GetLease().Builder.PayloadAndMeta.Length;

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
