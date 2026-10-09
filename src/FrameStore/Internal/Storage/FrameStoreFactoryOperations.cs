using Atelia.FrameStore.Internal.Platform;

namespace Atelia.FrameStore.Internal.Storage;

/// <summary>工厂初始化的窄 I/O 入口；测试依赖按调用传入，不交给返回的 owner。</summary>
/// <remarks>默认实例无状态；不提供全局可变 hook 或一般文件系统替换。</remarks>
internal class FrameStoreFactoryOperations {
    internal static readonly FrameStoreFactoryOperations Default = new();

    internal virtual void BeforeCreateOwnerLock(string admittedRoot) { }

    internal virtual IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) =>
        FrameStorePlatform.AcquireOwnerLock(root, create, readOnly);

    internal virtual FileStream CreateGate(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    internal virtual FileStream ReadGate(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal virtual FileStream ReadPrivateCandidate(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
