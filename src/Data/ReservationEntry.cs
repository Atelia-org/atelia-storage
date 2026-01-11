namespace Atelia.Data;

/// <summary>
/// 表示一个待提交的预留区域
/// </summary>
internal sealed class ReservationEntry {
    /// <summary>预留区域所在的 Chunk</summary>
    public readonly ReservableWriterChunk Chunk;

    /// <summary>预留区域在 Chunk 中的起始偏移</summary>
    public readonly int Offset;

    /// <summary>预留区域的字节长度</summary>
    public readonly int Length;

    /// <summary>预留区域在逻辑流中的起始偏移</summary>
    public readonly long LogicalOffset;

    /// <summary>可选的调试标签</summary>
    public readonly string? Tag;

    public ReservationEntry(
        ReservableWriterChunk chunk,
        int offset,
        int length,
        long logicalOffset,
        string? tag
    ) {
        Chunk = chunk;
        Offset = offset;
        Length = length;
        LogicalOffset = logicalOffset;
        Tag = tag;
    }
}
