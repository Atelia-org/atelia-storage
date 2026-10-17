using System.Buffers.Binary;
using Atelia.Data;

namespace Atelia.HeadStore;

/// <summary>Entry 的自有定位值，保存紧凑 head 定位值与完整 SizedPtr ticket。</summary>
/// <remarks>
/// 可由库操作取得或从持久编码恢复；值操作独立于 owner 生命周期。
/// 解析成功不证明目标存在、发布成立、历史归属或内容健康，操作时仍须验证。
/// default 可比较但不能编码或定位。跨库定位误用仅概率检出。
/// </remarks>
public readonly struct HeadEntryId : IEquatable<HeadEntryId> {
    /// <summary>Handle encoding v1 的固定字节数，不包含托管布局的 padding。</summary>
    public const int EncodedSize = 12;

    private readonly HeadId _headId;
    private readonly SizedPtr _ticket;

    private HeadEntryId(HeadId headId, SizedPtr ticket) {
        _headId = headId;
        _ticket = ticket;
    }

    /// <summary>定位值声明的 Entry 所属 head；default 返回 default，实际归属由操作验证。</summary>
    public HeadId HeadId => _headId;

    /// <summary>定位值声明的完整位置与长度；default 返回 default，实际记录由操作验证。</summary>
    public SizedPtr Ticket => _ticket;

    /// <summary>按 Handle encoding v1 写入4B HeadId 和8B UInt64LE ticket。</summary>
    /// <remarks>
    /// ticketBits 固定为 ((offset / 4) &lt;&lt; 26) | (length / 4)，即高38bit偏移、低26bit长度。
    /// 当前对应 SizedPtr.Packed，不采用其 Serialize 交错编码；未来内部表示变化不能改变本v1合同。
    /// 无需 owner 或 I/O，版本与种类由外层容器指定。
    /// </remarks>
    /// <param name="destination">至少具有 <see cref="EncodedSize"/> 字节的目标；成功仅写前12B。</param>
    /// <returns>HeadId 为 default、ticket 长度为零或容量不足时返回 false，目标全部保持不变。</returns>
    public bool TryWrite(Span<byte> destination) {
        if (destination.Length < EncodedSize || _headId == default || _ticket.Length == 0) { return false; }
        _headId.TryWrite(destination[..HeadId.EncodedSize]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[HeadId.EncodedSize..], _ticket.Packed);
        return true;
    }

    /// <summary>从恰好12B的 Handle encoding v1 编码恢复 Entry 定位值。</summary>
    /// <remarks>仅检查非零 HeadId 和正 ticket 长度；不验证 header 后界、完成范围或实际记录。</remarks>
    /// <param name="source">恰好 <see cref="EncodedSize"/> 字节；不接受尾随字节。</param>
    /// <param name="value">成功时为恢复的值；失败时为 default。</param>
    /// <returns>长度不符、HeadId 为零或 ticket 长度为零时返回 false。</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out HeadEntryId value) {
        value = default;
        if (source.Length != EncodedSize || !HeadId.TryRead(source[..HeadId.EncodedSize], out HeadId headId)) { return false; }
        SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(source[HeadId.EncodedSize..]));
        if (ticket.Length == 0) { return false; }
        value = new HeadEntryId(headId, ticket);
        return true;
    }

    // 库操作由完成发布、checked Entry read 或完整 checked HeadSetTag 成员表恢复调用。
    // 公开 TryRead 也能恢复定位值；所有坐标仍须在使用时精确检查。
    internal static HeadEntryId Create(HeadId headId, SizedPtr ticket) {
        return new HeadEntryId(headId, ticket);
    }

    /// <summary>比较 head 编码值和 ticket 的全部 Packed bits，包含长度。</summary>
    public bool Equals(HeadEntryId other) {
        return _headId == other._headId && _ticket.Packed == other._ticket.Packed;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is HeadEntryId other && Equals(other);
    }

    /// <summary>运行期间的纯值哈希，不属于持久格式。</summary>
    public override int GetHashCode() {
        return HashCode.Combine(_headId, _ticket.Packed);
    }

    /// <summary>比较紧凑 Head 编码与完整 ticket。</summary>
    public static bool operator ==(HeadEntryId left, HeadEntryId right) {
        return left.Equals(right);
    }

    /// <summary>比较紧凑 Head 编码与完整 ticket。</summary>
    public static bool operator !=(HeadEntryId left, HeadEntryId right) {
        return !left.Equals(right);
    }
}
