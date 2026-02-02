using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Atelia.Data;

/// <summary>A chunked, reservable buffer writer (logical chunks backed by ArrayPool).</summary>
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
    private ChunkSizingStrategy _sizingStrategy;
    private readonly Action<string, string>? _debugLog;
    private readonly string _debugCategory;

    private readonly ArrayPool<byte> _pool;
    private readonly SlidingQueue<ReservableWriterChunk> _chunks = new(); // 抽离原 headIndex + Compact 模式

    // 当前写入状态
    private long _writtenLength;
    private long _flushedLength;

    private ReservableWriterChunk CreateChunk(int sizeHint) {
        int size = _sizingStrategy.ComputeChunkSize(sizeHint);
        byte[] buffer = _pool.Rent(size);
        if (buffer.Length < sizeHint) { throw new InvalidOperationException($"ArrayPool returned buffer length {buffer.Length} < requested {sizeHint}"); }
        var chunk = new ReservableWriterChunk { Buffer = buffer, DataEnd = 0, DataBegin = 0, IsRented = true };
        _chunks.Enqueue(chunk);

        _sizingStrategy.NotifyChunkCreated(size);
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

    /// <summary>Gets the number of active chunks (excluding recycled ones).</summary>
    private int GetActiveChunksCount() => _chunks.Count;

    /// <summary>Gets the last active chunk, or null if there are none.</summary>
    private bool TryGetLastActiveChunk([MaybeNullWhen(false)] out ReservableWriterChunk item) => _chunks.TryPeekLast(out item);

    /// <summary>Gets an enumerator for the active chunks.</summary>
    private IEnumerable<ReservableWriterChunk> GetActiveChunks() => _chunks;

    private ReservableWriterChunk EnsureSpace(int sizeHint) {
        ReservableWriterChunk? lastChunk;
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
        _sizingStrategy = new ChunkSizingStrategy(opt.MinChunkSize, opt.MaxChunkSize);
        _debugLog = opt.DebugLog;
        _debugCategory = string.IsNullOrWhiteSpace(opt.DebugCategory) ? "BinaryLog" : opt.DebugCategory;
    }

    private void Trace(string message) {
        var logger = _debugLog;
        if (logger is null) { return; }
        logger(_debugCategory, message);
    }

    #region Reservation
    private ReservationTracker _reservations = new();

    /// <summary>
    /// Checks and flushes the contiguous completed data from the beginning to the _innerWriter.
    /// This is the core logic for advancing the flushed data prefix.
    /// </summary>
    /// <returns>True if any data was flushed; otherwise, false.</returns>
    private bool FlushCommittedData() {
        ReservationEntry? firstReservation = _reservations.FirstPending;
        bool flushed = false;

        foreach (ReservableWriterChunk chunk in GetActiveChunks()) {
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

    /// <summary>Flushes a specified amount of data from a chunk to the _innerWriter.</summary>
    private void FlushChunkData(ReservableWriterChunk chunk, int length) {
        var dataToFlush = chunk.Buffer.AsSpan(chunk.DataBegin, length);
        var innerSpan = _innerWriter.GetSpan(length);
        dataToFlush.CopyTo(innerSpan);
        _innerWriter.Advance(length);

        chunk.DataBegin += length;
        _flushedLength += length;
        if (_debugLog is not null) {
            Trace($"Flushed {length} bytes, pending={PendingLength}");
        }
    }

    /// <summary>
    /// Tries to recycle chunks that have been fully flushed.
    /// Uses a head index to avoid O(n) for List.RemoveAt(0).
    /// </summary>
    private void TryRecycleFlushedChunks() {
        // 手动逐个检查并出队，确保在出队前归还池内缓冲区
        int recycled = 0;
        while (_chunks.TryPeekFirst(out var c) && c.IsFullyFlushed) {
            if (c.IsRented) {
                _pool.Return(c.Buffer);
            }

            _chunks.TryDequeue(out _); // 丢弃引用
            recycled++;
        }
        _chunks.Compact(); // 仍按阈值压缩
        if (recycled > 0 && _debugLog is not null) {
            Trace($"Recycled {recycled} chunks");
        }
        if (recycled > 0 && _chunks.IsEmpty && _debugLog is not null) {
            Trace("All buffered chunks recycled");
        }
    }

    /// <summary>Compacts the chunks list by removing recycled empty slots at the beginning.</summary>
    private void CompactChunksList() => _chunks.Compact();

    /// <summary>
    /// If there are no pending reservations and all chunks have been recycled,
    /// explicitly clear the list to restore passthrough mode.
    /// </summary>
    private void TryRestorePassthroughIfIdle() {
        if (_reservations.PendingCount == 0 && _chunks.IsEmpty) {
            // Clear() 会释放底层 List 容量引用
            _chunks.Clear();
            if (_debugLog is not null) {
                Trace("Passthrough mode restored");
            }
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
            if (_debugLog is not null) {
                Trace($"Advance passthrough count={count}, written={_writtenLength}");
            }
        }
        else {
            // With active reservations, update the current chunk's written position.
            ReservableWriterChunk? lastChunk;
            if (TryGetLastActiveChunk(out lastChunk)) {
                lastChunk.DataEnd += count;
                _writtenLength += count;
                bool flushed = FlushCommittedData();
                if (flushed) {
                    // Only try to recycle if a flush actually occurred.
                    TryRecycleFlushedChunks();
                    TryRestorePassthroughIfIdle();
                }
                else if (_debugLog is not null) {
                    Trace($"Advance buffered count={count}, pending={PendingLength}");
                }
            }
        }

        _hasLastSpan = false; // Clear the state to prevent reuse.
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() (or Advance(0)) before requesting another buffer."); }
        sizeHint = Math.Max(sizeHint, 1);

        // Passthrough mode
        if (GetActiveChunksCount() == 0) {
            Memory<byte> memory = _innerWriter.GetMemory(sizeHint);
            _lastSpanLength = memory.Length;
            _hasLastSpan = true;
            return memory;
        }

        // Buffered mode
        ReservableWriterChunk chunk = EnsureSpace(sizeHint);
        // Return the entire remaining free space of the current chunk (common pattern in IBufferWriter implementations)
        // to reduce the number of GetMemory/Advance roundtrips. This still satisfies the sizeHint contract.
        Memory<byte> availableMemory = chunk.Buffer.AsMemory(chunk.DataEnd, chunk.FreeSpace);
        _lastSpanLength = availableMemory.Length;
        _hasLastSpan = true;
        return availableMemory;
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() (or Advance(0)) before requesting another buffer."); }
        sizeHint = Math.Max(sizeHint, 1);

        // Passthrough mode
        if (GetActiveChunksCount() == 0) {
            Span<byte> span = _innerWriter.GetSpan(sizeHint);
            _lastSpanLength = span.Length;
            _hasLastSpan = true;
            return span;
        }

        // Buffered mode
        ReservableWriterChunk chunk = EnsureSpace(sizeHint);
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
    /// <summary>
    /// Reserves a contiguous buffer region for later backfilling.
    /// The returned span remains valid until the reservation is committed or the writer is reset/disposed,
    /// even if additional <see cref="GetSpan"/> or <see cref="ReserveSpan"/> calls allocate space after it.
    /// </summary>
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0) { throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive"); }
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() before ReserveSpan()."); }

        // Ensure there is a chunk with enough space.
        ReservableWriterChunk chunk = EnsureSpace(count);
        int offset = chunk.DataEnd;
        long logicalOffset = _writtenLength; // The logical offset where the reservation starts.

        // Add the reservation to the tracker.
        reservationToken = _reservations.Add(chunk, offset, count, logicalOffset, tag);

        // Update the chunk's state.
        chunk.DataEnd += count;
        _writtenLength += count;

        // Return the reserved span.
        if (_debugLog is not null) {
            Trace($"ReserveSpan token={reservationToken}, count={count}, tag={tag ?? string.Empty}, logicalOffset={logicalOffset}");
        }
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

        if (!_reservations.TryCommit(reservationToken)) { throw new InvalidOperationException("Invalid or already committed reservation token."); }

        // Check if we can now flush a contiguous block of data.
        if (_debugLog is not null) {
            Trace($"Commit token={reservationToken}, remaining={_reservations.PendingCount}");
        }
        bool flushed = FlushCommittedData();
        if (flushed) {
            TryRecycleFlushedChunks();
            TryRestorePassthroughIfIdle();
        }
    }
    #endregion

    #region IDisposable and Reset
    private bool _disposed = false;

    /// <summary>Resets the writer to its initial state, returning all rented buffers to the pool.</summary>
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
        _reservations.Clear();
        _writtenLength = 0;
        _flushedLength = 0;
        _hasLastSpan = false;
        _lastSpanLength = 0;
        // 不重置 _reservationSerial，避免 Reset 后旧 token 与新 token 发生整数复用造成误提交风险。
    }

    #region Core State Properties
    /// <summary>Gets the total number of bytes written or reserved. This is the logical length of the buffer.</summary>
    public long WrittenLength => _writtenLength;

    /// <summary>Gets the total number of bytes that have been successfully flushed to the underlying writer.</summary>
    public long FlushedLength => _flushedLength;

    /// <summary>Gets the number of bytes that have been written but not yet flushed (WrittenLength - FlushedLength).</summary>
    public long PendingLength => _writtenLength - _flushedLength;
    #endregion

    #region Diagnostic Properties
    /// <summary>Gets the number of reservations that are currently pending (not yet committed).</summary>
    public int PendingReservationCount => _reservations.PendingCount;

    /// <summary>
    /// Gets a value indicating whether the writer is currently in passthrough mode
    /// (i.e., no active reservations or internal buffering).
    /// This property is for diagnostic purposes and is not guaranteed to be thread-safe.
    /// </summary>
    public bool IsPassthrough => GetActiveChunksCount() == 0 && _reservations.PendingCount == 0;
    #endregion

    /// <summary>Releases all resources used by the writer, returning rented memory to the pool.</summary>
    public void Dispose() {
        if (!_disposed) {
            Reset();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    #endregion
}
