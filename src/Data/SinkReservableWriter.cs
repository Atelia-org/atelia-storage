using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Atelia.Data.Binary;
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

    private long _length;
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
        _length += count;

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
    /// <inheritdoc/>
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0) { throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive"); }
        if (_hasLastSpan) { throw new InvalidOperationException("Previous buffer not advanced. Call Advance() before ReserveSpan()."); }

        ReservableWriterChunk chunk = EnsureSpace(count);
        int offset = chunk.DataEnd;
        long logicalOffset = _length;

        reservationToken = _reservations.Add(chunk, offset, count, logicalOffset, tag);

        chunk.DataEnd += count;
        _length += count;

        if (_debugLog is not null) {
            Trace($"ReserveSpan token={reservationToken}, count={count}, tag={tag ?? string.Empty}, logicalOffset={logicalOffset}");
        }

        return chunk.Buffer.AsSpan(offset, count);
    }

    /// <inheritdoc/>
    public void Commit(int reservationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_reservations.TryCommit(reservationToken)) { throw new InvalidOperationException("Invalid or already committed reservation token."); }

        if (_debugLog is not null) {
            Trace($"Commit token={reservationToken}, remaining={_reservations.PendingCount}");
        }

        bool pushed = FlushCommittedData();
        if (pushed) {
            TryRecycleFlushedChunks();
        }
    }

    /// <inheritdoc/>
    public bool TryGetReservedSpan(int reservationToken, out Span<byte> span) {
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

    /// <summary>Resets the writer to its initial state, returning all rented buffers to the pool.</summary>
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

        _length = 0;
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

    #region XOR Escape
    /// <summary>为指定 pending reservation 末尾到当前已写入末尾的完整后缀选择转义键，并原地 XOR。</summary>
    /// <param name="reservationToken">必须是当前唯一的 pending reservation。</param>
    /// <param name="fence">需要排除的对齐 LE word，必须大于等于 0x04000000。</param>
    /// <returns>用于后缀的 XOR 键；键自身也不会等于 <paramref name="fence"/>。</returns>
    /// <exception cref="ObjectDisposedException">writer 已 Dispose。</exception>
    /// <exception cref="InvalidOperationException">token 无效、存在多个 pending reservation 或未 Advance 的借用。</exception>
    /// <exception cref="ArgumentOutOfRangeException">Fence 或后缀长度不在支持的范围。</exception>
    /// <exception cref="ArgumentException">后缀长度没有按 4B 对齐。</exception>
    /// <remarks>
    /// 不修改 reservation、Length、PushedLength，不调用 Push、Commit 或 Reset。
    /// 输入必须在整个同步操作期间保持不变。随机数故障发生在 XOR 前并直接传播；
    /// 意外的变换异常不保证回滚，调用方应按其 owner 语义丢弃或重建缓冲。
    /// </remarks>
    public uint XorEscapeSinceReservationEnd(int reservationToken, uint fence) {
        return XorEscapeSinceReservationEnd(reservationToken, fence, randomCandidate: null);
    }

    // Per-call injection is restricted to the existing Data.Tests friend; production uses the system RNG.
    internal uint XorEscapeSinceReservationEnd(int reservationToken, uint fence, Func<uint>? randomCandidate) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        XorEscape.ValidateFence(fence);

        if (!_reservations.TryPeek(reservationToken, out var entry)) {
            throw new InvalidOperationException("Invalid or already committed reservation token.");
        }
        if (_reservations.PendingCount != 1) {
            throw new InvalidOperationException(
                $"XorEscapeSinceReservationEnd requires exactly 1 pending reservation, but found {_reservations.PendingCount}."
            );
        }
        if (_hasLastSpan) {
            throw new InvalidOperationException("Cannot escape while a buffer is borrowed. Call Advance() or Advance(0) first.");
        }

        int byteLength = XorEscape.ValidateByteLength(_length - entry.LogicalOffset - entry.Length);
        int startOffset = entry.Offset + entry.Length;
        if (startOffset < entry.Chunk.DataBegin || startOffset > entry.Chunk.DataEnd) {
            throw new InvalidOperationException("Reservation end is outside the active written data (internal error).");
        }

        int startChunkIndex = 0;
        while (startChunkIndex < _chunks.Count && _chunks[startChunkIndex] != entry.Chunk) { startChunkIndex++; }
        if (startChunkIndex == _chunks.Count) {
            throw new InvalidOperationException("Reservation chunk not found in active chunks (internal error).");
        }

        var source = new XorEscapeCursor(_chunks, startChunkIndex, startOffset, byteLength);
        uint key = XorEscape.SelectKey(fence, byteLength, source, randomCandidate);
        if (key == 0) { return key; }

        int bytePhase = 0;
        while (source.TryGetNextSpan(out var bytes)) {
            XorEscape.InPlace(bytes, key, bytePhase);
            bytePhase = (bytePhase + (bytes.Length & 3)) & 3;
        }
        return key;
    }

    // Each selector pass copies this value; no interface enumerator or pooled storage escapes the writer.
    private struct XorEscapeCursor : IXorEscapeSource {
        private readonly SlidingQueue<ReservableWriterChunk> _chunks;
        private readonly int _startChunkIndex;
        private readonly int _startOffset;
        private readonly int _chunkEndExclusive;
        private int _nextChunkIndex;
        private int _remaining;

        public XorEscapeCursor(SlidingQueue<ReservableWriterChunk> chunks, int startChunkIndex, int startOffset, int byteLength) {
            _chunks = chunks;
            _startChunkIndex = startChunkIndex;
            _startOffset = startOffset;
            _chunkEndExclusive = chunks.Count;
            _nextChunkIndex = startChunkIndex;
            _remaining = byteLength;
        }

        public bool TryGetNext(out ReadOnlySpan<byte> bytes) {
            bool found = TryGetNextSpan(out var writable);
            bytes = writable;
            return found;
        }

        public bool TryGetNextSpan(out Span<byte> bytes) {
            while (_remaining != 0 && _nextChunkIndex < _chunkEndExclusive) {
                int index = _nextChunkIndex++;
                var chunk = _chunks[index];
                int begin = index == _startChunkIndex ? _startOffset : chunk.DataBegin;
                int count = Math.Min(_remaining, chunk.DataEnd - begin);
                if (count == 0) { continue; }
                bytes = chunk.Buffer.AsSpan(begin, count);
                _remaining -= count;
                return true;
            }
            if (_remaining != 0) {
                throw new InvalidOperationException("The written suffix ended before its logical length (internal error).");
            }
            bytes = default;
            return false;
        }
    }
    #endregion

    #region Diagnostics
    /// <summary>Total logical bytes written or reserved.</summary>
    public long Length => _length;

    /// <summary>Total bytes pushed to the sink.</summary>
    public long PushedLength => _pushedLength;

    /// <summary>Bytes written but not yet pushed.</summary>
    public long PendingLength => _length - _pushedLength;

    public int PendingReservationCount => _reservations.PendingCount;

    /// <summary>True if there are no pending reservations and no pending buffered bytes.</summary>
    public bool IsIdle => PendingLength == 0 && _reservations.PendingCount == 0;

    /// <summary>计算从指定 pending reservation 末尾到当前已写入末尾之间所有字节的 CRC32C。</summary>
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
