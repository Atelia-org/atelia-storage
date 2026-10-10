using System.Buffers.Binary;
using Atelia.Binary;
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
        if (!IsValidNumericAddress(fileId, ticket)) { return false; }

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

    /// <summary>返回最短 VarUInt32 FileId + VarUInt64 交错 ticket 编码的字节数。</summary>
    /// <exception cref="InvalidOperationException">地址为 default，不能持久编码。</exception>
    public int MeasureVarInt() {
        if (this == default) { throw new InvalidOperationException("A default address cannot be encoded."); }

        return BareValueEncoding.MeasureVarUInt32(_fileId) + BareValueEncoding.MeasureVarUInt64(_ticket.Serialize());
    }

    /// <summary>按 FileId、交错 ticket 顺序写入最短 VarUInt 编码。</summary>
    /// <remarks>输出故障直接传播，已写入的字段不回滚。</remarks>
    /// <exception cref="InvalidOperationException">地址为 default；拒绝前不访问 writer 的 sink。</exception>
    public void WriteVarInt(BareValueWriter writer) {
        if (this == default) { throw new InvalidOperationException("A default address cannot be encoded."); }

        writer.WriteVarUInt32(_fileId);
        writer.WriteVarUInt64(_ticket.Serialize());
    }

    /// <summary>读取 VarUInt32 FileId + VarUInt64 交错 ticket；成功后才推进 reader。</summary>
    /// <remarks>接受 Binary 支持的有界冗余表示，不消费地址后的字段。</remarks>
    /// <exception cref="EndOfStreamException">地址字段截短；reader 保持不变。</exception>
    /// <exception cref="InvalidDataException">整数溢出或地址数值不合法；reader 保持不变。</exception>
    public static FrameAddress ReadVarInt(ref BareValueReader reader) {
        BareValueReader copy = reader;
        uint fileId = copy.ReadVarUInt32();
        SizedPtr ticket = SizedPtr.Deserialize(copy.ReadVarUInt64());
        if (!IsValidNumericAddress(fileId, ticket)) {
            throw new InvalidDataException("The encoded address has invalid numeric coordinates.");
        }

        FrameAddress address = Create(fileId, ticket);
        reader = copy;
        return address;
    }

    private static bool IsValidNumericAddress(uint fileId, SizedPtr ticket) {
        return fileId != 0 &&
            ticket.Offset >= RbfScanBoundary.Empty.EndExclusive &&
            ticket.Length >= RbfFile.MeasureWriteSize(0, 0).Value.FrameLength;
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
