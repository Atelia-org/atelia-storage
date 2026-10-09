namespace Atelia.FrameStore;

/// <summary>Inventory 产生的帧元信息快照，不持有 reader 或 buffer。</summary>
/// <remarks>已通过 framing 与 TrailerCRC 检查；不证明用户 PayloadCRC 正确或业务已发布。</remarks>
public readonly struct FrameInfo {
    internal FrameInfo(FrameAddress address, uint tag, int payloadLength, int tailMetaLength, bool isTombstone) {
        Address = address;
        Tag = tag;
        PayloadLength = payloadLength;
        TailMetaLength = tailMetaLength;
        IsTombstone = isTombstone;
    }

    /// <summary>在产生此结果的 store 中定位用户帧的地址。</summary>
    public FrameAddress Address { get; }

    /// <summary>帧标签，包含合法的用户标签 0。</summary>
    public uint Tag { get; }

    /// <summary>Payload 的字节长度，不含 TailMeta。</summary>
    public int PayloadLength { get; }

    /// <summary>TailMeta 的字节长度。</summary>
    public int TailMetaLength { get; }

    /// <summary>该用户帧是否为墓碑。</summary>
    public bool IsTombstone { get; }
}
