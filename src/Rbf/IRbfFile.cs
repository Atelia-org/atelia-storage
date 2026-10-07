using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>RBF 文件对象门面。</summary>
/// <remarks>
/// 职责：资源管理（Dispose）、状态维护（TailOffset）、调用转发。
/// 串行约束：同一 File 及其派生对象访问共享 reader/cache、构建状态或执行 I/O 的操作，由调用方串行；包括 Dispose 与枚举器 MoveNext。不要求固定 OS 线程；独立只读实例可并行。
/// Builder 活跃期间，指定 ticket 的随机读取可读取已完成前缀，ticket 及终止 Fence 必须完整位于 TailOffset 以内；构建中帧不可读。扫描及物理后继读取仍拒绝。
/// 此前取得的 RbfFrameInfo 可串行读取历史帧。已物化数据及纯元信息值属性按原生命周期使用。
/// 实际文件输出、最终 Builder Commit 或 DurableFlush 的异常使实例永久 fault；新的读取、写入与扫描迭代均拒绝，须 Dispose 后重新打开。
/// 已物化的帧数据和值属性仍可使用；普通文件对象不提供任意截断。
/// </remarks>
public interface IRbfFile : IDisposable {
    /// <summary>获取由文件 Header 确定的不可变格式。</summary>
    RbfFormat Format { get; }

    /// <summary>获取当前文件逻辑长度（也是下一个写入 Offset）。</summary>
    long TailOffset { get; }

