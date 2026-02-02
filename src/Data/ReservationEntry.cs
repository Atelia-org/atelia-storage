namespace Atelia.Data;

/// <summary>表示一个待提交的预留区域</summary>
internal sealed class ReservationEntry {
    /// <summary>预留区域所在的 Chunk</summary>
    public ReservableWriterChunk Chunk = null!;

    /// <summary>预留区域在 Chunk 中的起始偏移</summary>
    public int Offset;

    /// <summary>预留区域的字节长度</summary>
    public int Length;

    /// <summary>预留区域在逻辑流中的起始偏移</summary>
    public long LogicalOffset;

    /// <summary>可选的调试标签</summary>
    public string? Tag;

    /// <summary>侵入式双向链表：前驱节点</summary>
    public ReservationEntry? Prev;

    /// <summary>侵入式双向链表：后继节点</summary>
    public ReservationEntry? Next;

    /// <summary>池化用无参构造函数</summary>
    internal ReservationEntry() { }

    /// <summary>重置所有字段（为 Phase 2 池化准备）</summary>
    internal void Reset() {
        Chunk = null!;
        Offset = 0;
        Length = 0;
        LogicalOffset = 0;
        Tag = null;
        Prev = null;
        Next = null;
    }
}
