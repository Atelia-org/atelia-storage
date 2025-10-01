using System.Buffers;

namespace Atelia.Data;

/// <summary>
/// 在 <see cref="IBufferWriter{T} " /> 协议基础上，扩展出“预留区段并延后回填”的写入契约。
/// </summary>
/// <remarks>
/// <para>核心语义：</para>
/// <list type="bullet">
/// <item>
/// <description>单生产者顺序写入：<see cref="GetSpan(int)"/> / <see cref="GetMemory(int)"/> / <see cref="Advance(int)"/> / <see cref="ReserveSpan(int, out int, string?)"/> / <see cref="Commit(int)"/> 必须由同一线程按调用顺序执行，禁止并发访问。</description>
/// </item>
/// <item>
/// <description><see cref="GetSpan(int)"/>（或 <see cref="GetMemory(int)"/>）与 <see cref="Advance(int)"/> 必须严格配对。调用者在再次索取缓冲或预留之前必须先调用 <see cref="Advance(int)"/>（若无需写入可传入 0 表示放弃先前缓冲）。</description>
/// </item>
/// <item>
/// <description>在调用 <see cref="GetSpan(int)"/> 或 <see cref="GetMemory(int)"/> 后、执行对应的 <see cref="Advance(int)"/> 之前，禁止再次调用 <see cref="GetSpan(int)"/>、<see cref="GetMemory(int)"/> 或 <see cref="ReserveSpan(int, out int, string?)"/>；调用方必须先完成配对的 <see cref="Advance(int)"/>。</description>
/// </item>
/// <item>
/// <description><see cref="ReserveSpan(int, out int, string?)"/> 只能在不存在未 <see cref="Advance(int)"/> 的普通缓冲时调用；返回的 <see cref="Span{Byte}"/> 在对应的 <see cref="Commit(int)"/>、<see cref="IDisposable.Dispose"/>（若实现支持）或实现自带的 <c>Reset</c> 发生前始终有效，即使其间调用了新的 <see cref="GetSpan"/> / <see cref="ReserveSpan"/>。</description>
/// </item>
/// <item>
/// <description><see cref="Commit(int)"/> 之后，底层实现需确保所有比该 reservation 更早写入的数据最终可以推进到下游目标；若实现选择延迟冲刷，必须提供显式 <c>Flush</c> 或在生命周期结束时自动冲刷。</description>
/// </item>
/// </list>
/// <para>状态约束（写入方需要遵守下列调用序列）：</para>
/// <list type="bullet">
/// <item>
/// <description>&lt;b&gt;空闲 → 借用普通缓冲&lt;/b&gt;：调用 <see cref="GetSpan(int)"/> 或 <see cref="GetMemory(int)"/> 进入“待 Advance”状态。</description>
/// </item>
/// <item>
/// <description>&lt;b&gt;待 Advance&lt;/b&gt;：唯一允许的调用是 <see cref="Advance(int)"/>（可传 0 表示放弃）。完成后回到空闲状态，可继续请求新的缓冲或进行预留。</description>
/// </item>
/// <item>
/// <description>&lt;b&gt;空闲 → 预留缓冲&lt;/b&gt;：在没有“待 Advance”缓冲时调用 <see cref="ReserveSpan(int, out int, string?)"/>；预留 span 持续有效直至配对的 <see cref="Commit(int)"/>。</description>
/// </item>
/// <item>
/// <description>&lt;b&gt;待 Commit&lt;/b&gt;：每个 reservation 需调用一次 <see cref="Commit(int)"/>。实现应阻止重复提交、未知 token 或未提交 reservation 即结束生命周期。</description>
/// </item>
/// </list>
/// <para>任何违反顺序的调用都应在实现中导致异常，以便尽早暴露调用方错误。</para>
/// </remarks>
public interface IReservableBufferWriter : IBufferWriter<byte> {
    /// <summary>
    /// 预留一段将来回填的数据空间。返回该空间的 Span 供直接写入；同时生成 reservationToken 用于后续 Commit。
    /// </summary>
    /// <param name="count">需要预留的字节数（确切值）。</param>
    /// <param name="reservationToken">输出：用于 Commit 的 token。</param>
    /// <param name="tag">可选的调试注解，帮助定位未提交的 reservation。</param>
    /// <returns>预留区域的可写 Span（初始内容未定义）。</returns>
    /// <remarks>
    /// 调用该方法前必须确保不存在未 <see cref="Advance(int)"/> 的缓冲租借；若需要提前放弃上一段缓冲，可调用 <see cref="Advance(int)"/> 并传入 0。
    /// </remarks>
    Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null);

    /// <summary>
    /// 提交指定 reservation，表示该区域已填充完成，可以参与连续前缀 flush。
    /// </summary>
    /// <param name="reservationToken">由 <see cref="ReserveSpan"/> 返回的 token。</param>
    void Commit(int reservationToken);
}
