using Atelia.Rbf;

namespace Atelia.EventJournal;

public sealed class EventFrame : IDisposable {
    private RbfPooledFrame? _frame;

    internal EventFrame(EventAddress address, EventFrameHeader header, RbfPooledFrame frame) {
        Address = address;
        Header = header;
        _frame = frame;
    }

    public EventAddress Address { get; }
    public EventFrameHeader Header { get; }

    public ReadOnlySpan<byte> Payload {
        get {
            var frame = _frame ?? throw new ObjectDisposedException(nameof(EventFrame));
            return frame.PayloadAndMeta[..^frame.TailMetaLength];
        }
    }

    public void Dispose() {
        var frame = Interlocked.Exchange(ref _frame, null);
        frame?.Dispose();
    }
}
