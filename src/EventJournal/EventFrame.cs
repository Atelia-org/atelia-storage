using System.Buffers;
using Atelia.Rbf;

namespace Atelia.EventJournal;

public sealed class EventFrame : IDisposable {
    private RbfPooledFrame? _frame;
    private byte[]? _decodedPayloadBuffer;
    private readonly int _decodedPayloadLength;
    private bool _disposed;

    internal EventFrame(EventAddress address, EventFrameHeader header, RbfPooledFrame frame) {
        Address = address;
        Header = header;
        _frame = frame;
    }

    internal EventFrame(EventAddress address, EventFrameHeader header, RbfPooledFrame frame, byte[] decodedPayloadBuffer, int decodedPayloadLength) {
        Address = address;
        Header = header;
        _frame = frame;
        _decodedPayloadBuffer = decodedPayloadBuffer;
        _decodedPayloadLength = decodedPayloadLength;
    }

    public EventAddress Address { get; }
    public EventFrameHeader Header { get; }

    public ReadOnlySpan<byte> Payload {
        get {
            if (_disposed) { throw new ObjectDisposedException(nameof(EventFrame)); }

            if (_decodedPayloadBuffer is { } decodedPayloadBuffer) { return decodedPayloadBuffer.AsSpan(0, _decodedPayloadLength); }

            var frame = _frame ?? throw new ObjectDisposedException(nameof(EventFrame));
            return frame.PayloadAndMeta[..^frame.TailMetaLength];
        }
    }

    public void Dispose() {
        _disposed = true;

        var frame = Interlocked.Exchange(ref _frame, null);
        frame?.Dispose();

        var decodedPayloadBuffer = Interlocked.Exchange(ref _decodedPayloadBuffer, null);
        if (decodedPayloadBuffer is not null) { ArrayPool<byte>.Shared.Return(decodedPayloadBuffer); }
    }
}
