using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Atelia.Data;

/// <summary>
/// Reservation 管理器：追踪待提交的预留区域，支持 O(1) 查找和有序遍历
/// </summary>
/// <remarks>
/// 设计说明：
/// - 使用 Dictionary + LinkedList 实现 O(1) token 查找 + 有序遍历
/// - 不重置 _reservationSerial 以避免 Reset 后 token 复用风险
/// - 与具体的 Writer 实现解耦，可同时用于 ChunkedReservableWriter 和 SinkReservableWriter
/// </remarks>
internal sealed class ReservationTracker {
    private Dictionary<int, LinkedListNode<ReservationEntry>>? _tokenToNode;
    private LinkedList<ReservationEntry>? _reservationOrder;
    private uint _reservationSerial;

    /// <summary>待提交预留数量</summary>
    public int PendingCount => _reservationOrder?.Count ?? 0;

    /// <summary>最早的待提交预留（用于确定可刷新边界）</summary>
    public ReservationEntry? FirstPending => _reservationOrder?.First?.Value;

    /// <summary>
    /// 尝试获取最早的待提交预留
    /// </summary>
    public bool TryGetFirstPending([NotNullWhen(true)] out ReservationEntry? entry) {
        entry = _reservationOrder?.First?.Value;
        return entry is not null;
    }

    /// <summary>
    /// 添加新的预留区域
    /// </summary>
    public int Add(
        ReservableWriterChunk chunk,
        int offset,
        int length,
        long logicalOffset,
        string? tag
    ) {
        EnsureInitialized();

        var entry = new ReservationEntry(chunk, offset, length, logicalOffset, tag);
        int token = ReservationTokenHelper.AllocToken(ref _reservationSerial);

        var node = _reservationOrder!.AddLast(entry);
        _tokenToNode![token] = node;

        return token;
    }

    /// <summary>
    /// 尝试提交指定 token 的预留
    /// </summary>
    /// <returns>true 如果 token 有效且成功提交</returns>
    public bool TryCommit(int token, [NotNullWhen(true)] out ReservationEntry? entry) {
        if (_tokenToNode is null || !_tokenToNode.TryGetValue(token, out var node)) {
            entry = null;
            return false;
        }

        entry = node.Value;
        _reservationOrder!.Remove(node);
        _tokenToNode.Remove(token);
        return true;
    }

    /// <summary>
    /// 清空所有待提交预留（不重置 serial）
    /// </summary>
    public void Clear() {
        _tokenToNode?.Clear();
        _reservationOrder?.Clear();
        // 不重置 _reservationSerial，避免 token 复用风险
    }

    private void EnsureInitialized() {
        _tokenToNode ??= new();
        _reservationOrder ??= new();
    }
}
