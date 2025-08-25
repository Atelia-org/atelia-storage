using System.Buffers; // RuntimeHelpers.GetHashCode(obj);

// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Text;
// using System.Threading.Tasks;
// using System.Runtime.CompilerServices;
// using System.Runtime.InteropServices; // MemoryMarshal

namespace Atelia.Memory;

/// <summary>
/// 按页对齐的分块(Chunk)式，与可预留(Reservation)缓冲写入器。
/// </summary>
/// <remarks>
/// 设计与实现意图:
/// 1. 内部用若干个内存Chunk来储存数据，每个内存Chunk的大小是4K字节的整倍数。
/// 2. 用ArrayPool<byte>来管理Chunk的租用和归还。
/// 3. 记录每个Chunk使用了多少字节，因为在边界处会出现GetSpan请求数量超出当前页剩余空间的情况，此时就划分新的Chunk，保证单个Span不跨Chunk。
/// 4. 还需要管理所有已ReserveSpan但未Commit的Span，在Commit时更新内部索引，以支持快速的获知已完整填入数据(即不包含reservation的连续完整头部，这部分数据可以用于向网络写入并适当释放Chunk)。
///
/// 目标：
/// 1. 提供与IBufferWriter<byte>一致的顺序写入语义：GetSpan/GetMemory + Advance。
/// 2. 允许调用方显式 ReserveSpan(N) 预留一段将来才会被回填的数据区域；在 Commit 之前这段区域不能被归并进“已完全可消费”的前缀。
/// 3. 内部采用4K整数倍大小的Chunk池化管理，使用 ArrayPool<byte> 租用与归还，降低GC压力。
/// 4. 支持快速判定“从开头起连续已完成写入(不包含尚未Commit的reservation)的总字节长度”，以便将这些字节发送到网络 / 写入磁盘并及时释放前端Chunk。
/// 5. 避免单个返回给调用方的Span跨越两个Chunk（满足Span来自单一底层数组的要求）。
///
/// 线程安全：默认假设单线程使用（与PipeWriter类似）；不支持多线程并发写入。
/// 内存对齐：交由上层逻辑控制
/// </remarks>
public class PagedReservableWriter : IReservableBufferWriter, IDisposable {
    #region Chunked Buffer
    public const int PageSize = 4096; // 4K

    private int _minChunkPages = 1; // 默认最小1页
    private int _maxChunkPages = 256; // 默认最大256页，即1MB

    private class Chunk {
        public byte[] Buffer = null!; // 在CreateChunk中初始化
        public int DataEnd; // 已写入字节数
        public int DataBegin; // 已写入_innerWriter的字节数
        public int FreeSpace => Buffer.Length - DataEnd;
        public int PendingData => DataEnd - DataBegin; // 待写入_innerWriter的数据
        public bool IsRented; // 是否从ArrayPool租用，用于正确归还
        public bool IsFullyFlushed => DataBegin == DataEnd; // 是否完全flush，可以回收

        public Span<byte> GetAvailableSpan() => Buffer.AsSpan(DataEnd);
        public Span<byte> GetAvailableSpan(int maxLength) => Buffer.AsSpan(DataEnd, Math.Min(maxLength, FreeSpace));
    }

    // ArrayPool：可允许外部注入以便测试；默认 Shared。
    private readonly ArrayPool<byte> _pool;
    private readonly List<Chunk> _chunks = new();
    private int _chunksHeadIndex = 0; // 优化：避免List.RemoveAt(0)的O(n)开销

    // 当前写入状态
    private long _writtenLength; // 包括普通写入 + 预留区域长度
    private long _flushedLength; // 从头开始连续已完成的长度

    private Chunk CreateChunk(int sizeHint) {
        // 确保新Chunk能满足sizeHint的要求
        // 计算需要的页数，至少满足sizeHint，最少1页，最多_maxChunkPages页
        int requiredBytes = Math.Max(sizeHint, PageSize);
        int pages = Math.Max(_minChunkPages, (requiredBytes + PageSize - 1) / PageSize);
        pages = Math.Min(pages, _maxChunkPages);

        int chunkSize = pages * PageSize;
        byte[] buffer = _pool.Rent(chunkSize);

        // 验证租用的buffer确实能满足sizeHint
        if (buffer.Length < sizeHint) {
            throw new InvalidOperationException($"ArrayPool returned buffer of size {buffer.Length}, but {sizeHint} was required");
        }

        Chunk chunk = new Chunk {
            Buffer = buffer,
            DataEnd = 0,
            IsRented = true
        };

        _chunks.Add(chunk);
        return chunk;
    }

