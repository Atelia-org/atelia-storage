using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Atelia.Data;

/// <summary>
/// A chunked, reservable buffer writer (logical chunks backed by ArrayPool).
/// </summary>
/// <remarks>
/// This writer is designed for high-performance scenarios where data is written sequentially,
/// but some sections need to be reserved and filled in later (e.g., length prefixes in network protocols).
///
/// Core Goals:
/// 1. Provides IBufferWriter&lt;byte&gt; semantics (GetSpan/GetMemory + Advance).
/// 2. Allows explicit reservation of a buffer area for future writes via ReserveSpan() and Commit().
/// 3. Uses a pool of memory chunks (rented from ArrayPool&lt;byte&gt;) to minimize GC pressure.
/// 4. Enables efficient flushing of the contiguous, committed data prefix to an underlying writer.
/// 5. Ensures that any returned Span or Memory&lt;byte&gt; does not cross internal chunk boundaries.
///
/// Thread Safety: This class is not thread-safe and is intended for use by a single writer thread, similar to PipeWriter.
/// </remarks>
public class ChunkedReservableWriter : IReservableBufferWriter, IDisposable {
    #region Chunked Buffer
    // New sizing fields (byte-based)
    private readonly int _minChunkSize;
    private readonly int _maxChunkSize;
    private int _currentChunkTargetSize; // adaptive growth baseline
    private const double GrowthFactor = 2.0; // simple heuristic; could be optionized later
    private readonly bool _enforceStrictAdvance;

    private class Chunk {
        public byte[] Buffer = null!;
        public int DataEnd;
        public int DataBegin;
        public int FreeSpace => Buffer.Length - DataEnd;
        public int PendingData => DataEnd - DataBegin;
        public bool IsRented;
        public bool IsFullyFlushed => DataBegin == DataEnd;

        public Span<byte> GetAvailableSpan() => Buffer.AsSpan(DataEnd);
        public Span<byte> GetAvailableSpan(int maxLength) => Buffer.AsSpan(DataEnd, Math.Min(maxLength, FreeSpace));
    }

    private readonly ArrayPool<byte> _pool;
    private readonly SlidingQueue<Chunk> _chunks = new(); // 抽离原 headIndex + Compact 模式

    // 当前写入状态
    private long _writtenLength;
    private long _flushedLength;

    private Chunk CreateChunk(int sizeHint) {
        sizeHint = Math.Max(sizeHint, 1);
        // Determine base required size.
        int required = Math.Max(sizeHint, _minChunkSize);

        int size;
        if (required > _maxChunkSize) {
            // Oversized direct rent (do not influence adaptive target).
            size = required;
        }
        else {
            int candidate = Math.Max(required, _currentChunkTargetSize);
            // Round up to power of two (bounded) for better ArrayPool bucket locality.
            // candidate = RoundUpToPowerOfTwo(candidate);
            candidate = (int)BitOperations.RoundUpToPowerOf2((uint)candidate); // 由前面若干Math.Max确保candidate为正数。
            if (candidate > _maxChunkSize) {
                candidate = _maxChunkSize;
            }

            size = candidate;
        }

        byte[] buffer = _pool.Rent(size);
        if (buffer.Length < sizeHint) { throw new InvalidOperationException($"ArrayPool returned buffer length {buffer.Length} < requested {sizeHint}"); }
        var chunk = new Chunk { Buffer = buffer, DataEnd = 0, IsRented = true };
        _chunks.Enqueue(chunk);

        // Adaptive growth: if we allocated below max and prior target nearly fully used, grow.
        if (size <= _maxChunkSize && size < _maxChunkSize) {
            // Heuristic: grow when prior chunk target was reached (implicit since we just allocated with target >= required)
            if (_currentChunkTargetSize < _maxChunkSize) {
                long next = (long)(_currentChunkTargetSize * GrowthFactor);
                _currentChunkTargetSize = (int)Math.Min(next, _maxChunkSize);
            }
        }

        return chunk;
    }

    // private static int RoundUpToPowerOfTwo(int value) {
    //     // Clamp negative / zero
    //     if (value <= 2) return 2;
    //     // From BitOperations.RoundUpToPowerOf2 (netstandard polyfill style)
    //     value--;
    //     value |= value >> 1;
    //     value |= value >> 2;
    //     value |= value >> 4;
    //     value |= value >> 8;
    //     value |= value >> 16;
    //     value++;
    //     return value;
    // }

