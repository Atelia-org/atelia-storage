namespace Atelia.VersionStore;

/// <summary>成功历史查询的终止原因；预算耗尽返回失败 Result。</summary>
public enum HistoryEnd {
    /// <summary>本次固定发布前缀已到达无来源的合法起点。</summary>
    Complete,

    /// <summary>visitor 已接收当前项并要求正常停止。</summary>
    VisitorStopped
}
