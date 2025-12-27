namespace Atelia.Rbf;

public sealed class RbfFileFramer : IRbfFramer, IDisposable {
    private readonly RbfFramer _framer;
    private readonly FileBackendBufferWriter _writer;

    public RbfFileFramer(string filePath, RbfFileMode mode = RbfFileMode.OpenOrCreate) {
        Backend = RbfFileBackend.Open(filePath, mode);
        _writer = new FileBackendBufferWriter(Backend);

        var startPosition = Backend.Length;
        var writeGenesis = startPosition == 0;
        _framer = new RbfFramer(_writer, startPosition: startPosition, writeGenesis: writeGenesis);
    }

    public IRbfFileBackend Backend { get; }

    public long Position => _framer.Position;

    public Address64 Append(FrameTag tag, ReadOnlySpan<byte> payload) {
        using var builder = BeginFrame(tag);
        if (!payload.IsEmpty) {
            var span = builder.Payload.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.Payload.Advance(payload.Length);
        }
        return builder.Commit();
    }

    public RbfFrameBuilder BeginFrame(FrameTag tag) {
        return _framer.BeginFrame(tag);
    }

    public void Flush() {
        _writer.Flush();
    }

    public void Dispose() {
        Flush();
        _writer.Dispose();
        Backend.Dispose();
    }
}
