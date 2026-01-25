using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>RBF 文件对象门面。</summary>
/// <remarks>
/// <para>职责：资源管理（Dispose）、状态维护（TailOffset）、调用转发。</para>
/// <para><b>并发约束</b>：同一实例在任一时刻最多 1 个 open Builder。</para>
/// </remarks>
public interface IRbfFile : IDisposable {
    /// <summary>获取当前文件逻辑长度（也是下一个写入 Offset）。</summary>
    long TailOffset { get; }

    /// <summary>追加完整帧（payload 已就绪）。</summary>
    SizedPtr Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default);

    /// <summary>复杂帧构建（流式写入 payload / payload 内回填）。</summary>
    /// <remarks>
    /// <para>注意：在 Builder Dispose/EndAppend 前，TailOffset 不会更新。</para>
    /// <para>注意：存在 open Builder 时，不应允许并发 Append/BeginAppend。</para>
    /// </remarks>
    RbfFrameBuilder BeginAppend();

    /// <summary>随机读（从 ArrayPool 借缓存）。</summary>
    /// <remarks>
    /// <para>调用方 MUST 调用返回值的 Dispose() 归还 buffer。</para>
    /// <para>失败时 buffer 已自动归还。</para>
    /// </remarks>
    AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr);

    /// <summary>读取指定位置的帧到提供的 buffer 中。</summary>
    /// <param name="ptr">帧位置凭据。</param>
    /// <param name="buffer">目标缓冲区，长度必须 &gt;= ptr.Length。</param>
    /// <returns>成功时返回帧视图（指向 buffer 内部），失败返回错误。</returns>
    AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer);

    /// <summary>逆向扫描。</summary>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    RbfReverseSequence ScanReverse(bool showTombstone = false);

    /// <summary>读取指定帧信息的帧到提供的 buffer 中（便捷重载）。</summary>
    /// <param name="info">帧元信息（来自 ScanReverse）。</param>
    /// <param name="buffer">目标缓冲区，长度必须 &gt;= info.Ticket.Length。</param>
    /// <returns>成功时返回帧视图（指向 buffer 内部），失败返回错误。</returns>
    AteliaResult<RbfFrame> ReadFrame(in RbfFrameInfo info, Span<byte> buffer);

    /// <summary>读取指定帧信息的帧（从 ArrayPool 借缓存，便捷重载）。</summary>
    /// <param name="info">帧元信息（来自 ScanReverse）。</param>
    /// <returns>成功时返回帧，调用方 MUST 调用 Dispose() 归还 buffer。</returns>
    AteliaResult<RbfPooledFrame> ReadPooledFrame(in RbfFrameInfo info);

    /// <summary>durable flush（落盘）。</summary>
    /// <remarks>
    /// <para>用于上层 commit 顺序（例如 data→meta）的 durable 边界。</para>
    /// </remarks>
    void DurableFlush();

    /// <summary>截断（恢复用）。</summary>
    void Truncate(long newLengthBytes);
}
