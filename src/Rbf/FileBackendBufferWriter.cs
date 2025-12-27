using System.Buffers;

namespace Atelia.Rbf;

/// <summary>
/// High-performance IBufferWriter that appends directly to an IRbfFileBackend.
/// Uses ArrayPool for buffer reuse to avoid per-call allocations.
/// </summary>
/// <remarks>
/// This writer has "single outstanding buffer" semantics:
/// - After GetSpan/GetMemory, you MUST call Advance before requesting another buffer.
/// - Advance(0) cancels the outstanding buffer without writing.
/// - Advance(n&gt;0) writes buffer[0..n) to the backend via Append.
///
/// Designed to be the inner writer for ChunkedReservableWriter.
/// </remarks>
public sealed class FileBackendBufferWriter : IBufferWriter<byte>, IDisposable {
    private const int DefaultBufferSize = 256;

    private readonly IRbfFileBackend _backend;
    private readonly ArrayPool<byte> _pool;

    private byte[] _buffer;
    private int _bufferLength;
    private bool _hasOutstanding;
    private bool _disposed;

    public FileBackendBufferWriter(IRbfFileBackend backend)
        : this(backend, ArrayPool<byte>.Shared) {
    }

    public FileBackendBufferWriter(IRbfFileBackend backend, ArrayPool<byte> pool) {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(pool);
        _backend = backend;
        _pool = pool;
        _buffer = _pool.Rent(DefaultBufferSize);
        _bufferLength = _buffer.Length;
    }

    public long Position => _backend.Length;

    public void Flush() {
        _backend.Flush();
    }

    public void Advance(int count) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_hasOutstanding) { throw new InvalidOperationException("Advance called without a prior GetSpan/GetMemory."); }

        if ((uint)count > (uint)_bufferLength) { throw new ArgumentOutOfRangeException(nameof(count)); }

        if (count > 0) {
            _backend.Append(_buffer.AsSpan(0, count));
        }

        // Clear outstanding flag (count==0 is a cancel operation)
        _hasOutstanding = false;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasOutstanding) { throw new InvalidOperationException("A buffer is already outstanding. Call Advance before requesting another memory block."); }

        EnsureCapacity(sizeHint);
        _hasOutstanding = true;
        return _buffer.AsMemory(0, _bufferLength);
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasOutstanding) { throw new InvalidOperationException("A buffer is already outstanding. Call Advance before requesting another span."); }

        EnsureCapacity(sizeHint);
        _hasOutstanding = true;
        return _buffer.AsSpan(0, _bufferLength);
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;

        if (_buffer is not null) {
            _pool.Return(_buffer);
            _buffer = null!;
            _bufferLength = 0;
        }
        _hasOutstanding = false;
    }

    private void EnsureCapacity(int sizeHint) {
        if (sizeHint <= 0) { sizeHint = 1; }

        if (_bufferLength >= sizeHint) {
            // Current buffer is large enough
            return;
        }

        // Need a bigger buffer: return old one, rent new one
        var oldBuffer = _buffer;
        _buffer = _pool.Rent(sizeHint);
        _bufferLength = _buffer.Length;
        _pool.Return(oldBuffer);
    }
}
