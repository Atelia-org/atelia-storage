using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Atelia.Data;

/// <summary>
/// A chunked, reservable buffer writer (logical chunks backed by <see cref="ArrayPool{T}"/>)
/// which flushes committed data to a synchronous push sink (<see cref="IByteSink"/>).
/// </summary>
/// <remarks>
/// Design notes (draft):
/// - Always buffered: there is no passthrough mode.
/// - Reservation spans are always backed by pooled chunks owned by this instance, so their lifetime is stable
///   until <see cref="Commit"/>, <see cref="Reset"/>, or <see cref="Dispose"/>.
/// - Flushing is synchronous via <see cref="IByteSink.Push"/>; therefore a flushed region can be recycled
///   immediately after <see cref="Push"/> returns.
/// - This type keeps the same call-order constraints as <see cref="IReservableBufferWriter"/>.
///
/// Thread Safety: not thread-safe.
/// </remarks>
public sealed class SinkReservableWriter : IReservableBufferWriter, IDisposable {
    #region Chunked Buffer
    private readonly int _minChunkSize;
    private readonly int _maxChunkSize;
    private int _currentChunkTargetSize;
    private const double GrowthFactor = 2.0;

    private readonly Action<string, string>? _debugLog;
    private readonly string _debugCategory;

    private sealed class Chunk {
        public byte[] Buffer = null!;
        public int DataEnd;
        public int DataBegin;
        public bool IsRented;

        public int FreeSpace => Buffer.Length - DataEnd;
        public bool IsFullyFlushed => DataBegin == DataEnd;

        public Span<byte> GetAvailableSpan() => Buffer.AsSpan(DataEnd);
        public Memory<byte> GetAvailableMemory() => Buffer.AsMemory(DataEnd, FreeSpace);
    }

    private readonly ArrayPool<byte> _pool;
    private readonly SlidingQueue<Chunk> _chunks = new();

    private long _writtenLength;
    private long _pushedLength;

    private Chunk CreateChunk(int sizeHint) {
        sizeHint = Math.Max(sizeHint, 1);
        int required = Math.Max(sizeHint, _minChunkSize);

        int size;
        if (required > _maxChunkSize) {
            size = required; // oversize direct rent
        }
        else {
            int candidate = Math.Max(required, _currentChunkTargetSize);
            candidate = (int)BitOperations.RoundUpToPowerOf2((uint)candidate);
            if (candidate > _maxChunkSize) { candidate = _maxChunkSize; }
            size = candidate;
        }

        byte[] buffer = _pool.Rent(size);
        if (buffer.Length < sizeHint) { throw new InvalidOperationException($"ArrayPool returned buffer length {buffer.Length} < requested {sizeHint}"); }

        var chunk = new Chunk { Buffer = buffer, DataEnd = 0, DataBegin = 0, IsRented = true };
        _chunks.Enqueue(chunk);

        if (size < _maxChunkSize && _currentChunkTargetSize < _maxChunkSize) {
            long next = (long)(_currentChunkTargetSize * GrowthFactor);
            _currentChunkTargetSize = (int)Math.Min(next, _maxChunkSize);
        }

        return chunk;
    }

    private bool TryGetLastActiveChunk([MaybeNullWhen(false)] out Chunk item) => _chunks.TryPeekLast(out item);
    private IEnumerable<Chunk> GetActiveChunks() => _chunks;

    private Chunk EnsureSpace(int sizeHint) {
        if (TryGetLastActiveChunk(out var lastChunk) && lastChunk.FreeSpace >= sizeHint) { return lastChunk; }
        return CreateChunk(sizeHint);
    }
    #endregion

    private readonly IByteSink _sink;

    public SinkReservableWriter(IByteSink sink, ArrayPool<byte>? pool = null)
        : this(sink, new ChunkedReservableWriterOptions { Pool = pool }) {
    }

