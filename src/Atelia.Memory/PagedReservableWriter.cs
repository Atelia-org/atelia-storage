using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Buffers; // RuntimeHelpers.GetHashCode(obj);
using System.Runtime.InteropServices; // MemoryMarshal

namespace Atelia.Memory;

// AI TODO:实现这个类型
// 内部用若干个内存Chunk来储存数据，每个内存Chunk的大小是4K字节的整倍数。
// 用ArrayPool<byte>来管理Chunk的租用和归还。
// 记录每个Chunk使用了多少字节，因为在边界处会出现GetSpan请求数量超出当前页剩余空间的情况，此时就划分新的Chunk，保证单个Span不跨Chunk。
// 还需要管理所有已ReserveSpan但未Commit的Span，在Commit时更新内部索引，以支持快速的获知已完整填入数据(即不包含reservation的连续完整头部，这部分数据可以用于向网络写入并适当释放Chunk)。
//
/// <summary>
/// 按页对齐的分块(Chunk)式，与可预留(Reservation)缓冲写入器。
/// 目标：
/// 1. 提供与IBufferWriter<byte>一致的顺序写入语义：GetSpan/GetMemory + Advance。
/// 2. 允许调用方显式 ReserveSpan(N) 预留一段将来才会被回填的数据区域；在 Commit 之前这段区域不能被归并进“已完全可消费”的前缀。
/// 3. 内部采用4K整数倍大小的Chunk池化管理，使用 ArrayPool<byte> 租用与归还，降低GC压力。
/// 4. 支持快速判定“从开头起连续已完成写入(不包含尚未Commit的reservation)的总字节长度”，以便将这些字节发送到网络 / 写入磁盘并及时释放前端Chunk。
/// 5. 避免单个返回给调用方的Span跨越两个Chunk（满足Span来自单一底层数组的要求）。
///
/// 线程安全：默认假设单线程使用（与PipeWriter类似）；不支持多线程并发写入。
/// 内存对齐：交由上层逻辑控制
/// </summary>
public class PagedReservableWriter : IReservableBufferWriter {
    #region Chunked Buffer
    public const int PageSize = 4096; // 4K

    private int _minChunkPages = 1; // 默认最小1页
    private int _maxChunkPages = 256; // 默认最大256页，即1MB

    private class Chunk {
        public byte[] Buffer;
        public int Written; // 已写入字节数
        public int DataBegin; // 已写入_innerWriter的字节数
        public int FreeSpace => Buffer.Length - Written;
        public int PendingData => Written - DataBegin; // 待写入_innerWriter的数据
        public bool IsRented; // 是否从ArrayPool租用，用于正确归还
        public bool IsFullyFlushed => DataBegin == Written; // 是否完全flush，可以回收

        public Span<byte> GetAvailableSpan() => Buffer.AsSpan(Written);
        public Span<byte> GetAvailableSpan(int maxLength) => Buffer.AsSpan(Written, Math.Min(maxLength, FreeSpace));
    }

    // ArrayPool：可允许外部注入以便测试；默认 Shared。
    private readonly ArrayPool<byte> _pool;
    private readonly List<Chunk> _chunks = new();

    // 当前写入状态
    private long _totalLogicalLength; // 包括普通写入 + 预留区域长度
    private long _completedLogicalLength; // 从头开始连续已完成的长度

    private Chunk CreateChunk(int sizeHint) {
        // 计算需要的页数，至少1页，最多_maxChunkPages页
        int requiredBytes = Math.Max(sizeHint, PageSize);
        int pages = Math.Max(_minChunkPages, (requiredBytes + PageSize - 1) / PageSize);
        pages = Math.Min(pages, _maxChunkPages);

        int chunkSize = pages * PageSize;
        byte[] buffer = _pool.Rent(chunkSize);

        var chunk = new Chunk {
            Buffer = buffer,
            Written = 0,
            IsRented = true
        };

        _chunks.Add(chunk);
        return chunk;
    }

    private Chunk EnsureSpace(int sizeHint) {
        Chunk ret;
        if (_chunks.Count == 0 || _chunks[^1].FreeSpace < sizeHint) {
            ret = CreateChunk(sizeHint);
        } else {
            ret = _chunks[^1];
        }
        return ret;
    }
    #endregion

    private readonly IBufferWriter<byte> _innerWriter;