    /// <summary>
    /// 获取有效的Chunks数量（排除已回收的）
    /// </summary>
    private int GetActiveChunksCount() => _chunks.Count - _chunksHeadIndex;

    /// <summary>
    /// 获取最后一个有效的Chunk，如果没有则返回null
    /// </summary>
    private Chunk? GetLastActiveChunk() {
        return GetActiveChunksCount() > 0 ? _chunks[^1] : null;
    }

    /// <summary>
    /// 获取有效的Chunks枚举器
    /// </summary>
    private IEnumerable<Chunk> GetActiveChunks() {
        for (int i = _chunksHeadIndex; i < _chunks.Count; i++) {
            yield return _chunks[i];
        }
    }

    private Chunk EnsureSpace(int sizeHint) {
        Chunk? lastChunk = GetLastActiveChunk();
        if (lastChunk == null || lastChunk.FreeSpace < sizeHint) {
            return CreateChunk(sizeHint);
        } else {
            return lastChunk;
        }
    }
    #endregion

    private readonly IBufferWriter<byte> _innerWriter;

    public PagedReservableWriter(IBufferWriter<byte> innerWriter, ArrayPool<byte>? pool = null) {
        _innerWriter = innerWriter ?? throw new ArgumentNullException(nameof(innerWriter));
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    #region Reservation
    private class Reservation {
        public readonly Chunk Chunk;
        public readonly int Offset;
        public readonly int Length;
        public readonly long LogicalOffset; // 预留区域在整体逻辑流中的起始偏移
        public readonly string? Tag; // 调试注解，可为空

        public Reservation(Chunk chunk, int offset, int length, long logicalOffset, string? tag) {
            Chunk = chunk;
            Offset = offset;
            Length = length;
            LogicalOffset = logicalOffset;
            Tag = tag;
        }
    }

    /// <summary>
    /// 采用方案1: Dictionary<int, LinkedListNode<Reservation>> + LinkedList<Reservation>
    /// 优势：实现简单，快速验证，保序访问+快速查找+快速删除
    /// </summary>
    private readonly Dictionary<int, LinkedListNode<Reservation>> _tokenToNode = new();
    private readonly LinkedList<Reservation> _reservationOrder = new();


    private uint _reservationSerial;
    private int AllocReservationToken() {
        return (int)Bijection(++_reservationSerial);
        // return (int)++_reservationSerial; // 仅调试时使用
    }
    public static uint Bijection(uint x) {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    /// <summary>
    /// 检查并 flush 从头开始的“连续已完成数据”到 _innerWriter。
    /// 返回是否发生了任何实际 flush，用于后续决定是否尝试回收 Chunk。
    /// 逻辑：遍历有效 Chunk，直到遇到包含第一个未提交 Reservation 的 Chunk；该 Chunk 仅 flush 到该 Reservation 的起始位置。
    /// </summary>
    /// <returns>是否有数据被写入到 _innerWriter。</returns>
    private bool FlushCommittedData() {
        Reservation? firstReservation = _reservationOrder.First?.Value;
        bool flushed = false;

        foreach (Chunk chunk in GetActiveChunks()) {
            int flushableLength;

            if (firstReservation?.Chunk == chunk) {
                // 这个Chunk有最早的未Commit的Reservation，最多 flush 到 reservation 起点
                flushableLength = firstReservation.Offset - chunk.DataBegin;
                if (flushableLength > 0) {
                    FlushChunkData(chunk, flushableLength);
                    flushed = true;
                }
                break; // 后续 Chunk 被阻断
            } else {
                // 该 Chunk 没有未提交 Reservation，可全部 flush
                flushableLength = chunk.DataEnd - chunk.DataBegin;
                if (flushableLength > 0) {
                    FlushChunkData(chunk, flushableLength);
                    flushed = true;
                }
                // 继续检查下一个Chunk
            }
        }
        return flushed;
    }

    /// <summary>
    /// 将Chunk中的数据flush到_innerWriter
    /// </summary>
    /// <param name="chunk">要flush的Chunk</param>
    /// <param name="length">要flush的长度</param>
    private void FlushChunkData(Chunk chunk, int length) {
        var dataToFlush = chunk.Buffer.AsSpan(chunk.DataBegin, length);
        var innerSpan = _innerWriter.GetSpan(length);
        dataToFlush.CopyTo(innerSpan);
        _innerWriter.Advance(length);

        chunk.DataBegin += length;
        _flushedLength += length;
    }

    /// <summary>
    /// 尝试回收完全flush的Chunk
    /// 优化：使用headIndex避免List.RemoveAt(0)的O(n)开销
    /// </summary>
    private void TryRecycleFlushedChunks() {
        // 从头开始检查，回收完全flush的Chunk
        while (_chunksHeadIndex < _chunks.Count && _chunks[_chunksHeadIndex].IsFullyFlushed) {
            Chunk chunk = _chunks[_chunksHeadIndex];
            if (chunk.IsRented) {
                _pool.Return(chunk.Buffer);
            }
            _chunks[_chunksHeadIndex] = null!; // 清空引用，帮助GC
            _chunksHeadIndex++;
        }

        // 当积累了太多空位时，压缩列表
        if (_chunksHeadIndex > _chunks.Count / 2 && _chunksHeadIndex > 10) {
            CompactChunksList();
        }
    }

    /// <summary>
    /// 压缩Chunks列表，移除已回收的空位
    /// </summary>
    private void CompactChunksList() {
        if (_chunksHeadIndex == 0) return;

        // 将有效的Chunk移到列表前面
        int writeIndex = 0;
        for (int readIndex = _chunksHeadIndex; readIndex < _chunks.Count; readIndex++) {
            _chunks[writeIndex++] = _chunks[readIndex];
        }

        // 移除尾部的无效项
        _chunks.RemoveRange(writeIndex, _chunks.Count - writeIndex);
        _chunksHeadIndex = 0;
    }

    /// <summary>
    /// 若当前已无未提交 Reservation 且所有 Chunk 已被回收，则显式清空列表，恢复直通模式。
    /// （不重置长度计数器，以保持逻辑字节统计的连续性。）
    /// </summary>
    private void TryRestorePassthroughIfIdle() {
        if (_reservationOrder.Count == 0 && GetActiveChunksCount() == 0 && _chunks.Count > 0) {
            // 所有已回收（_chunksHeadIndex == _chunks.Count），清理内部列表释放引用与容量
            _chunks.Clear();
            _chunksHeadIndex = 0;
            // 不触碰 _writtenLength / _flushedLength
        }
    }
    #endregion

    #region IBufferWriter<byte>
    // 用于Advance验证的状态，避免使用Span<byte>字段
    private int _lastSpanLength; // 记录最后一次GetSpan返回的Span长度
    private bool _hasLastSpan; // 是否有有效的lastSpan状态

    public void Advance(int count) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");
        if (count == 0)
            return;

        // 验证count不超过最后一次GetSpan的可用空间
        if (!_hasLastSpan || count > _lastSpanLength)
            throw new ArgumentOutOfRangeException(nameof(count), "Count exceeds available space");

        // 如果没有Reservation，直接写入_innerWriter
        if (GetActiveChunksCount() == 0) {
            _innerWriter.Advance(count);
            _writtenLength += count;
            _flushedLength += count;
        } else {
            // 有Reservation时，更新当前Chunk的Written
            Chunk? lastChunk = GetLastActiveChunk();
            if (lastChunk != null) {
                lastChunk.DataEnd += count;
                _writtenLength += count;
                bool flushed = FlushCommittedData();
                if (flushed) {
                    // 仅在确实 flush 时尝试回收，避免无谓遍历
                    TryRecycleFlushedChunks();
                    TryRestorePassthroughIfIdle();
                }
            }
        }

        _hasLastSpan = false; // 清空状态，防止重复Advance
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        sizeHint = Math.Max(sizeHint, 1);

        // 如果没有Reservation，直接使用_innerWriter
        if (GetActiveChunksCount() == 0) {
            Memory<byte> memory = _innerWriter.GetMemory(sizeHint);
            _lastSpanLength = memory.Length;
            _hasLastSpan = true;
            return memory;
        }

        // 有Reservation时，使用内部Chunk
        Chunk chunk = EnsureSpace(sizeHint);
        Memory<byte> availableMemory = chunk.Buffer.AsMemory(chunk.DataEnd, Math.Min(sizeHint, chunk.FreeSpace));
        _lastSpanLength = availableMemory.Length;
        _hasLastSpan = true;
        return availableMemory;
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        sizeHint = Math.Max(sizeHint, 1);

        // 如果没有Reservation，直接使用_innerWriter
        if (GetActiveChunksCount() == 0) {
            Span<byte> span = _innerWriter.GetSpan(sizeHint);
            _lastSpanLength = span.Length;
            _hasLastSpan = true;
            return span;
        }

        // 有Reservation时，使用内部Chunk
        Chunk chunk = EnsureSpace(sizeHint);
        Span<byte> availableSpan = chunk.GetAvailableSpan();

        // 确保返回的Span至少满足sizeHint的要求
        if (availableSpan.Length < sizeHint) {
            throw new InvalidOperationException($"Unable to provide span of size {sizeHint}, only {availableSpan.Length} available");
        }

        _lastSpanLength = availableSpan.Length;
        _hasLastSpan = true;
        return availableSpan;
    }
    #endregion

    #region IReservableBufferWriter
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive");

        // 确保有足够空间的Chunk
        Chunk chunk = EnsureSpace(count);
        int offset = chunk.DataEnd;
        long logicalOffset = _writtenLength; // 预留区域起始逻辑偏移（当前总长度）

        // 创建Reservation
        Reservation reservation = new Reservation(chunk, offset, count, logicalOffset, tag);
        reservationToken = AllocReservationToken();

        // 添加到数据结构
        LinkedListNode<Reservation> node = _reservationOrder.AddLast(reservation);
        _tokenToNode[reservationToken] = node;

        // 更新Chunk状态
        chunk.DataEnd += count;
        _writtenLength += count;

        // 返回预留的Span
        return chunk.Buffer.AsSpan(offset, count);
    }