    public SinkReservableWriter(IByteSink sink, ChunkedReservableWriterOptions? options) {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        options ??= new ChunkedReservableWriterOptions();
        var opt = options.Clone();

        _pool = opt.Pool ?? ArrayPool<byte>.Shared;
        int minSize = opt.MinChunkSize;
        int maxSize = opt.MaxChunkSize;
        if (minSize < 1024) { throw new ArgumentException("MinChunkSize must be >= 1024", nameof(options)); }
        if (maxSize < minSize) { throw new ArgumentException("MaxChunkSize must be >= MinChunkSize", nameof(options)); }

        _minChunkSize = minSize;
        _maxChunkSize = maxSize;
        _currentChunkTargetSize = _minChunkSize;

        _debugLog = opt.DebugLog;
        _debugCategory = string.IsNullOrWhiteSpace(opt.DebugCategory) ? "BinaryLog" : opt.DebugCategory;
    }

    private void Trace(string message) {
        var logger = _debugLog;
        if (logger is null) { return; }
        logger(_debugCategory, message);
    }

    #region Reservation
    private sealed class Reservation {
        public readonly Chunk Chunk;
        public readonly int Offset;
        public readonly int Length;
        public readonly long LogicalOffset;
        public readonly string? Tag;

        public Reservation(Chunk chunk, int offset, int length, long logicalOffset, string? tag) {
            Chunk = chunk;
            Offset = offset;
            Length = length;
            LogicalOffset = logicalOffset;
            Tag = tag;
        }
    }

    private readonly Dictionary<int, LinkedListNode<Reservation>> _tokenToNode = new();
    private readonly LinkedList<Reservation> _reservationOrder = new();

    private uint _reservationSerial;

    private int AllocReservationToken() {
        return unchecked((int)Bijection(++_reservationSerial));
    }

    public static uint Bijection(uint x) {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    private bool FlushCommittedData() {
        Reservation? firstReservation = _reservationOrder.First?.Value;
        bool pushedAny = false;

        foreach (Chunk chunk in GetActiveChunks()) {
            int pushableLength;
            if (firstReservation?.Chunk == chunk) {
                pushableLength = firstReservation.Offset - chunk.DataBegin;
                if (pushableLength > 0) {
                    PushChunkData(chunk, pushableLength);
                    pushedAny = true;
                }
                break;
            }
            else {
                pushableLength = chunk.DataEnd - chunk.DataBegin;
                if (pushableLength > 0) {
                    PushChunkData(chunk, pushableLength);
                    pushedAny = true;
                }
            }
        }

        return pushedAny;
    }

    private void PushChunkData(Chunk chunk, int length) {
        // Important: If sink throws, do not advance DataBegin/_pushedLength.
        ReadOnlySpan<byte> dataToPush = chunk.Buffer.AsSpan(chunk.DataBegin, length);
        _sink.Push(dataToPush);

        chunk.DataBegin += length;
        _pushedLength += length;

        if (_debugLog is not null) {
            Trace($"Pushed {length} bytes, pending={PendingLength}");
        }
    }

    private void TryRecycleFlushedChunks() {
        int recycled = 0;
        while (_chunks.TryPeekFirst(out var c) && c.IsFullyFlushed) {
            if (c.IsRented) {
                _pool.Return(c.Buffer);
            }

            _chunks.TryDequeue(out _);
            recycled++;
        }
        _chunks.Compact();

        if (recycled > 0 && _debugLog is not null) {
            Trace($"Recycled {recycled} chunks");
        }
    }
    #endregion

    #region IBufferWriter<byte>
    private int _lastSpanLength;
    private bool _hasLastSpan;

    public void Advance(int count) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count < 0) { throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative"); }
        if (count == 0) {
            _hasLastSpan = false;
            _lastSpanLength = 0;
            return;
        }

        if (!_hasLastSpan || count > _lastSpanLength) { throw new ArgumentOutOfRangeException(nameof(count), "Count exceeds available space from the last buffer request."); }

        if (!TryGetLastActiveChunk(out var lastChunk)) {
            // Should not happen because we are always buffered.
            throw new InvalidOperationException("Internal error: no active chunk when advancing.");
        }

        lastChunk.DataEnd += count;
        _writtenLength += count;

        bool pushed = FlushCommittedData();
        if (pushed) {
            TryRecycleFlushedChunks();
        }
        else if (_debugLog is not null) {
            Trace($"Advance buffered count={count}, pending={PendingLength}");
        }

        _hasLastSpan = false;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() (or Advance(0)) before requesting another buffer."); }
        sizeHint = Math.Max(sizeHint, 1);

        Chunk chunk = EnsureSpace(sizeHint);
        Memory<byte> mem = chunk.GetAvailableMemory();

        _lastSpanLength = mem.Length;
        _hasLastSpan = true;
        return mem;
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() (or Advance(0)) before requesting another buffer."); }
        sizeHint = Math.Max(sizeHint, 1);

        Chunk chunk = EnsureSpace(sizeHint);
        Span<byte> span = chunk.GetAvailableSpan();

        if (span.Length < sizeHint) {
            chunk = CreateChunk(sizeHint);
            span = chunk.GetAvailableSpan();
        }

        _lastSpanLength = span.Length;
        _hasLastSpan = true;
        return span;
    }
    #endregion