    /// <summary>
    /// Gets the number of active chunks (excluding recycled ones).
    /// </summary>
    private int GetActiveChunksCount() => _chunks.Count;

    /// <summary>
    /// Gets the last active chunk, or null if there are none.
    /// </summary>
    private bool TryGetLastActiveChunk([MaybeNullWhen(false)] out Chunk item) => _chunks.TryPeekLast(out item);

    /// <summary>
    /// Gets an enumerator for the active chunks.
    /// </summary>
    private IEnumerable<Chunk> GetActiveChunks() => _chunks;

    private Chunk EnsureSpace(int sizeHint) {
        Chunk? lastChunk;
        if (TryGetLastActiveChunk(out lastChunk) && lastChunk.FreeSpace >= sizeHint) { return lastChunk; }
        else { return CreateChunk(sizeHint); }
    }
    #endregion

    private readonly IBufferWriter<byte> _innerWriter;

    public ChunkedReservableWriter(IBufferWriter<byte> innerWriter, ArrayPool<byte>? pool = null)
    : this(innerWriter, new ChunkedReservableWriterOptions { Pool = pool }) { }

    public ChunkedReservableWriter(IBufferWriter<byte> innerWriter, ChunkedReservableWriterOptions? options) {
        _innerWriter = innerWriter ?? throw new ArgumentNullException(nameof(innerWriter));
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
        _enforceStrictAdvance = opt.EnforceStrictAdvance;
    }

    #region Reservation
    private class Reservation {
        public readonly Chunk Chunk;
        public readonly int Offset;
        public readonly int Length;
        public readonly long LogicalOffset; // The starting offset of the reserved area in the overall logical stream.
        public readonly string? Tag; // Optional debug annotation.

        public Reservation(Chunk chunk, int offset, int length, long logicalOffset, string? tag) {
            Chunk = chunk;
            Offset = offset;
            Length = length;
            LogicalOffset = logicalOffset;
            Tag = tag;
        }
    }

    /// <summary>
    /// Using a Dictionary&lt;int, LinkedListNode&lt;Reservation&gt;&gt; + LinkedList&lt;Reservation&gt;
    /// for fast lookups, ordered traversal, and quick removal.
    /// </summary>
    private readonly Dictionary<int, LinkedListNode<Reservation>> _tokenToNode = new();
    private readonly LinkedList<Reservation> _reservationOrder = new();