    public PagedReservableWriter(IBufferWriter<byte> innerWriter, ArrayPool<byte> pool = null) {
        _innerWriter = innerWriter ?? throw new ArgumentNullException(nameof(innerWriter));
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    #region Reservation
    private class Reservation {
        public readonly Chunk Chunk;
        public readonly int Offset;
        public readonly int Length;

        public Reservation(Chunk chunk, int offset, int length) {
            Chunk = chunk;
            Offset = offset;
            Length = length;
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
    /// 供Commit调用，用以实现“拼出一段连续的有完成数据的头部，就写入_innerWriter中”的逻辑。
    /// </summary>
    /// <exception cref="NotImplementedException"></exception>
    /// <summary>
    /// 检查并flush连续完成的数据到_innerWriter
    /// </summary>
    private void CheckAndFlushCompletedData() {
        var firstReservation = _reservationOrder.First?.Value;

        foreach (var chunk in _chunks) {
            int flushableLength;

            if (firstReservation?.Chunk == chunk) {
                // 这个Chunk有最早的未Commit的Reservation
                flushableLength = firstReservation.Offset - chunk.DataBegin;

                // flush这部分数据（如果有的话）
                if (flushableLength > 0) {
                    FlushChunkData(chunk, flushableLength);
                }

                // 遇到第一个有Reservation的Chunk后就停止，后面的都不能flush
                break;
            } else {
                // 这个Chunk没有未Commit的Reservation，全部可flush
                flushableLength = chunk.Written - chunk.DataBegin;

                if (flushableLength > 0) {
                    FlushChunkData(chunk, flushableLength);
                }
                // 继续检查下一个Chunk
            }
        }
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
        _completedLogicalLength += length;
    }

    /// <summary>
    /// 尝试回收完全flush的Chunk
    /// </summary>
    private void TryRecycleCompletedChunks() {
        // 从头开始检查，回收完全flush的Chunk
        while (_chunks.Count > 0 && _chunks[0].IsFullyFlushed) {
            var chunk = _chunks[0];
            if (chunk.IsRented) {
                _pool.Return(chunk.Buffer);
            }
            _chunks.RemoveAt(0);
        }
    }
    #endregion

    #region IBufferWriter<byte>
    private Span<byte> _lastSpan; // 记录最后一次GetSpan返回的Span，用于Advance验证

    public void Advance(int count) {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");
        if (count == 0)
            return;

        // 验证count不超过最后一次GetSpan的可用空间
        if (count > _lastSpan.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Count exceeds available space");

        // 如果没有Reservation，直接写入_innerWriter
        if (_chunks.Count == 0) {
            _innerWriter.Advance(count);
            _totalLogicalLength += count;
            _completedLogicalLength += count;
        } else {
            // 有Reservation时，更新当前Chunk的Written
            if (_chunks.Count > 0) {
                _chunks[^1].Written += count;
                _totalLogicalLength += count;
                CheckAndFlushCompletedData();
            }
        }

        _lastSpan = default; // 清空，防止重复Advance
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        return GetSpan(sizeHint).AsMemory();
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        sizeHint = Math.Max(sizeHint, 1);

        // 如果没有Reservation，直接使用_innerWriter
        if (_chunks.Count == 0) {
            var span = _innerWriter.GetSpan(sizeHint);
            _lastSpan = span;
            return span;
        }

        // 有Reservation时，使用内部Chunk
        var chunk = EnsureSpace(sizeHint);
        var availableSpan = chunk.GetAvailableSpan(sizeHint);
        _lastSpan = availableSpan;
        return availableSpan;
    }
    #endregion

    #region IReservableBufferWriter
    public Span<byte> ReserveSpan(int count, out int reservationToken) {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive");

        // 确保有足够空间的Chunk
        var chunk = EnsureSpace(count);
        int offset = chunk.Written;

        // 创建Reservation
        var reservation = new Reservation(chunk, offset, count);
        reservationToken = AllocReservationToken();

        // 添加到数据结构
        var node = _reservationOrder.AddLast(reservation);
        _tokenToNode[reservationToken] = node;

        // 更新Chunk状态
        chunk.Written += count;
        _totalLogicalLength += count;

        // 返回预留的Span
        return chunk.Buffer.AsSpan(offset, count);
    }

    /// <summary>
    /// Commit 重复调用 => 抛 InvalidOperationException 便于发现逻辑问题。
    /// </summary>
    /// <param name="reservationToken"></param>
    public void Commit(int reservationToken) {
        if (!_tokenToNode.TryGetValue(reservationToken, out var node))
            throw new InvalidOperationException("Invalid or already committed reservation token");

        // 直接删除Reservation
        _reservationOrder.Remove(node);
        _tokenToNode.Remove(reservationToken);

        // 检查并flush连续完成的数据
        CheckAndFlushCompletedData();

        // 尝试回收完全flush的Chunk
        TryRecycleCompletedChunks();
    }
    #endregion
}

// GTP-5的分析与建议：
///
/// 设计要点：
///   在 Commit 时标记并尝试推进“已完成前缀”边界：
///     * 维护一个 frontCompletedPointer（逻辑偏移），表示从头开始连续且全部 Commit 的最大位置。
///     * 在有 Reservation 存在时，“前缀连续性”会被这些未Commit的洞阻断。
/// - 提前释放：
///   并将这些数据对应的完整Chunk在无保留数据时归还池。
/// - ReserveSpan(count):
///   4. Reservation 包含 Advance；即 Reservation 的空间“占位”也增加 logicalLength。
///      - 需要决定逻辑：
///        方案B: Reserve 时也把其作为已“占据”的逻辑长度（更像长度固定的占位洞），但仍不算“已完成前缀”。Commit 后变为已完成。推荐方案B，便于整体线性偏移计算。
/// - Commit(reservationSpan):
///   1. 找到对应Reservation（需要能从Span反查；可在Reservation结构中保存UnderlyingArray引用与起始offset进行匹配。注意Span可能被复制比较时成本大，
///      更高效做法：ReserveSpan 返回前把 Reservation 对象入列表并同时返回Span；Commit 传回Span 时在内部通过ReferenceEquals(array) + offset匹配。）
////      可替代方案：ReserveSpan 返回一个 ReservationHandle (struct) 供 Commit 使用，更安全高效。但接口已定只能传Span，只能做匹配。
///   2. 标记Committed = true。
///   3. 尝试推进 frontCompletedPointer：
///        while 下一个尚未处理的逻辑区域已全部Advance（普通）或 Reservation 已Committed，则向前移动；
///        若跨Chunk并且最前Chunk已完全被 frontCompletedPointer 超过且不含未Commit的Reservation，则归还Chunk。
/// - 元数据结构建议：
///   class Chunk { byte[] Buffer; int Length; int Written; int ActiveReservations; }
///   struct Reservation { Chunk Chunk; int Offset; int Length; bool Committed; }
///   - 需要一个 List<Chunk> 按顺序排列。
///   - Reservation 列表可按创建顺序追加；再维护一个指针 nextReservationToPromote 用于推进 frontCompletedPointer。
/// - frontCompletedPointer 推进算法：
///   * 维护一个 long completedLogicalLength；
///   * 维护一个 long totalLogicalLength（包括普通写入 + 预留区域长度）；
///   * 每次 Commit 或 Advance 后检查 Reservation 列表/普通写入边界：
///       - 我们可维护一个“Gap Set”表示未Commit Reservation 区域的逻辑区间。
///       - 更简单：Reservation 按逻辑顺序（因为写入顺序）添加。保持一个索引 currentReservationPromoteIndex。
///         在循环中：
///            if currentReservationPromoteIndex 的 Reservation 已Committed -> 推进 completedLogicalLength 至该 Reservation 末尾并递增索引；
///            else 终止（遇到未Commit洞）。
///         同时普通写入与 Reservation 混合的逻辑边界如何合并？因为 Reservation 在创建时就分配了逻辑位置（方案B），
///         所以 completedLogicalLength 只需与 next logical boundary 比较即可。
///
/// - Dispose / Reset：
///   * 提供 Reset() 清空状态并归还所有Chunk；
///   * 提供可选的最大保留Chunk数以减少后续租用；
///   * 注意防止重复归还。
///
/// - 异常与契约：
///   * Advance(负数或超过可用Span) => ArgumentOutOfRangeException。
///   * GetSpan 在 sizeHint > chunkSize 时：创建多倍PageSize的Chunk，向上取整。
///   * ReserveSpan/Commit 传入无效Span => ArgumentException。
