using System.Buffers;
using Atelia.Data;
using Atelia.Rbf.Internal;

namespace Atelia.Rbf;

/// <summary>RbfFrameBuilder 的写入器包装（epoch 保护）。</summary>
/// <remarks>
/// 该类型为 readonly struct，每次调用都会校验 epoch 与 File 状态，
/// 避免旧 writer 被长期持有并在错误时机写入。
/// 已知尺寸模式的 Advance/ReserveSpan 在消费前检查声明的合计额度；超限 Advance 保留当前借用供纠正。
/// GetSpan/GetMemory 不按剩余额度裁剪容量；借用后实际消费仍由 Advance 检查。
/// Length 保留底层累计长度的投影（包括内部 HeadLen），不能直接作为调用方已用或剩余额度。
/// </remarks>
public readonly struct RbfPayloadWriter : IReservableBufferWriter {
    private readonly RbfFileImpl? _owner;
    private readonly uint _epoch;

    internal RbfPayloadWriter(RbfFileImpl owner, uint epoch) {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _epoch = epoch;
    }

    private RbfFileImpl GetOwner() => _owner ?? throw new InvalidOperationException("Writer is not initialized.");

    private SinkReservableWriter GetWriter() => GetOwner().GetPayloadWriter(_epoch);

    /// <inheritdoc/>
    public long Length => GetWriter().Length;

    /// <inheritdoc/>
    public void Advance(int count) => GetOwner().AdvancePayload(_epoch, count);

    public Memory<byte> GetMemory(int sizeHint = 0) => GetWriter().GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => GetWriter().GetSpan(sizeHint);

    /// <inheritdoc/>
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) =>
        GetOwner().ReservePayloadSpan(_epoch, count, out reservationToken, tag);

    /// <inheritdoc/>
    public void Commit(int reservationToken) => GetWriter().Commit(reservationToken);

    /// <inheritdoc/>
    public bool TryGetReservedSpan(int reservationToken, out Span<byte> span) =>
        GetWriter().TryGetReservedSpan(reservationToken, out span);
}
