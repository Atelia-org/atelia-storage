using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Atelia.Memory;

/// <summary>
/// SlidingQueue<T> 是一个“基于 List + 头索引”的单向滑动窗口容器：
///  - 只支持尾部 Append (Add)
///  - 只支持从头部按顺序批量回收 (DequeueWhile)
///  - 通过 _head 索引惰性丢弃前缀，必要时 Compact 将活动元素搬移到前部
/// 设计背景：源自早期在 <see cref="ChunkedReservableWriter"/> 中的专用实现（纪念：刘德智 SlidingBuffer 思路），
/// 抽取为独立类型以复用并降低主类复杂度。
///
/// 使用场景：生产/消费模型中，消费顺序严格且永不回头；偶尔需要遍历活动区；需要 O(1) 摊销 Add/Recycle。
/// 非线程安全。
/// </summary>
/// <remarks>
/// 可作为一个“可压缩队头批量消费队列”使用；未来若需要替换为 <c>Queue<T></c> 或 deque，只需保持公开成员契约。
/// 重要不变量：0 <= _head <= _items.Count；活动区为 [_head, _items.Count)。///
/// 2025 增强：
///  - 实现 IReadOnlyList<T> 提供索引访问
///  - struct 枚舉器 + fail-fast 版本号，减少 foreach 分配并在修改时抛错
///  - 缓存 NeedsClear 避免重复调用 RuntimeHelpers
///  - 新增 PeekFirst/PeekLast 非 Try 版本
///  - 添加 ToArray / CopyTo / Indexer / Any
///  - Dequeue / TryDequeue 支持可选自动 Compact 检查参数（默认启用）
///  - 固定压缩策略：_head >= 64 且 _head >= Count(物理) / 2 时触发一次 O(存活数) 搬移；摊销 O(1)
/// </remarks>
[DebuggerDisplay("Count={Count}, Head={_head}")]
public sealed class SlidingQueue<T> : IReadOnlyList<T> where T : notnull {
    private readonly List<T> _items; // 延迟在构造函数中根据初始容量初始化
    private int _head; // 指向第一个“活动”元素（逻辑队头）
    private int _version; // 枚举 fail-fast 版本号

    // 固定压缩策略（Plan A）：当已消费>=64 且 已消费部分>=总长度一半 时触发压缩
    private const int CompactHeadMin = 64;

