using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore;

/// <summary>绑定持久 VersionStore 身份的自有、不透明 ref 值。</summary>
/// <remarks>default 可比较但不能定位或编码。LocalId 只在显式选择的库上下文中有意义。</remarks>
public readonly struct RefId : IEquatable<RefId> {
    private readonly ulong _identityFirst;
    private readonly ulong _identitySecond;
    private readonly uint _localId;

    private RefId(StoreIdentity identity, uint localId) {
        _identityFirst = identity.First;
        _identitySecond = identity.Second;
        _localId = localId;
    }

    /// <summary>完整本地编号；default 返回 0。此投影不证明目标存在或内容健康。</summary>
    public uint LocalId => _localId;

    // 仅保存调用方已资格化的身份与非零编号；不在发布确认后的交付中重新校验或执行 I/O。
    internal static RefId Create(StoreIdentity identity, uint localId) {
        return new RefId(identity, localId);
    }

    internal bool BelongsTo(StoreIdentity identity) {
        return _localId != 0 && identity != default &&
            _identityFirst == identity.First && _identitySecond == identity.Second;
    }

    /// <summary>比较完整持久身份与本地编号，不访问 owner 或磁盘。</summary>
    public bool Equals(RefId other) {
        return _identityFirst == other._identityFirst && _identitySecond == other._identitySecond &&
            _localId == other._localId;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is RefId other && Equals(other);
    }

    /// <summary>运行期间的纯值哈希，不属于持久格式。</summary>
    public override int GetHashCode() {
        return HashCode.Combine(_identityFirst, _identitySecond, _localId);
    }

    /// <summary>比较完整 ref 身份。</summary>
    public static bool operator ==(RefId left, RefId right) {
        return left.Equals(right);
    }

    /// <summary>比较完整 ref 身份。</summary>
    public static bool operator !=(RefId left, RefId right) {
        return !left.Equals(right);
    }
}
