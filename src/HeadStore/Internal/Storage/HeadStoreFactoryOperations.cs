using Atelia.Rbf;
using Atelia.HeadStore.Internal.Platform;

namespace Atelia.HeadStore.Internal.Storage;

/// <summary>工厂与 tag 文件操作失败窗口的窄接缝；公开工厂只将默认操作交给返回的 owner。</summary>
internal class HeadStoreFactoryOperations {
    internal static readonly HeadStoreFactoryOperations Default = new();

    internal virtual void BeforeCreateOwnerLock(string admittedRoot) { }

    internal virtual IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) =>
        HeadStorePlatform.AcquireOwnerLock(root, create, readOnly);

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

    internal virtual void DeletePending(string path) => HeadStorePlatform.DeleteFile(path);

    internal virtual IRbfFile CreatePending(string path) => RbfFile.CreateNew(path, RbfCacheMode.Off);

    // The default is the exact public RBF path previously used by the directory backend.
    // Internal process tests may observe the report produced by the actual HeadStore lazy open.
    internal virtual IRbfFile OpenHead(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
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
        HeadStorePlatform.CreateDirectory(path, markOwnedCleanupFault);

    internal virtual void PublishPendingTagBucket(string source, string target, Action markOwnedCleanupFault) =>
        HeadStorePlatform.MoveFileNoOverwrite(source, target, markOwnedCleanupFault);

    internal virtual void BeforeOwnerHandoff(string admittedRoot) { }
}
