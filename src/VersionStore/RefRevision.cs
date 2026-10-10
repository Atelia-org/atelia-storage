using Atelia.Data;

namespace Atelia.VersionStore;

/// <summary>已接受快照的自有位置值，保存完整 ref 身份与完整 SizedPtr ticket。</summary>
/// <remarks>值操作独立于 owner 生命周期；值本身不证明当前内容健康或旧调用的发布证据。</remarks>
public readonly struct RefRevision : IEquatable<RefRevision> {
    private readonly RefId _refId;
    private readonly SizedPtr _ticket;

    private RefRevision(RefId refId, SizedPtr ticket) {
        _refId = refId;
        _ticket = ticket;
    }

    /// <summary>该快照实际所属的 ref；default 返回 default。</summary>
    public RefId RefId => _refId;

    /// <summary>该快照实际的完整位置与长度；default 返回 default。</summary>
    public SizedPtr Ticket => _ticket;

    // 仅由完成发布或 checked-read 的真实成员交付路径调用。声明来源/结构扫描不能单独签发 revision。
    internal static RefRevision Create(RefId refId, SizedPtr ticket) {
        return new RefRevision(refId, ticket);
    }

    /// <summary>比较完整 ref 身份和 ticket 的全部 Packed bits，包含长度。</summary>
    public bool Equals(RefRevision other) {
        return _refId == other._refId && _ticket.Packed == other._ticket.Packed;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is RefRevision other && Equals(other);
    }

    /// <summary>运行期间的纯值哈希，不属于持久格式。</summary>
    public override int GetHashCode() {
        return HashCode.Combine(_refId, _ticket.Packed);
    }

    /// <summary>比较完整快照位置值。</summary>
    public static bool operator ==(RefRevision left, RefRevision right) {
        return left.Equals(right);
    }

    /// <summary>比较完整快照位置值。</summary>
    public static bool operator !=(RefRevision left, RefRevision right) {
        return !left.Equals(right);
    }
}
