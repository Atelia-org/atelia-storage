namespace Atelia.Rbf;

/// <summary>由已打开文件的 Header 确定的 RBF 格式。</summary>
/// <remarks>0/default 未定义；格式信息不选择 writer profile。</remarks>
public enum RbfFormat {
    /// <summary>历史 RBF1 byte-wire 格式。</summary>
    Rbf1 = 1,

    /// <summary>当前 RBF3 格式。</summary>
    Rbf3 = 3
}