    #region IReservableBufferWriter
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0) { throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive"); }
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() before ReserveSpan()."); }

        Chunk chunk = EnsureSpace(count);
        int offset = chunk.DataEnd;
        long logicalOffset = _writtenLength;

        var reservation = new Reservation(chunk, offset, count, logicalOffset, tag);
        reservationToken = AllocReservationToken();

        var node = _reservationOrder.AddLast(reservation);
        _tokenToNode[reservationToken] = node;

        chunk.DataEnd += count;
        _writtenLength += count;

        if (_debugLog is not null) {
            Trace($"ReserveSpan token={reservationToken}, count={count}, tag={tag ?? string.Empty}, logicalOffset={logicalOffset}");
        }

        return chunk.Buffer.AsSpan(offset, count);
    }

    public void Commit(int reservationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tokenToNode.TryGetValue(reservationToken, out var node)) { throw new InvalidOperationException("Invalid or already committed reservation token."); }

        _reservationOrder.Remove(node);
        _tokenToNode.Remove(reservationToken);

        if (_debugLog is not null) {
            Trace($"Commit token={reservationToken}, remaining={_reservationOrder.Count}");
        }

        bool pushed = FlushCommittedData();
        if (pushed) {
            TryRecycleFlushedChunks();
        }
    }
    #endregion

    #region Reset/Dispose
    private bool _disposed;

    /// <summary>
    /// Resets the writer to its initial state, returning all rented buffers to the pool.
    /// </summary>
    public void Reset() {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var c in _chunks) {
            if (c.IsRented) {
                _pool.Return(c.Buffer);
            }
        }
        _chunks.Clear();

        _tokenToNode.Clear();
        _reservationOrder.Clear();

        _writtenLength = 0;
        _pushedLength = 0;

        _hasLastSpan = false;
        _lastSpanLength = 0;
        // Do not reset _reservationSerial to avoid token reuse hazards.
    }

    public void Dispose() {
        if (_disposed) { return; }
        Reset();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Diagnostics
    /// <summary>
    /// Total logical bytes written or reserved.
    /// </summary>
    public long WrittenLength => _writtenLength;

    /// <summary>
    /// Total bytes pushed to the sink.
    /// </summary>
    public long PushedLength => _pushedLength;

    /// <summary>
    /// Bytes written but not yet pushed.
    /// </summary>
    public long PendingLength => _writtenLength - _pushedLength;

    public int PendingReservationCount => _reservationOrder.Count;

    /// <summary>
    /// True if there are no pending reservations and no pending buffered bytes.
    /// </summary>
    public bool IsIdle => PendingLength == 0 && _reservationOrder.Count == 0;
    #endregion
}