    private uint _reservationSerial;
    private int AllocReservationToken() {
        return (int)Bijection(++_reservationSerial);
    }
    public static uint Bijection(uint x) {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    /// <summary>
    /// Checks and flushes the contiguous completed data from the beginning to the _innerWriter.
    /// This is the core logic for advancing the flushed data prefix.
    /// </summary>
    /// <returns>True if any data was flushed; otherwise, false.</returns>
    private bool FlushCommittedData() {
        Reservation? firstReservation = _reservationOrder.First?.Value;
        bool flushed = false;

        foreach (Chunk chunk in GetActiveChunks()) {
            int flushableLength;

            if (firstReservation?.Chunk == chunk) {
                // This chunk contains the earliest uncommitted reservation.
                // We can only flush up to the start of this reservation.
                flushableLength = firstReservation.Offset - chunk.DataBegin;
                if (flushableLength > 0) {
                    FlushChunkData(chunk, flushableLength);
                    flushed = true;
                }
                break; // Subsequent chunks are blocked.
            }
            else {
                // This chunk has no pending reservations and can be fully flushed.
                flushableLength = chunk.DataEnd - chunk.DataBegin;
                if (flushableLength > 0) {
                    FlushChunkData(chunk, flushableLength);
                    flushed = true;
                }
                // Continue to the next chunk.
            }
        }
        return flushed;
    }

    /// <summary>
    /// Flushes a specified amount of data from a chunk to the _innerWriter.
    /// </summary>
    private void FlushChunkData(Chunk chunk, int length) {
        var dataToFlush = chunk.Buffer.AsSpan(chunk.DataBegin, length);
        var innerSpan = _innerWriter.GetSpan(length);
        dataToFlush.CopyTo(innerSpan);
        _innerWriter.Advance(length);

        chunk.DataBegin += length;
        _flushedLength += length;
    }

    /// <summary>
    /// Tries to recycle chunks that have been fully flushed.
    /// Uses a head index to avoid O(n) for List.RemoveAt(0).
    /// </summary>
    private void TryRecycleFlushedChunks() {
        // 手动逐个检查并出队，确保在出队前归还池内缓冲区
        while (_chunks.TryPeekFirst(out var c) && c.IsFullyFlushed) {
            if (c.IsRented) {
                _pool.Return(c.Buffer);
            }

            _chunks.TryDequeue(out _); // 丢弃引用
        }
        _chunks.Compact(); // 仍按阈值压缩
    }

    /// <summary>
    /// Compacts the chunks list by removing recycled empty slots at the beginning.
    /// </summary>
    private void CompactChunksList() => _chunks.Compact();

    /// <summary>
    /// If there are no pending reservations and all chunks have been recycled,
    /// explicitly clear the list to restore passthrough mode.
    /// </summary>
    private void TryRestorePassthroughIfIdle() {
        if (_reservationOrder.Count == 0 && _chunks.IsEmpty) {
            // Clear() 会释放底层 List 容量引用
            _chunks.Clear();
        }
    }
    #endregion

    #region IBufferWriter<byte>
    // State for Advance() validation, to avoid using a Span<byte> field.
    private int _lastSpanLength;
    private bool _hasLastSpan;

    public void Advance(int count) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count < 0) { throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative"); }
        if (count == 0) {
            // Treat Advance(0) as explicit cancellation of outstanding span.
            _hasLastSpan = false;
            _lastSpanLength = 0;
            return;
        }

        // Validate that count does not exceed the available space from the last GetSpan/GetMemory call.
        if (!_hasLastSpan || count > _lastSpanLength) { throw new ArgumentOutOfRangeException(nameof(count), "Count exceeds available space from the last buffer request."); }
        if (GetActiveChunksCount() == 0) {
            _innerWriter.Advance(count);
            _writtenLength += count;
            _flushedLength += count;
        }
        else {
            // With active reservations, update the current chunk's written position.
            Chunk? lastChunk;
            if (TryGetLastActiveChunk(out lastChunk)) {
                lastChunk.DataEnd += count;
                _writtenLength += count;
                bool flushed = FlushCommittedData();
                if (flushed) {
                    // Only try to recycle if a flush actually occurred.
                    TryRecycleFlushedChunks();
                    TryRestorePassthroughIfIdle();
                }
            }
        }

        _hasLastSpan = false; // Clear the state to prevent reuse.
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        sizeHint = Math.Max(sizeHint, 1);

        // Passthrough mode
        if (GetActiveChunksCount() == 0) {
            Memory<byte> memory = _innerWriter.GetMemory(sizeHint);
            _lastSpanLength = memory.Length;
            _hasLastSpan = true;
            return memory;
        }

        // Buffered mode
        Chunk chunk = EnsureSpace(sizeHint);
        // Return the entire remaining free space of the current chunk (common pattern in IBufferWriter implementations)
        // to reduce the number of GetMemory/Advance roundtrips. This still satisfies the sizeHint contract.
        Memory<byte> availableMemory = chunk.Buffer.AsMemory(chunk.DataEnd, chunk.FreeSpace);
        _lastSpanLength = availableMemory.Length;
        _hasLastSpan = true;
        return availableMemory;
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        sizeHint = Math.Max(sizeHint, 1);

        // Passthrough mode
        if (GetActiveChunksCount() == 0) {
            Span<byte> span = _innerWriter.GetSpan(sizeHint);
            _lastSpanLength = span.Length;
            _hasLastSpan = true;
            return span;
        }

        // Buffered mode
        Chunk chunk = EnsureSpace(sizeHint);
        Span<byte> availableSpan = chunk.GetAvailableSpan();

        // Ensure the returned span meets the size hint if possible within the current chunk.
        // If not, a new chunk should have already been created by EnsureSpace.
        if (availableSpan.Length < sizeHint) {
            chunk = CreateChunk(sizeHint);
            availableSpan = chunk.GetAvailableSpan();
        }

        _lastSpanLength = availableSpan.Length;
        _hasLastSpan = true;
        return availableSpan;
    }
    #endregion

