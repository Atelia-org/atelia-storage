using System.Buffers.Binary;
using Atelia.Data;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore;

/// <summary>由库的稳定 HandleKey 编码的 4B、不透明 head 值。</summary>
/// <remarks>
/// 可由库操作取得或从持久编码恢复；解析成功不证明目标存在或发布成立。
/// default 可比较但不能编码或定位。跨库误用仅概率检出；相等不证明完整库身份相同。
/// </remarks>
public readonly struct HeadId : IEquatable<HeadId> {
    /// <summary>Handle encoding v1 的固定字节数；不同于完整库身份。</summary>
    public const int EncodedSize = 4;

    private readonly uint _encoded;

    private HeadId(uint encoded) { _encoded = encoded; }

    /// <summary>将非零编码 Handle 按 Handle encoding v1 写为 UInt32LE。</summary>
    /// <remarks>无需 owner 或 I/O。版本与种类由外层容器指定，不写入本编码。</remarks>
    /// <param name="destination">至少具有 <see cref="EncodedSize"/> 字节的目标；成功仅写前4B。</param>
    /// <returns>default 或容量不足时返回 false，目标全部保持不变。</returns>
    public bool TryWrite(Span<byte> destination) {
        if (_encoded == 0 || destination.Length < EncodedSize) { return false; }
        BinaryPrimitives.WriteUInt32LittleEndian(destination, _encoded);
        return true;
    }

    /// <summary>从恰好4B的 Handle encoding v1 UInt32LE 编码恢复非零定位值。</summary>
    /// <remarks>无需 owner 或 I/O；不检查当前库的24bit本地编号范围、目标存在性或发布资格。</remarks>
    /// <param name="source">恰好 <see cref="EncodedSize"/> 字节；不接受尾随字节。</param>
    /// <param name="value">成功时为恢复的值；失败时为 default。</param>
    /// <returns>长度不符或编码为零时返回 false。</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out HeadId value) {
        value = default;
        if (source.Length != EncodedSize) { return false; }
        uint encoded = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (encoded == 0) { return false; }
        value = new HeadId(encoded);
        return true;
    }

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
