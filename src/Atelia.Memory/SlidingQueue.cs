using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Atelia.Memory;

/// <summary>
/// SlidingQueue<T> 是一个“基于 List + 头索引”的单向滑动窗口容器：
///  - 只支持尾部 Append (Add)
///  - 只支持从头部按顺序批量回收 (RecycleWhile)
///  - 通过 _head 索引惰性丢弃前缀，必要时 Compact 将活动元素搬移到前部
/// 设计背景：源自早期在 <see cref="ChunkedReservableWriter"/> 中的专用实现（纪念：刘德智 SlidingBuffer 思路），
/// 抽取为独立类型以复用并降低主类复杂度。
///
/// 使用场景：生产/消费模型中，消费顺序严格且永不回头；偶尔需要遍历活动区；需要 O(1) 摊销 Add/Recycle。
/// 非线程安全。
/// </summary>
/// <remarks>
/// 可作为一个“可压缩队头批量消费队列”使用；未来若需要替换为 <c>Queue<T></c> 或 deque，只需保持公开成员契约。
/// 重要不变量：0 <= _head <= _items.Count；活动区为 [_head, _items.Count)。
/// </remarks>
/// <remarks>
/// 2025 增强：
///  - 实现 IReadOnlyList<T> 提供索引访问
///  - struct 枚举器 + fail-fast 版本号，减少 foreach 分配并在修改时抛错
///  - 缓存 NeedsClear 避免重复调用 RuntimeHelpers
///  - 新增 PeekFirst/PeekLast 非 Try 版本
///  - 添加 ToArray / CopyTo / Indexer / Any
///  - Dequeue / TryDequeue 支持可选自动 Compact 检查参数（默认启用）
///  - 固定压缩策略：_head >= 64 且 _head >= Count(物理) / 2 时触发一次 O(存活数) 搬移；摊销 O(1)
/// </remarks>
[
    DebuggerDisplay("Count = {Count}, Head = {_head}")
]
public sealed class SlidingQueue<T> : IReadOnlyList<T> {
    private readonly List<T> _items = new();
    private int _head; // 指向第一个“活动”元素（逻辑队头）
    private int _version; // 枚举 fail-fast 版本号

    // 固定压缩策略（Plan A）：当已消费>=64 且 已消费部分>=总长度一半 时触发压缩
    private const int CompactHeadMin = 64;

    private static readonly bool NeedsClear = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    public SlidingQueue() { }

    /// <summary>当前活动元素数量。</summary>
    public int Count => _items.Count - _head;

    /// <summary>索引访问（0 对应逻辑队头）。</summary>
    public T this[int index] {
        get {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[_head + index]!;
        }
    }

    /// <summary>是否无活动元素。</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>追加一个元素到尾部。禁止引用类型 null 进入以保持非空语义。</summary>
    public void Enqueue(T item) {
        if (!typeof(T).IsValueType && item is null) throw new ArgumentNullException(nameof(item));
        _items.Add(item);
        _version++;
        Debug.Assert(_head <= _items.Count);
    }

    /// <summary>尝试窥视队头元素；若为空返回 false。</summary>
    public bool TryPeekFirst([MaybeNullWhen(false)] out T value) {
        if (IsEmpty) { value = default!; return false; }
        value = _items[_head];
        return true;
    }
    /// <summary>尝试窥视队尾元素；若为空返回 false。</summary>
    public bool TryPeekLast([MaybeNullWhen(false)] out T value) {
        if (IsEmpty) { value = default!; return false; }
        value = _items[^1];
        return true;
    }

    /// <summary>出队一个元素，失败抛 <see cref="InvalidOperationException"/>。</summary>
    public T Dequeue(bool autoCompactCheck = true) {
        if (IsEmpty) throw new InvalidOperationException("Queue is empty");
        var v = _items[_head];
        if (NeedsClear) _items[_head] = default!; // 协助 GC
        _head++;
        _version++;
        Debug.Assert(_head <= _items.Count);
        if (autoCompactCheck) AutoCompactMaybe();
        return v!;
    }

    /// <summary>尝试出队一个元素。</summary>
    public bool TryDequeue([MaybeNullWhen(false)] out T value, bool autoCompactCheck = true) {
        if (IsEmpty) { value = default!; return false; }
        value = _items[_head];
        if (NeedsClear) _items[_head] = default!;
        _head++;
        _version++;
        Debug.Assert(_head <= _items.Count);
        if (autoCompactCheck) AutoCompactMaybe();
        return true;
    }

    /// <summary>
    /// 按谓词连续出队（头到尾），返回实际出队个数。与 BCL 不同：这是一个附加便捷方法，用于批量消费。
    /// </summary>
    public int DequeueWhile(System.Func<T, bool> predicate, bool autoCompactCheck = true) {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        int oldHead = _head;
        int count = 0;
        while (_head < _items.Count) {
            var cur = _items[_head];
            if (!predicate(cur)) break;
            if (NeedsClear) _items[_head] = default!;
            _head++;
            count++;
        }
        if (_head != oldHead) { _version++; Debug.Assert(_head <= _items.Count); if (autoCompactCheck) AutoCompactMaybe(); }
        return count;
    }

    /// <summary>
    /// 压缩前缀空洞（可选调用）。满足阈值时把活动元素搬移到前部以释放已消费段的引用。
    /// </summary>
    public void Compact() {
        if (_head < CompactHeadMin) return;
        if (_head * 2 < _items.Count) return; // 未达到一半条件
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
        if (_items.Count == 0 && _head == 0) return;
        _items.Clear();
        _head = 0;
        _version++;
        Debug.Assert(_head <= _items.Count);
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
        public T Current { get; private set; }
        object IEnumerator.Current => Current!;
        public bool MoveNext() {
            if (_version != _queue._version) throw new InvalidOperationException("Collection was modified during enumeration");
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
            if (_version != _queue._version) throw new InvalidOperationException("Collection was modified during enumeration");
            _index = -1;
            Current = default!;
        }
        public void Dispose() { }
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
        if (IsEmpty) return Array.Empty<T>();
        var result = new T[Count];
        _items.CopyTo(_head, result, 0, Count);
        return result;
    }

    /// <summary>复制到外部数组。</summary>
    public void CopyTo(T[] array, int arrayIndex) {
        if (array == null) throw new ArgumentNullException(nameof(array));
        if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (array.Length - arrayIndex < Count) throw new ArgumentException("Destination array is too small.");
        for (int i = 0, src = _head; i < Count; i++, src++) array[arrayIndex + i] = _items[src]!;
    }

    /// <summary>是否包含任意元素。</summary>
    public bool Any() => !IsEmpty;

    private void AutoCompactMaybe() {
        // 直接调用 Compact，内部含条件；保持调用点简洁
        Compact();
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal int DebugHeadIndex => _head; // 调试辅助
}
