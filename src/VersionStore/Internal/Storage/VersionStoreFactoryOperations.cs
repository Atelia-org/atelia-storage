using Atelia.VersionStore.Internal.Platform;

namespace Atelia.VersionStore.Internal.Storage;

/// <summary>工厂失败窗口的窄接缝；按调用注入，不交给返回的 owner。</summary>
internal class VersionStoreFactoryOperations {
    internal static readonly VersionStoreFactoryOperations Default = new();

    internal virtual void BeforeCreateOwnerLock(string admittedRoot) { }

    internal virtual IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) =>
        VersionStorePlatform.AcquireOwnerLock(root, create, readOnly);

    internal virtual FileStream CreateGate(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    internal virtual FileStream ReadGate(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal virtual IEnumerator<string> EnumerateDirectory(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        }).GetEnumerator();

    internal virtual void DeletePending(string path) => VersionStorePlatform.DeleteFile(path);

    internal virtual void BeforeOwnerHandoff(string admittedRoot) { }
}
