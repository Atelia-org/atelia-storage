using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Atelia.Data;

/// <summary>Reservation 管理器：追踪待提交的预留区域，支持 O(1) 查找和有序遍历</summary>
/// <remarks>
/// 设计说明：
/// - 使用 Dictionary + 侵入式双向链表实现 O(1) token 查找 + 有序遍历
/// - 侵入式链表避免 LinkedListNode 的额外分配
/// - 不重置 _reservationSerial 以避免 Reset 后 token 复用风险
/// - 与具体的 Writer 实现解耦，可同时用于 ChunkedReservableWriter 和 SinkReservableWriter
/// </remarks>
internal sealed class ReservationTracker {
    private Dictionary<int, ReservationEntry>? _tokenToEntry;
    private ReservationEntry? _head;
    private ReservationEntry? _tail;
    private int _count;
    private uint _reservationSerial;

    // 对象池（单链表，复用 Next 指针）
    private ReservationEntry? _poolHead;
    private int _poolCount;

    /// <summary>待提交预留数量</summary>
    public int PendingCount => _count;

    /// <summary>最早的待提交预留（用于确定可刷新边界）</summary>
    public ReservationEntry? FirstPending => _head;

    /// <summary>尝试获取最早的待提交预留</summary>
    public bool TryGetFirstPending([NotNullWhen(true)] out ReservationEntry? entry) {
        entry = _head;
        return entry is not null;
    }

    /// <summary>添加新的预留区域</summary>
    public int Add(
        ReservableWriterChunk chunk,
        int offset,
        int length,
        long logicalOffset,
        string? tag
    ) {
        EnsureInitialized();

        var entry = Rent(chunk, offset, length, logicalOffset, tag);
        int token = ReservationTokenHelper.AllocToken(ref _reservationSerial);

        LinkToTail(entry);
        _tokenToEntry![token] = entry;

        return token;
    }

    /// <summary>尝试提交指定 token 的预留</summary>
    /// <returns>true 如果 token 有效且成功提交</returns>
    public bool TryCommit(int token) {
        if (_tokenToEntry is null || !_tokenToEntry.TryGetValue(token, out var entry)) { return false; }

        Unlink(entry);
        _tokenToEntry.Remove(token);
        Return(entry);
        return true;
    }

    /// <summary>尝试获取指定 token 的预留（不移除）</summary>
    /// <returns>true 如果 token 有效</returns>
    public bool TryPeek(int token, [NotNullWhen(true)] out ReservationEntry? entry) {
        if (_tokenToEntry is null || !_tokenToEntry.TryGetValue(token, out entry)) {
            entry = null;
            return false;
        }

        return true;
    }

    /// <summary>清空所有待提交预留（不重置 serial）</summary>
    public void Clear() {
        // 先清空字典，避免池对象被字典引用的窗口
        _tokenToEntry?.Clear();

        // 遍历链表，批量归还到对象池
        var current = _head;
        while (current is not null) {
            var next = current.Next;
            current.Prev = null;
            current.Next = null;
            Return(current);
            current = next;
        }

        _head = null;
        _tail = null;
        _count = 0;
        // 不重置 _reservationSerial，避免 token 复用风险
    }

    /// <summary>将 entry 添加到链表尾部</summary>
    private void LinkToTail(ReservationEntry entry) {
        Debug.Assert(entry.Prev is null && entry.Next is null, "Entry already linked");

        entry.Prev = _tail;
        entry.Next = null;

        if (_tail is not null) {
            _tail.Next = entry;
        }
        else {
            _head = entry;
        }

        _tail = entry;
        _count++;
    }

    /// <summary>将 entry 从链表中移除</summary>
    private void Unlink(ReservationEntry entry) {
        if (entry.Prev is not null) {
            entry.Prev.Next = entry.Next;
        }
        else {
            _head = entry.Next;
        }

        if (entry.Next is not null) {
            entry.Next.Prev = entry.Prev;
        }
        else {
            _tail = entry.Prev;
        }

        entry.Prev = null;
        entry.Next = null;
        _count--;
    }

    private void EnsureInitialized() {
        _tokenToEntry ??= new();
    }

    /// <summary>从池获取或创建新 Entry</summary>
    private ReservationEntry Rent(
        ReservableWriterChunk chunk,
        int offset,
        int length,
        long logicalOffset,
        string? tag
    ) {
        if (_poolHead is not null) {
            Debug.Assert(_poolCount > 0, "Pool count underflow");
            var entry = _poolHead;
            _poolHead = entry.Next;
            _poolCount--;

            entry.Prev = null;  // 防御性：确保干净状态
            entry.Next = null;  // 清理池链接
            entry.Chunk = chunk;
            entry.Offset = offset;
            entry.Length = length;
            entry.LogicalOffset = logicalOffset;
            entry.Tag = tag;
            return entry;
        }

        var created = new ReservationEntry {
            Prev = null,
            Next = null,
            Chunk = chunk,
            Offset = offset,
            Length = length,
            LogicalOffset = logicalOffset,
            Tag = tag,
        };
        return created;
    }

    /// <summary>归还 Entry 到池</summary>
    private void Return(ReservationEntry entry) {
        Debug.Assert(entry.Prev is null && entry.Next is null, "Entry still linked");

        entry.Reset();  // 清理所有字段，避免保活引用
        entry.Next = _poolHead;
        _poolHead = entry;
        _poolCount++;
    }
}