    private static readonly bool NeedsClear = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    /// <summary>使用默认容量初始化一个空的滑动队列。</summary>
    public SlidingQueue() {
        _items = new List<T>();
    }
    /// <summary>使用指定初始容量初始化队列（不会预填充元素）。</summary>
    /// <param name="capacity">初始容量，必须 >= 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">capacity 小于 0。</exception>
    public SlidingQueue(int capacity) {
        if (capacity < 0) {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _items = capacity == 0 ? new List<T>() : new List<T>(capacity);
    }

    /// <summary>当前活动元素数量。</summary>
    public int Count => _items.Count - _head;
    /// <summary>当前底层容量（物理数组长度）。</summary>
    public int Capacity => _items.Capacity;
    /// <summary>当前是否达到压缩阈值（若调用 <see cref="Compact"/> 将会执行实际搬移）。</summary>
    public bool ShouldCompact => _head >= CompactHeadMin && _head * 2 >= _items.Count;

    // public int ConsumedCount => _head; 会引入“历史总消费数 vs 当前skip ahead”的误解，所以注释掉了

    /// <summary>索引访问（0 对应逻辑队头）。</summary>
    public T this[int index] {
        get {
            if ((uint)index >= (uint)Count) {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return _items[_head + index]!;
        }
    }

    /// <summary>是否无活动元素。</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>追加一个元素到尾部（泛型约束 notnull 确保不接受 null）。</summary>
    public void Enqueue(T item) {
        // JIT Compaction: 若底层 List 已满且存在已消费前缀，则先搬移存活元素到下标 0 复用空间，避免真正扩容。
        if (_head > 0 && _items.Count == _items.Capacity) {
            Compact(force: true); // force: 无视阈值，直接压缩；O(存活数)，劣于扩容的 O(总数) 只有活动区更小才触发
        }
        _items.Add(item);
        _version++;
        Debug.Assert(_head <= _items.Count);
    }

    /// <summary>尝试窥视队头元素；若为空返回 false。</summary>
    public bool TryPeekFirst([MaybeNullWhen(false)] out T value) {
        if (IsEmpty) {
            value = default!;
            return false;
        }
        value = _items[_head];
        return true;
    }
    /// <summary>尝试窥视队尾元素；若为空返回 false。</summary>
    public bool TryPeekLast([MaybeNullWhen(false)] out T value) {
        if (IsEmpty) {
            value = default!;
            return false;
        }
        value = _items[^1];
        return true;
    }

    /// <summary>出队一个元素，失败抛 <see cref="InvalidOperationException"/>。</summary>
    public T Dequeue(bool autoCompactCheck = true) {
        if (IsEmpty) {
            throw new InvalidOperationException("Queue is empty");
        }
        var v = _items[_head];
        if (NeedsClear) {
            _items[_head] = default!; // 协助 GC
        }
        _head++;
        _version++;
        Debug.Assert(_head <= _items.Count);
        if (autoCompactCheck) {
            Compact(); // Compact 内部自行判断阈值，无副作用
        }
        return v!;
    }

    /// <summary>尝试出队一个元素。</summary>
    public bool TryDequeue([MaybeNullWhen(false)] out T value, bool autoCompactCheck = true) {
        if (IsEmpty) {
            value = default!;
            return false;
        }
        value = _items[_head];
        if (NeedsClear) {
            _items[_head] = default!;
        }
        _head++;
        _version++;
        Debug.Assert(_head <= _items.Count);
        if (autoCompactCheck) {
            Compact();
        }
        return true;
    }
    /// <summary>尝试出队（默认执行自动压缩检查）。</summary>
    public bool TryDequeue([MaybeNullWhen(false)] out T value) => TryDequeue(out value, true);

    /// <summary>
    /// 按谓词连续出队（头到尾），返回实际出队个数。与 BCL 不同：这是一个附加便捷方法，用于批量消费。
    /// </summary>
    public int DequeueWhile(System.Func<T, bool> predicate, bool autoCompactCheck = true) {
        if (predicate == null) {
            throw new ArgumentNullException(nameof(predicate));
        }
        return DequeueWhile(new FuncValuePredicate<T>(predicate), autoCompactCheck);
    }
    /// <summary>
    /// 高性能值类型谓词版本；避免委托调用开销。TPredicate 应为小 struct，实现 <see cref="IValuePredicate{T}"/>。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DequeueWhile<TPredicate>(TPredicate predicate, bool autoCompactCheck = true) where TPredicate : struct, IValuePredicate<T> {
        // 直接内联循环，避免任何委托适配层 —— 真正零委托/零间接调用路径。
        int oldHead = _head;
        int count = 0;
        while (_head < _items.Count) {
            var cur = _items[_head];
            if (!predicate.Invoke(cur)) {
                break;
            }
            if (NeedsClear) {
                _items[_head] = default!;
            }
            _head++;
            count++;
        }
        if (_head != oldHead) {
            _version++;
            Debug.Assert(_head <= _items.Count);
            if (autoCompactCheck) {
                Compact();
            }
        }
        return count;
    }

    /// <summary>
    /// 压缩前缀空洞（可选调用）。满足阈值时把活动元素搬移到前部以释放已 consumed 段的引用。
    /// </summary>
    /// <param name="force">
    /// 强制压缩（无视阈值条件）。适用于需要立即释放引用的内存压力场景。
    /// </param>
    public void Compact(bool force = false) {
        if (force) {
            if (_head == 0) {
                return; // 无空洞
            }
        } else if (!ShouldCompact) { // 复用 ShouldCompact 条件
            return;
        }
        int write = 0;
        for (int read = _head; read < _items.Count; read++) {
            _items[write++] = _items[read];
        }
        _items.RemoveRange(write, _items.Count - write);
        _head = 0;
        _version++;
        Debug.Assert(_head <= _items.Count);
    }

    /// <summary>清空所有元素。</summary>
    public void Clear() {
        if (_items.Count == 0 && _head == 0) {
            return;
        }

        _items.Clear();
        _head = 0;
        _version++;
        Debug.Assert(_head <= _items.Count);
    }

    /// <summary>
    /// 清空所有元素，并可选择在需要时收缩底层列表容量（防止长期占用大数组）。
    /// </summary>
    public void Clear(bool trimExcess) {
        Clear();
        if (trimExcess) {
            _items.TrimExcess();
        }
    }
    /// <summary>枚举器（struct，fail-fast）。</summary>
    public struct Enumerator : IEnumerator<T> {
        private readonly SlidingQueue<T> _queue;
        private readonly int _version;
        private int _index; // -1 初始, == Count 结束
        internal Enumerator(SlidingQueue<T> queue) {
            _queue = queue;
            _version = queue._version;
            _index = -1;
            Current = default!;
        }
        public T Current {
            get; private set;
        }
        object IEnumerator.Current => Current!;
        public bool MoveNext() {
            if (_version != _queue._version) {
                throw new InvalidOperationException("Collection was modified during enumeration");
            }
            int next = _index + 1;
            if (next < _queue.Count) {
                Current = _queue._items[_queue._head + next]!;
                _index = next;
                return true;
            }
            _index = _queue.Count; // 结束
            return false;
        }
        public void Reset() {
            if (_version != _queue._version) {
                throw new InvalidOperationException("Collection was modified during enumeration");
            }
            _index = -1;
            Current = default!;
        }
        public void Dispose() {
        }
    }

    /// <summary>获取枚举器（无分配）。</summary>
    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    /// <summary>直接窥视首元素；为空抛异常。</summary>
    public T PeekFirst() => TryPeekFirst(out var v) ? v! : throw new InvalidOperationException("Queue is empty");
    /// <summary>直接窥视尾元素；为空抛异常。</summary>
    public T PeekLast() => TryPeekLast(out var v) ? v! : throw new InvalidOperationException("Queue is empty");

    /// <summary>复制活动区到新数组。</summary>
    public T[] ToArray() {
        if (IsEmpty) {
            return Array.Empty<T>();
        }
        var result = new T[Count];
        _items.CopyTo(_head, result, 0, Count);
        return result;
    }

    /// <summary>复制到外部数组。</summary>
    public void CopyTo(T[] array, int arrayIndex) {
        if (array == null) {
            throw new ArgumentNullException(nameof(array));
        }
        if (arrayIndex < 0) {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        }
        if (array.Length - arrayIndex < Count) {
            throw new ArgumentException("Destination array is too small.");
        }
        if (Count == 0) {
            return;
        }
        _items.CopyTo(_head, array, arrayIndex, Count);
    }

    /// <summary>是否包含任意元素。</summary>
    public bool Any() => !IsEmpty;

    /// <summary>
    /// 按顺序批量入队；一次性提升版本号以保持枚举 fail-fast 语义。
    /// </summary>
    public void EnqueueRange(IEnumerable<T> source) {
        if (source == null) {
            throw new ArgumentNullException(nameof(source));
        }
        // 自引用防护：若传入当前实例（或其内部底层列表枚举），需要先快照。否则 List 在枚举期间修改会抛异常。
        if (ReferenceEquals(source, this) || ReferenceEquals(source, _items)) {
            var snapshot = ToArray(); // snapshot 只含活动区元素
            if (snapshot.Length == 0) {
                return;
            }
            EnqueueRange(snapshot); // 递归调用走 ICollection<T> 分支（数组实现 ICollection）
            return;
        }
        // 优先处理具备 Count 的集合：一次性预估容量，减少多次扩容/多次 JIT 搬移。
        if (source is ICollection<T> coll) {
            int incoming = coll.Count;
            if (incoming == 0) {
                return; // 无新增
            }
            // 若需要的总元素数会超过当前容量，先尝试 JIT 搬移以复用已消费前缀。
            bool compacted = false;
            if (_head > 0 && _items.Count + incoming > _items.Capacity) {
                Compact(force: true); // 可能已 bump 版本
                compacted = true;
            }
            int needed = _items.Count + incoming;
            if (needed > _items.Capacity) {
                // 直接设置足够容量，避免 List 的指数扩容行为导致多余内存峰值。
                _items.Capacity = needed;
            }
            // 利用 AddRange 降低循环边界检查成本（coll 可能是 List / 数组）
            if (coll is List<T> list) {
                _items.AddRange(list); // 内部会优化 List -> List 复制
            } else if (coll is T[] arr) {
                _items.AddRange(arr);
            } else {
                foreach (var item in coll) {
                    _items.Add(item);
                }
            }
            if (!compacted) {
                _version++; // 若已压缩则版本已递增，这里避免二次 bump
            }
            Debug.Assert(_head <= _items.Count);
            return;
        }

        int added = 0;
        bool compactedInLoop = false;
        // 对未知大小的可枚举：逐项添加；在需要真正增长前尝试 JIT 搬移。
        foreach (var item in source) {
            if (_head > 0 && _items.Count == _items.Capacity) {
                Compact(force: true); // 版本 +1
                compactedInLoop = true;
            }
            _items.Add(item);
            added++;
        }
        if (added > 0 && !compactedInLoop) {
            _version++;
            Debug.Assert(_head <= _items.Count);
        }
    }

    /// <summary>
    /// 尝试按逻辑索引窥视（0=队头）；失败返回 false，不抛异常。
    /// </summary>
    public bool TryPeek(int index, [MaybeNullWhen(false)] out T value) {
        if ((uint)index >= (uint)Count) {
            value = default!;
            return false;
        }
        value = _items[_head + index];
        return true;
    }

    /// <summary>
    /// 确保底层容量至少为指定值（若已足够则不扩容），返回最终容量。
    /// 注意：为复用前缀空洞，本方法可能触发一次 <see cref="Compact(bool)"/>（force 模式），
    /// 这会导致结构移动并使现有枚举器失效（版本号递增），即便逻辑元素集未发生增删。
    /// 调用者若依赖“仅容量调整不失效枚举”语义，请在调用前完成枚举或避免使用本方法。
    /// </summary>
    public int EnsureCapacity(int capacity) {
        if (capacity < 0) {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        int currentCap = _items.Capacity;
        if (capacity <= currentCap) {
            // 需求容量不超过现有容量：若前缀有空洞且为了后续追加需要更多连续尾部空间（腾挪后可获得），进行一次强制压缩。
            // 逻辑需要的"未来总元素上限"= capacity；当前存活数 = Count。
            // 当前尾部可直接追加空间 = currentCap - _items.Count。
            int freeTail = currentCap - _items.Count; // 物理数组尾部剩余插入位数
            int logicalNeededFree = capacity - Count; // 达到给定 capacity 还需要多少新增元素
            if (logicalNeededFree > freeTail && _head > 0) {
                // 通过一次搬移把存活区移到 0，尾部可用空间变成 currentCap - Count
                Compact(force: true); // 会 bump version（视为结构移动）
            }
            return _items.Capacity; // 可能未变或仍然相同
        }

        // 需求容量大于当前容量：可先（可选）压缩以降低内存峰值，再一次性设置目标容量，避免指数扩容产生中间大数组。
        if (_head > 0) {
            Compact(force: true); // 先回收前缀再扩容，避免活动区被复制两次
        }
        if (capacity > _items.Capacity) {
            _items.Capacity = capacity; // 直接设定足够容量
        }
        return _items.Capacity;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal int DebugHeadIndex => _head; // 调试辅助

    /// <summary>
    /// 收缩底层存储容量至当前存活元素所需的最小大小（类似 List.TrimExcess）。
    /// 为保证收缩前后逻辑索引连续性，会先强制压缩前缀空洞（若存在）。
    /// </summary>
    public void TrimExcess() {
        if (_head > 0) {
            // Compact(force:true) 会 bump version。仅在确实有空洞时进行。
            Compact(force: true);
        }
        // List.TrimExcess 只影响容量，不改变元素顺序与计数；不再 bump 版本号。
        _items.TrimExcess();
    }
}
