using System.Buffers;

namespace Atelia.Data;
public interface IReservableBufferWriter : IBufferWriter<byte> {
    /// <summary>
    /// 预留一段将来回填的数据空间。返回该空间的 Span 供直接写入；同时生成 reservationToken 用于后续 Commit。
    /// </summary>
    /// <param name="count">需要预留的字节数（确切值）。</param>
    /// <param name="reservationToken">输出：用于 Commit 的 token。</param>
    /// <param name="tag">可选的调试注解，帮助定位未提交的 reservation。</param>
    /// <returns>预留区域的可写 Span（初始内容未定义）。</returns>
    Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null);

    /// <summary>
    /// 提交指定 reservation，表示该区域已填充完成，可以参与连续前缀 flush。
    /// </summary>
    void Commit(int reservationToken);
}
