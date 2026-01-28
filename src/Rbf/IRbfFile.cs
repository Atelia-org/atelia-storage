using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>RBF 文件对象门面。</summary>
/// <remarks>
/// 职责：资源管理（Dispose）、状态维护（TailOffset）、调用转发。
/// 并发约束：同一实例在任一时刻最多 1 个 open Builder。
/// </remarks>
public interface IRbfFile : IDisposable {
    /// <summary>获取当前文件逻辑长度（也是下一个写入 Offset）。</summary>
    long TailOffset { get; }

    /// <summary>追加完整帧（payload 已就绪）。</summary>
    /// <remarks>
    /// 失败场景（返回 <see cref="AteliaResult{T}.IsFailure"/>）：
    /// - TailMeta 超长（&gt; 64KB）
    /// - Payload + TailMeta 超长（&gt; MaxPayloadAndMetaLength）
    /// - TailOffset 非 4B 对齐或超出 SizedPtr 可表示范围
    /// I/O 错误（磁盘满、权限等）仍抛出异常。
    /// </remarks>
    AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default);

    /// <summary>复杂帧构建（流式写入 payload / payload 内回填）。</summary>
    /// <remarks>
    /// 注意：在 Builder Dispose/EndAppend 前，TailOffset 不会更新。
    /// 注意：存在 open Builder 时，不应允许并发 Append/BeginAppend。
    /// </remarks>
    RbfFrameBuilder BeginAppend();

    /// <summary>随机读（从 ArrayPool 借缓存）。</summary>
    /// <remarks>
    /// 调用方 MUST 调用返回值的 Dispose() 归还 buffer。
    /// 失败时 buffer 已自动归还。
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

    /// <summary>从 SizedPtr 获取帧元信息（只读 TrailerCodeword，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfFrameInfo，失败返回错误。</returns>
    /// <remarks>
    /// I/O：只读取 TrailerCodeword（16B），不读 Payload。
    /// 信任级别：L2（TrailerCrc 校验通过）。
    /// 此方法允许从持久化的 SizedPtr 恢复完整的帧元信息。
    /// </remarks>
    AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= info.TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 内部），失败返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// 若需完整数据完整性保证，请使用 <see cref="ReadFrame(SizedPtr ptr, Span{byte})"/>。
    /// </remarks>
    AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任，自动租用 buffer，从 SizedPtr）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledTailMeta，调用方 MUST 调用 Dispose() 归还 buffer。失败时返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// I/O：读取 TrailerCodeword（16B）+ TailMeta 区域。
    /// 此方法是 <c>ReadFrameInfo(ticket)</c> + <c>ReadPooledTailMeta(info)</c> 的便捷组合。
    /// </remarks>
    AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket);

    /// <summary>durable flush（落盘）。</summary>
    /// <remarks>
    /// 用于上层 commit 顺序（例如 data→meta）的 durable 边界。
    /// </remarks>
    void DurableFlush();

    /// <summary>截断（恢复用）。</summary>
    void Truncate(long newLengthBytes);
}
