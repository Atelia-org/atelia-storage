using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

public sealed class RbfFileBackend : IRbfFileBackend {
    private readonly FileStream _stream;

    private RbfFileBackend(FileStream stream) {
        _stream = stream;
        _stream.Position = _stream.Length;
    }

    public static RbfFileBackend Open(string filePath, RbfFileMode mode = RbfFileMode.OpenOrCreate) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fileMode = mode switch {
            RbfFileMode.Create => FileMode.Create,
            RbfFileMode.OpenOrCreate => FileMode.OpenOrCreate,
            RbfFileMode.Append => FileMode.OpenOrCreate,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        var stream = new FileStream(
            filePath,
            fileMode,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.RandomAccess
        );

        return new RbfFileBackend(stream);
    }

    public long Length => _stream.Length;

    public SafeFileHandle SafeFileHandle => _stream.SafeFileHandle;

    public int ReadAt(long position, Span<byte> buffer) {
        if (position < 0) { throw new ArgumentOutOfRangeException(nameof(position)); }
        if (buffer.Length == 0) { return 0; }
        return RandomAccess.Read(_stream.SafeFileHandle, buffer, position);
    }

    public void Append(ReadOnlySpan<byte> data) {
        if (data.Length == 0) { return; }

        _stream.Position = _stream.Length;
        _stream.Write(data);
    }

    public void Flush() {
        _stream.Flush(flushToDisk: false);
    }

    public void DurableFlush() {
        _stream.Flush(flushToDisk: true);
    }

    public void TruncateTo(long length) {
        if (length < 0) { throw new ArgumentOutOfRangeException(nameof(length)); }
        if (length > _stream.Length) { throw new ArgumentOutOfRangeException(nameof(length), "Cannot truncate beyond current file length."); }

        _stream.SetLength(length);
        if (_stream.Position > length) {
            _stream.Position = length;
        }
    }

    public void Dispose() {
        _stream.Dispose();
    }
}
