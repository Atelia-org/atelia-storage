using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

public interface IRbfFileBackend : IDisposable {
    long Length { get; }

    SafeFileHandle SafeFileHandle { get; }

    int ReadAt(long position, Span<byte> buffer);

    void Append(ReadOnlySpan<byte> data);

    void Flush();

    void DurableFlush();

    void TruncateTo(long length);
}
