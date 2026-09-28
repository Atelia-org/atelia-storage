using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>已覆盖前缀的结束位置及其最后一帧的内容见证。</summary>
/// <remarks>持久化见证不赋予信任；ScanForward 每次重新校验 anchor。</remarks>
public readonly record struct RbfScanBoundary(long EndExclusive, SizedPtr AnchorTicket, uint AnchorContentCrc32C) {
    /// <summary>仅覆盖 HeaderFence 的空前缀；文件仍可含后续帧。</summary>
    public static RbfScanBoundary Empty => new(4, default, 0);
}
