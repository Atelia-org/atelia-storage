using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.FrameStore;

/// <summary>一个 FrameStore 上下文中的不透明帧地址。</summary>
/// <remarks>
/// 地址相等仅在同一个 store 中有意义。数值地址不证明原始 store、实际帧、完成或耐久资格。
/// default 可以比较，但不能持久编码；固定编码不构成 CLR 内存布局合同。
/// </remarks>
public readonly struct FrameAddress : IEquatable<FrameAddress> {
    /// <summary>一条 canonical 地址的固定编码字节数。</summary>
    public const int EncodedSize = 12;

    private readonly uint _fileId;
    private readonly SizedPtr _ticket;

    private FrameAddress(uint fileId, SizedPtr ticket) {
        _fileId = fileId;
        _ticket = ticket;
    }

    // 仅保存已由 owner/RBF 取得资格的数值；成功追加的交付路径不得重新校验或进行 I/O。
    internal static FrameAddress Create(uint fileId, SizedPtr ticket) {
        return new FrameAddress(fileId, ticket);
    }

    internal uint FileId => _fileId;
    internal SizedPtr Ticket => _ticket;

    /// <summary>从恰好 12B 的 LittleEndian 编码读取地址；拒绝时返回 false 和 default。</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out FrameAddress address) {
        address = default;
        if (source.Length != EncodedSize) { return false; }

        uint fileId = BinaryPrimitives.ReadUInt32LittleEndian(source);
        SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(source[sizeof(uint)..]));
        if (fileId == 0 ||
            ticket.Offset < RbfScanBoundary.Empty.EndExclusive ||
            ticket.Length < RbfFile.MeasureWriteSize(0, 0).Value.FrameLength) {
            return false;
        }

        address = Create(fileId, ticket);
        return true;
    }

    /// <summary>将地址写入目标的前 12B；拒绝时整个目标保持不变。</summary>
    public bool TryWrite(Span<byte> destination) {
        if (this == default || destination.Length < EncodedSize) { return false; }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, _fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(uint)..], _ticket.Packed);
        return true;
    }

    /// <summary>比较同一 store 中的完整地址数值。</summary>
    public bool Equals(FrameAddress other) {
        return _fileId == other._fileId && _ticket.Packed == other._ticket.Packed;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is FrameAddress other && Equals(other);
    }

    /// <summary>返回仅供运行期间使用的哈希；不属于持久格式。</summary>
    public override int GetHashCode() {
        return HashCode.Combine(_fileId, _ticket.Packed);
    }

    /// <summary>比较完整地址数值。</summary>
    public static bool operator ==(FrameAddress left, FrameAddress right) {
        return left.Equals(right);
    }

    /// <summary>比较完整地址数值。</summary>
    public static bool operator !=(FrameAddress left, FrameAddress right) {
        return !left.Equals(right);
    }
}
