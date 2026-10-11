using Atelia.Rbf;
using Atelia.VersionStore.Internal.Platform;

namespace Atelia.VersionStore.Internal.Storage;

/// <summary>工厂与 tag 文件操作失败窗口的窄接缝；公开工厂只将默认操作交给返回的 owner。</summary>
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

    internal virtual IRbfFile CreatePending(string path) => RbfFile.CreateNew(path, RbfCacheMode.Off);

    // The default is the exact public RBF path previously used by the directory backend.
    // Internal process tests may observe the report produced by the actual VS lazy open.
    internal virtual IRbfFile OpenRef(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        recovery = default;
        return readOnly
            ? RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)
            : RbfFile.OpenExisting(path, out recovery, RbfCacheMode.Off);
    }

    internal virtual FileStream ProbeTagBucket(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal virtual IRbfFile OpenTagBucket(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        recovery = default;
        return readOnly
            ? RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)
            : RbfFile.OpenExisting(path, out recovery, RbfCacheMode.Off);
    }

    internal virtual void CreateTagsDirectory(string path, Action markOwnedCleanupFault) =>
        VersionStorePlatform.CreateDirectory(path, markOwnedCleanupFault);

    internal virtual void PublishPendingTagBucket(string source, string target, Action markOwnedCleanupFault) =>
        VersionStorePlatform.MoveFileNoOverwrite(source, target, markOwnedCleanupFault);

    internal virtual void BeforeOwnerHandoff(string admittedRoot) { }
}