    /// <summary>追加完整帧（payload 已就绪）。</summary>
    /// <remarks>
    /// 失败场景（返回 <see cref="AteliaResult{T}.IsFailure"/>）：
    /// - TailMeta 超长（&gt; 64KB）
    /// - Payload + TailMeta 超长（&gt; <see cref="RbfFile.MaxPayloadAndMetaLength"/>）
    /// - TailOffset 非 4B 对齐或超出 SizedPtr 可表示范围
    /// I/O 错误（磁盘满、权限等）仍抛出异常。
    /// </remarks>
    AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default);

    /// <summary>复杂帧构建（流式写入 payload / payload 内回填）。</summary>
    /// <remarks>
    /// 注意：在 Builder Dispose/EndAppend 前，TailOffset 不会更新。
    /// 同一 File 及派生对象的共享状态访问由调用方串行；open Builder 时拒绝再次 Append/BeginAppend，允许随机读取已完成前缀，仍拒绝扫描及物理后继读取。
    /// </remarks>
    RbfFrameBuilder BeginAppend();

    /// <summary>按已知 payload 和 TailMeta 长度开始构建，并取得本次追加的提前位置凭据。</summary>
    /// <param name="payloadLength">最终 stored payload 的字节长度。</param>
    /// <param name="tailMetaLength">位于 payload 之后的 TailMeta 字节长度。</param>
    /// <param name="ticket">正常返回时绑定本次追加位置及尺寸的凭据，不证明帧已完成或耐久。</param>
    /// <remarks>
    /// 两部分共用 PayloadAndMeta 写入器；EndAppend(tag) 使用此处声明的 TailMeta 长度。
    /// 必须写入恰好声明的总字节数。Builder 活跃期间 TailOffset 不推进，其他追加被拒绝。
    /// 健康取消不输出，地址可能被以后追加复用；提前凭据没有追加尝试身份。
    /// 初始化失败不签发有效 Builder 或提前凭据，不保证后续资源操作成功。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">声明长度为负、TailMeta 超长或合计超过新写容量。</exception>
    RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket);

    /// <summary>随机读（从 ArrayPool 借缓存）。</summary>
    /// <remarks>
    /// 调用方 MUST 调用返回值的 Dispose() 归还 buffer。
    /// 失败时 buffer 已自动归还。
    /// Builder 活跃期间，ticket 及终止 Fence 必须完整位于已完成前缀；越界在租用 buffer 或 I/O 前抛 InvalidOperationException。
    /// </remarks>
    AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr);

    /// <summary>读取指定位置的帧到提供的 buffer 中。</summary>
    /// <param name="ptr">帧位置凭据。</param>
    /// <param name="buffer">目标缓冲区，长度必须 &gt;= ptr.Length。</param>
    /// <returns>成功时返回帧视图（指向 buffer 内部），失败返回错误。</returns>
    /// <remarks>Builder 活跃期间，ticket 及终止 Fence 必须完整位于已完成前缀；越界在 I/O 前抛 InvalidOperationException。</remarks>
    AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer);

    /// <summary>逆向扫描。</summary>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    RbfReverseSequence ScanReverse(bool showTombstone = false);

    /// <summary>正向扫描。</summary>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    RbfForwardSequence ScanForward(bool showTombstone = false);

    /// <summary>完整校验指定帧与尾 Fence，取得其后的扫描边界。</summary>
    AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket);

    /// <summary>验证边界见证后直接扫描后缀，不扫描此前的帧。</summary>
    AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false);

    /// <summary>计算指定帧结束后的下一个物理帧 Offset。</summary>
    /// <param name="ticket">已验证或可信来源提供的帧位置凭据。</param>
    /// <remarks>
    /// 返回值等于该帧的半开区间结束位置加上尾部 Fence 长度。
    /// 调用方不应复制 RBF wire-format 常量来自行计算这个位置。
    /// </remarks>
    long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket);

    /// <summary>读取指定帧之后的直接物理后继帧元信息。</summary>
    /// <param name="ticket">已验证或可信来源提供的前一帧位置凭据。</param>
    /// <returns>
    /// 成功时返回后继帧元信息；若前一帧已经位于文件尾部，返回 <see cref="OptionalRbfFrameInfo.None"/>。
    /// 若后继位置越界、未对齐或后继帧结构损坏，返回 RBF 错误。
    /// </returns>
    AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket);

    /// <summary>从 SizedPtr 获取帧元信息（只读 TrailerCodeword，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfFrameInfo，失败返回错误。</returns>
    /// <remarks>
    /// I/O：只读取 TrailerCodeword（16B），不读 Payload。
    /// 信任级别：L2（TrailerCrc 校验通过）。
    /// 此方法允许从持久化的 SizedPtr 恢复完整的帧元信息。
    /// Builder 活跃期间，ticket 及终止 Fence 必须完整位于已完成前缀；越界在 I/O 前抛 InvalidOperationException。
    /// </remarks>
    AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= info.TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 内部），失败返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// 若需完整数据完整性保证，请使用 <see cref="ReadFrame(SizedPtr, Span{byte})"/>。
    /// Builder 活跃期间，ticket 及终止 Fence 必须完整位于已完成前缀；越界在 I/O 前抛 InvalidOperationException。
    /// </remarks>
    AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任，自动租用 buffer，从 SizedPtr）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledTailMeta，调用方 MUST 调用 Dispose() 归还 buffer。失败时返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// I/O：读取 TrailerCodeword（16B）+ TailMeta 区域。
    /// 此方法是 <c>ReadFrameInfo(ticket)</c> + <c>ReadPooledTailMeta(info)</c> 的便捷组合。
    /// Builder 活跃期间，ticket 及终止 Fence 必须完整位于已完成前缀；越界在租用 buffer 或 I/O 前抛 InvalidOperationException。
    /// </remarks>
    AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket);

    /// <summary>durable flush（落盘）。</summary>
    /// <remarks>
    /// 用于上层 commit 顺序（例如 data→meta）的 durable 边界。
    /// 允许在 Builder 活跃期间调用；成功时保持其构建内容、借用及 reservation，不输出、完成或取消当前 Builder。
    /// 后续 Builder 提交的新输出需要新的 flush；flush 失败使共享实例永久 fault。
    /// </remarks>
    void DurableFlush();

    /// <summary>启用或禁用读取 I/O 日志。</summary>
    /// <param name="logPath">日志文件路径。传 null 禁用日志。</param>
    /// <remarks>
    /// 延迟生效：在下一次 Read 操作时切换，当前进行中的 Read 不受影响。
    /// 日志格式：CSV（seq, offset, requested, bytesRead, rawCount, rawBytes, ioTicks, cacheTicks, hitmap）。
    /// </remarks>
    void SetupReadLog(string? logPath);
}