    /// <summary>
    /// Commit 重复调用 => 抛 InvalidOperationException 便于发现逻辑问题。
    /// </summary>
    /// <param name="reservationToken"></param>
    public void Commit(int reservationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tokenToNode.TryGetValue(reservationToken, out LinkedListNode<Reservation>? node))
            throw new InvalidOperationException("Invalid or already committed reservation token");

        // 直接删除Reservation
        _reservationOrder.Remove(node);
        _tokenToNode.Remove(reservationToken);

        // 检查并flush连续完成的数据，并在有 flush 时尝试回收
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
    /// 重置Writer状态，归还所有Chunk到ArrayPool，清空所有Reservation
    /// </summary>
    public void Reset() {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 归还所有Chunk
        foreach (Chunk chunk in _chunks) {
            if (chunk != null && chunk.IsRented) {
                _pool.Return(chunk.Buffer);
            }
        }

        // 清空所有状态
        _chunks.Clear();
        _chunksHeadIndex = 0;
        _tokenToNode.Clear();
        _reservationOrder.Clear();
        _writtenLength = 0;
        _flushedLength = 0;
        _hasLastSpan = false;
        _lastSpanLength = 0;
        _reservationSerial = 0;
    }

    #region 核心状态属性
    /// <summary>
    /// 获取已写入或预留的总字节数。这是缓冲区的逻辑总长度。
    /// 即逻辑上被占用的总字节数（普通写入 + 所有 reservation 长度）。
    /// </summary>
    public long WrittenLength => _writtenLength;

