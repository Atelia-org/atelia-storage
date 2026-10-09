using Atelia.Rbf;

namespace Atelia.FrameStore;

/// <summary>独立拥有完整 CRC 校验结果的帧。调用方负责释放其 buffer。</summary>
public sealed class FrameRead : IDisposable {
    private readonly RbfPooledFrame _frame;

    internal FrameRead(FrameAddress address, RbfPooledFrame frame) {
        Address = address;
        _frame = frame;
    }

    /// <summary>在产生此结果的 store 中定位该帧的地址。</summary>
    public FrameAddress Address { get; }

    /// <summary>帧标签。</summary>
    public uint Tag => _frame.Tag;

    /// <summary>帧的完整 Payload 与 TailMeta；释放后访问抛 ObjectDisposedException。</summary>
    public ReadOnlySpan<byte> PayloadAndMeta => _frame.PayloadAndMeta;

    /// <summary>PayloadAndMeta 尾部的 TailMeta 长度。</summary>
    public int TailMetaLength => _frame.TailMetaLength;

    /// <summary>该帧是否为墓碑。</summary>
    public bool IsTombstone => _frame.IsTombstone;

    /// <summary>一次性归还自有 buffer；重复调用无效。</summary>
    public void Dispose() => _frame.Dispose();
}
