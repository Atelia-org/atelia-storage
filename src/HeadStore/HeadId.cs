using Atelia.Data;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore;

/// <summary>由库的稳定 HandleKey 编码的 4B、不透明 head 值。</summary>
/// <remarks>default 不能定位。跨库误用仅概率检出；相等不证明完整库身份相同。</remarks>
public readonly struct HeadId : IEquatable<HeadId> {
    private readonly uint _encoded;

    private HeadId(uint encoded) { _encoded = encoded; }

    // 调用方已资格化身份与编号；发布确认后的交付不新增校验、异常或 I/O。
    internal static HeadId Create(StoreIdentity identity, uint localId) =>
        new(ZeroPreservingXor32.Apply(localId, identity.HandleKey));

    // 仅供已检查的内部路径；解码结果本身不证明存在或完整库身份。
    internal uint GetLocalId(StoreIdentity identity) =>
        ZeroPreservingXor32.Apply(_encoded, identity.HandleKey);

    internal bool TryGetLocalId(StoreIdentity identity, out uint localId) {
        localId = GetLocalId(identity);
        if (identity != default && localId is > 0 and <= HeadStore.MaxLocalHeadId) { return true; }
        localId = 0;
        return false;
    }

    /// <summary>比较编码值；不同库的值可能相等。</summary>
    public bool Equals(HeadId other) => _encoded == other._encoded;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HeadId other && Equals(other);

    /// <summary>运行期间的纯值哈希，不属于持久格式。</summary>
    public override int GetHashCode() => _encoded.GetHashCode();

    /// <summary>比较编码值。</summary>
    public static bool operator ==(HeadId left, HeadId right) => left.Equals(right);

    /// <summary>比较编码值。</summary>
    public static bool operator !=(HeadId left, HeadId right) => !left.Equals(right);
}