    #region IReservableBufferWriter
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0) { throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive"); }
        if (_hasLastSpan) {
            if (_enforceStrictAdvance) { throw new InvalidOperationException("Previous buffer not advanced (strict mode). Call Advance() before ReserveSpan()."); }
            _hasLastSpan = false; // permissive discard
            _lastSpanLength = 0;
        }

        // Ensure there is a chunk with enough space.
        Chunk chunk = EnsureSpace(count);
        int offset = chunk.DataEnd;
        long logicalOffset = _writtenLength; // The logical offset where the reservation starts.

        // Create the reservation object.
        Reservation reservation = new Reservation(chunk, offset, count, logicalOffset, tag);
        reservationToken = AllocReservationToken();

        // Add it to the tracking data structures.
        LinkedListNode<Reservation> node = _reservationOrder.AddLast(reservation);
        _tokenToNode[reservationToken] = node;

        // Update the chunk's state.
        chunk.DataEnd += count;
        _writtenLength += count;

        // Return the reserved span.
        return chunk.Buffer.AsSpan(offset, count);
    }

    /// <summary>
    /// Commits a reservation, indicating that the reserved area has been filled.
    /// This allows the data to be included in future flushes.
    /// </summary>
    /// <param name="reservationToken">The token returned by ReserveSpan().</param>
    /// <exception cref="InvalidOperationException">Thrown if the token is invalid or already committed.</exception>
    public void Commit(int reservationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tokenToNode.TryGetValue(reservationToken, out LinkedListNode<Reservation>? node)) { throw new InvalidOperationException("Invalid or already committed reservation token."); }
        _reservationOrder.Remove(node);
        _tokenToNode.Remove(reservationToken);

        // Check if we can now flush a contiguous block of data.
        bool flushed = FlushCommittedData();
        if (flushed) {
            TryRecycleFlushedChunks();
            TryRestorePassthroughIfIdle();
        }
    }
    #endregion

    #region IDisposable and Reset
    private bool _disposed = false;

    /// <summary>
    /// Resets the writer to its initial state, returning all rented buffers to the pool.
    /// </summary>
    public void Reset() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 归还所有租借的缓冲区
        foreach (var c in _chunks) {
            if (c.IsRented) {
                _pool.Return(c.Buffer);
            }
        }
        _chunks.Clear();
        // Clear all state.
        _tokenToNode.Clear();
        _reservationOrder.Clear();
        _writtenLength = 0;
        _flushedLength = 0;
        _hasLastSpan = false;
        _lastSpanLength = 0;
        // 不重置 _reservationSerial，避免 Reset 后旧 token 与新 token 发生整数复用造成误提交风险。
    }

    #region Core State Properties
    /// <summary>
    /// Gets the total number of bytes written or reserved. This is the logical length of the buffer.
    /// </summary>
    public long WrittenLength => _writtenLength;

    /// <summary>
    /// Gets the total number of bytes that have been successfully flushed to the underlying writer.
    /// </summary>
    public long FlushedLength => _flushedLength;

    /// <summary>
    /// Gets the number of bytes that have been written but not yet flushed (WrittenLength - FlushedLength).
    /// </summary>
    public long PendingLength => _writtenLength - _flushedLength;
    #endregion

    #region Diagnostic Properties
    /// <summary>
    /// Gets the number of reservations that are currently pending (not yet committed).
    /// </summary>
    public int PendingReservationCount => _reservationOrder.Count;

    /// <summary>
    /// Gets the token of the first reservation that is blocking data from being flushed.
    /// Returns null if all data is committed and flushable.
    /// This property is useful for debugging data flow issues.
    /// </summary>
    public int? BlockingReservationToken => _reservationOrder.First is { } n ? _tokenToNode.First(kv => kv.Value == n).Key : (int?)null;

    /// <summary>
    /// Gets a value indicating whether the writer is currently in passthrough mode
    /// (i.e., no active reservations or internal buffering).
    /// This property is for diagnostic purposes and is not guaranteed to be thread-safe.
    /// </summary>
    public bool IsPassthrough => GetActiveChunksCount() == 0 && _reservationOrder.Count == 0;
    #endregion

    /// <summary>
    /// Releases all resources used by the writer, returning rented memory to the pool.
    /// </summary>
    public void Dispose() {
        if (!_disposed) {
            Reset();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    #endregion
}
