namespace Atelia.FrameStore;

/// <summary>单个正式文件全部帧通过完整 CRC 检查后的审计报告。</summary>
/// <remarks>报告可在回调和 owner 生命周期之外保留；单文件报告不等于全库审计成功。</remarks>
public readonly struct FrameFileAudit {
    internal FrameFileAudit(uint fileId, ReadOnlyMemory<byte> headerPayload, long userFrameCount) {
        FileId = fileId;
        HeaderPayload = headerPayload;
        UserFrameCount = userFrameCount;
    }

    /// <summary>正式文件的非零编号；不定义业务历史顺序。</summary>
    public uint FileId { get; }

    /// <summary>完整校验过的 24B decoded 文件 header 的独立副本。</summary>
    public ReadOnlyMemory<byte> HeaderPayload { get; }

    /// <summary>通过完整校验的用户帧数，含墓碑，不含首帧 header。</summary>
    public long UserFrameCount { get; }
}
