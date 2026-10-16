using Atelia.Data;

namespace Atelia.HeadStore;

/// <summary>已接受Entry的自有位置值，保存完整 head 身份与完整 SizedPtr ticket。</summary>
/// <remarks>值操作独立于 owner 生命周期；值本身不证明当前内容健康或旧调用的发布证据。</remarks>
public readonly struct HeadEntryId : IEquatable<HeadEntryId> {
    private readonly HeadId _headId;
    private readonly SizedPtr _ticket;

    private HeadEntryId(HeadId headId, SizedPtr ticket) {
        _headId = headId;
        _ticket = ticket;
    }

    /// <summary>该Entry实际所属的 head；default 返回 default。</summary>
    public HeadId HeadId => _headId;

    /// <summary>该Entry实际的完整位置与长度；default 返回 default。</summary>
    public SizedPtr Ticket => _ticket;

    // 仅由完成发布或 checked-read 的真实成员交付路径调用。声明来源/结构扫描不能单独签发 entryId。
    internal static HeadEntryId Create(HeadId headId, SizedPtr ticket) {
        return new HeadEntryId(headId, ticket);
    }

    /// <summary>比较完整 head 身份和 ticket 的全部 Packed bits，包含长度。</summary>
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

    /// <summary>比较完整Entry位置值。</summary>
    public static bool operator ==(HeadEntryId left, HeadEntryId right) {
        return left.Equals(right);
    }

    /// <summary>比较完整Entry位置值。</summary>
    public static bool operator !=(HeadEntryId left, HeadEntryId right) {
        return !left.Equals(right);
    }
}
