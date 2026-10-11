namespace Atelia.VersionStore;

/// <summary>同步 tag 元信息扫描的正常结束原因。</summary>
public enum TagScanEnd {
    /// <summary>全部实际已建桶的必要元信息、正常 EOF 与清理均已完成。</summary>
    Complete,
    /// <summary>visitor 接收当前项后返回 false，未继续访问后续记录或桶。</summary>
    VisitorStopped
}
