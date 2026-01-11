using System;

namespace Atelia.Data;

/// <summary>
/// IReservableBufferWriter 实现使用的内部 chunk，
/// 表示从 ArrayPool 租借的一段缓冲区及其读写游标。
/// </summary>
/// <remarks>
/// 这是 ChunkedReservableWriter 和 SinkReservableWriter 的共享内部类型。
/// 外部代码不应直接使用此类型。
/// </remarks>
internal sealed class ReservableWriterChunk {
    /// <summary>从 ArrayPool 租借的底层缓冲区</summary>
    public byte[] Buffer = null!;

    /// <summary>已写入数据的结束位置（下一个可写位置）</summary>
    public int DataEnd;

    /// <summary>已刷新数据的起始位置（下一个待刷新位置）</summary>
    public int DataBegin;

    /// <summary>缓冲区是否从 ArrayPool 租借（需要归还）</summary>
    public bool IsRented;

    /// <summary>缓冲区剩余可写空间</summary>
    public int FreeSpace => Buffer.Length - DataEnd;

    /// <summary>缓冲区中待刷新的数据量</summary>
    public int PendingData => DataEnd - DataBegin;

    /// <summary>缓冲区是否已完全刷新（可回收）</summary>
    public bool IsFullyFlushed => DataBegin == DataEnd;

    /// <summary>获取可写入区域的 Span 视图</summary>
    public Span<byte> GetAvailableSpan() => Buffer.AsSpan(DataEnd);

    /// <summary>获取指定最大长度的可写入区域 Span 视图</summary>
    public Span<byte> GetAvailableSpan(int maxLength)
        => Buffer.AsSpan(DataEnd, Math.Min(maxLength, FreeSpace));

    /// <summary>获取可写入区域的 Memory 视图</summary>
    public Memory<byte> GetAvailableMemory() => Buffer.AsMemory(DataEnd, FreeSpace);
}
