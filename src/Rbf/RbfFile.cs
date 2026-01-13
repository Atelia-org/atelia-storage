namespace Atelia.Rbf;

/// <summary>
/// RBF 文件静态工厂类。
/// </summary>
public static class RbfFile {
    /// <summary>
    /// 创建新的 RBF 文件（FailIfExists）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>RBF 文件对象。</returns>
    public static IRbfFile CreateNew(string path) {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 打开已有的 RBF 文件（验证 Genesis）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>RBF 文件对象。</returns>
    public static IRbfFile OpenExisting(string path) {
        throw new NotImplementedException();
    }
}
