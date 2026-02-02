using System.Buffers;
using Atelia.Data;
using Atelia.Rbf.Internal;

namespace Atelia.Rbf;

/// <summary>RbfFrameBuilder 的写入器包装（epoch 保护）。</summary>
/// <remarks>
/// 该类型为 readonly struct，每次调用都会校验 epoch 与 File 状态，
/// 避免旧 writer 被长期持有并在错误时机写入。
/// </remarks>
public readonly struct RbfPayloadWriter : IReservableBufferWriter {
    private readonly RbfFileImpl? _owner;
    private readonly uint _epoch;

    internal RbfPayloadWriter(RbfFileImpl owner, uint epoch) {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _epoch = epoch;
    }

    private SinkReservableWriter GetWriter() {
        var owner = _owner ?? throw new InvalidOperationException("Writer is not initialized.");
        return owner.GetPayloadWriter(_epoch);
    }

    public void Advance(int count) => GetWriter().Advance(count);

    public Memory<byte> GetMemory(int sizeHint = 0) => GetWriter().GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => GetWriter().GetSpan(sizeHint);

    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) =>
        GetWriter().ReserveSpan(count, out reservationToken, tag);

    public void Commit(int reservationToken) => GetWriter().Commit(reservationToken);
}