    /// <summary>
    /// 获取从头开始已成功刷写到底层写入器的总字节数。
    /// </summary>
    public long FlushedLength => _flushedLength;

    /// <summary>
    /// 获取已写入但尚未刷写的字节数 (WrittenLength - FlushedLength)。
    /// 即阻塞或等待回填的字节数。
    /// </summary>
    public long PendingLength => _writtenLength - _flushedLength;
    #endregion

    #region 诊断属性
    /// <summary>
    /// 获取当前尚未提交的预留(Reservation)数量。
    /// </summary>
    public int PendingReservationCount => _reservationOrder.Count;

    /// <summary>
    /// 获取第一个阻塞数据刷写的ReservationToken。如果所有Reservation都已提交，则返回 null。
    /// 即最早阻塞 flush 的 reservation 的 token（若无则为 null）。
    /// 这个属性对于调试非常有用，可以快速定位是哪个预留操作导致了数据积压。
    /// </summary>
    public int? BlockingReservationToken => _reservationOrder.First is { } n ? _tokenToNode.First(kv => kv.Value == n).Key : (int?)null;

    /// <summary>
    /// 获取一个值，该值指示写入器当前是否工作在直通模式下（即无任何预留和内部缓冲）。即无任何活动 Chunk（全部已回收或尚未创建）且无未提交 Reservation。
    /// 该属性仅用于诊断与可读性，不额外保证线程安全。
    /// </summary>
    public bool IsPassthrough => GetActiveChunksCount() == 0 && _reservationOrder.Count == 0;
    #endregion

    /// <summary>
    /// 释放资源，归还所有ArrayPool租用的内存
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
