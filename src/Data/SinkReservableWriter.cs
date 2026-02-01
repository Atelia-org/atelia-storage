using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Atelia.Data.Hashing;

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
    private ChunkSizingStrategy _sizingStrategy;

    private readonly Action<string, string>? _debugLog;
    private readonly string _debugCategory;

    private readonly ArrayPool<byte> _pool;
    private readonly SlidingQueue<ReservableWriterChunk> _chunks = new();

    private long _writtenLength;
    private long _pushedLength;

    private ReservableWriterChunk CreateChunk(int sizeHint) {
        int size = _sizingStrategy.ComputeChunkSize(sizeHint);
        byte[] buffer = _pool.Rent(size);
        if (buffer.Length < sizeHint) { throw new InvalidOperationException($"ArrayPool returned buffer length {buffer.Length} < requested {sizeHint}"); }

        var chunk = new ReservableWriterChunk { Buffer = buffer, DataEnd = 0, DataBegin = 0, IsRented = true };
        _chunks.Enqueue(chunk);

        _sizingStrategy.NotifyChunkCreated(size);
        return chunk;
    }

    private bool TryGetLastActiveChunk([MaybeNullWhen(false)] out ReservableWriterChunk item) => _chunks.TryPeekLast(out item);
    private IEnumerable<ReservableWriterChunk> GetActiveChunks() => _chunks;

    private ReservableWriterChunk EnsureSpace(int sizeHint) {
        if (TryGetLastActiveChunk(out var lastChunk) && lastChunk.FreeSpace >= sizeHint) { return lastChunk; }
        return CreateChunk(sizeHint);
    }
    #endregion

    private IByteSink _sink;

    public SinkReservableWriter(IByteSink sink, ArrayPool<byte>? pool = null)
        : this(sink, new ChunkedReservableWriterOptions { Pool = pool }) {
    }

    public SinkReservableWriter(IByteSink sink, ChunkedReservableWriterOptions? options) {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

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

    private bool FlushCommittedData() {
        ReservationEntry? firstReservation = _reservations.FirstPending;
        bool pushedAny = false;

        foreach (ReservableWriterChunk chunk in GetActiveChunks()) {
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

    private void PushChunkData(ReservableWriterChunk chunk, int length) {
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

        ReservableWriterChunk chunk = EnsureSpace(sizeHint);
        Memory<byte> mem = chunk.GetAvailableMemory();

        _lastSpanLength = mem.Length;
        _hasLastSpan = true;
        return mem;
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() (or Advance(0)) before requesting another buffer."); }
        sizeHint = Math.Max(sizeHint, 1);

        ReservableWriterChunk chunk = EnsureSpace(sizeHint);
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

        ReservableWriterChunk chunk = EnsureSpace(count);
        int offset = chunk.DataEnd;
        long logicalOffset = _writtenLength;

        reservationToken = _reservations.Add(chunk, offset, count, logicalOffset, tag);

        chunk.DataEnd += count;
        _writtenLength += count;

        if (_debugLog is not null) {
            Trace($"ReserveSpan token={reservationToken}, count={count}, tag={tag ?? string.Empty}, logicalOffset={logicalOffset}");
        }

        return chunk.Buffer.AsSpan(offset, count);
    }

    public void Commit(int reservationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_reservations.TryCommit(reservationToken, out _)) { throw new InvalidOperationException("Invalid or already committed reservation token."); }

        if (_debugLog is not null) {
            Trace($"Commit token={reservationToken}, remaining={_reservations.PendingCount}");
        }

        bool pushed = FlushCommittedData();
        if (pushed) {
            TryRecycleFlushedChunks();
        }
    }

    /// <summary>
    /// 获取指定 reservation 的可写 span（用于在 Commit 前回填数据）。
    /// </summary>
    /// <param name="reservationToken">由 <see cref="ReserveSpan"/> 返回的 token。</param>
    /// <param name="span">成功时返回 reservation 对应的 span；失败时为 default。</param>
    /// <returns>token 有效时返回 true，否则返回 false。</returns>
    /// <remarks>
    /// 返回的 span 在 Commit/Reset/Dispose 前有效。
    /// 此方法不改变 reservation 的状态（不执行 Commit）。
    /// </remarks>
    public bool TryGetReservationSpan(int reservationToken, out Span<byte> span) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_reservations.TryPeek(reservationToken, out var entry)) {
            span = default;
            return false;
        }

        span = entry.Chunk.Buffer.AsSpan(entry.Offset, entry.Length);
        return true;
    }
    #endregion

    #region Reset/Dispose
    private bool _disposed;

    /// <summary>
    /// Resets the writer to its initial state, returning all rented buffers to the pool.
    /// </summary>
    /// <param name="newSink">新的 Sink，若为 null 则保持当前 Sink</param>
    public void Reset(IByteSink? newSink = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var c in _chunks) {
            if (c.IsRented) {
                _pool.Return(c.Buffer);
            }
        }
        _chunks.Clear();

        _reservations.Clear();

        _writtenLength = 0;
        _pushedLength = 0;

        _hasLastSpan = false;
        _lastSpanLength = 0;
        // Do not reset _reservationSerial to avoid token reuse hazards.

        // 切换 sink（在清理完成后）
        if (newSink is not null) {
            _sink = newSink;
        }
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

    public int PendingReservationCount => _reservations.PendingCount;

    /// <summary>
    /// True if there are no pending reservations and no pending buffered bytes.
    /// </summary>
    public bool IsIdle => PendingLength == 0 && _reservations.PendingCount == 0;

    /// <summary>
    /// 计算从指定 pending reservation 末尾到当前已写入末尾之间所有字节的 CRC32C。
    /// </summary>
    /// <param name="reservationToken">必须是当前唯一的 pending reservation。</param>
    /// <param name="initValue">CRC 初始值（默认 0xFFFFFFFF）。</param>
    /// <param name="finalXor">CRC 最终异或值（默认 0xFFFFFFFF）。</param>
    /// <returns>计算得到的 CRC32C 值。</returns>
    /// <exception cref="ObjectDisposedException">writer 已 Dispose。</exception>
    /// <exception cref="InvalidOperationException">
    /// 存在未 Advance 的借用 span/memory；token 无效或已提交；存在多个 pending reservation。
    /// </exception>
    /// <remarks>
    /// 该方法不修改 writer 状态，不触发 flush。
    /// </remarks>
    public uint GetCrcSinceReservationEnd(
        int reservationToken,
        uint initValue = RollingCrc.DefaultInitValue,
        uint finalXor = RollingCrc.DefaultFinalXor
    ) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasLastSpan) {
            throw new InvalidOperationException(
                "Cannot compute CRC while a buffer is borrowed. Call Advance() or Advance(0) first."
            );
        }

        if (!_reservations.TryPeek(reservationToken, out var entry)) {
            throw new InvalidOperationException(
                "Invalid or already committed reservation token."
            );
        }

        if (_reservations.PendingCount != 1) {
            throw new InvalidOperationException(
                $"GetCrcSinceReservationEnd requires exactly 1 pending reservation, but found {_reservations.PendingCount}."
            );
        }

        // 起点：reservation 末尾
        var startChunk = entry.Chunk;
        int startOffset = entry.Offset + entry.Length;

        if (startOffset > startChunk.DataEnd) {
            throw new InvalidOperationException(
                "Reservation end offset exceeds written data end (internal error)."
            );
        }

        // Rolling CRC 计算
        uint crcRaw = initValue;
        bool started = false;

        foreach (var chunk in GetActiveChunks()) {
            if (!started) {
                if (chunk != startChunk) { continue; /* 跳过 reservation 之前的 chunk */ }
                started = true;

                // 第一个 chunk：从 reservation 末尾扫到 chunk.DataEnd
                if (startOffset < chunk.DataEnd) {
                    var span = chunk.Buffer.AsSpan(startOffset, chunk.DataEnd - startOffset);
                    crcRaw = RollingCrc.CrcForward(crcRaw, span);
                }
            }
            else {
                // 后续 chunk：从 DataBegin 扫到 DataEnd
                if (chunk.DataBegin < chunk.DataEnd) {
                    var span = chunk.Buffer.AsSpan(chunk.DataBegin, chunk.DataEnd - chunk.DataBegin);
                    crcRaw = RollingCrc.CrcForward(crcRaw, span);
                }
            }
        }

        if (!started) {
            throw new InvalidOperationException(
                "Reservation chunk not found in active chunks (internal error)."
            );
        }

        return crcRaw ^ finalXor;
    }
    #endregion
}
