namespace Atelia.HeadStore;

/// <summary>本次同步调用的非持久发布证据，与 owner 健康及内容值分别解释。</summary>
public enum PublicationOutcome {
    /// <summary>尚未尝试改变公开发布事实。</summary>
    NotAttempted = 0,
    /// <summary>公开事实可能已生效，但本次调用无法确认。</summary>
    Unknown = 1,
    /// <summary>所需发布 flush 与发现边界均成功返回；后续异常不撤销该证据。</summary>
    Confirmed = 2
}
