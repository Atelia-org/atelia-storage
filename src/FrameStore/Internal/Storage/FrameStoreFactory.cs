using System.Collections.ObjectModel;
using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Platform;
using Atelia.FrameStore.Internal.Runtime;
using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Storage;

/// <summary>公开 owner 的唯一资格与资源移交入口。</summary>
internal static class FrameStoreFactory {
    private static readonly IReadOnlyDictionary<uint, RbfTailRecoveryReport> EmptyReports =
        new ReadOnlyDictionary<uint, RbfTailRecoveryReport>(new Dictionary<uint, RbfTailRecoveryReport>());

    internal static FrameStore Create(string rootPath, long rotationThresholdBytes,
        FrameStoreFactoryOperations? operations = null) {
        operations ??= FrameStoreFactoryOperations.Default;
        RotationThreshold.Validate(rotationThresholdBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string root = Path.GetFullPath(rootPath);
        // Input/config preflight precedes even root/control bootstrap. Only the final root may be created.
        if (FrameStorePlatform.TryAdmitRoot(root)) {
            root = FrameStorePlatform.AdmitRoot(root, createIfMissing: false);
            DirectoryFrameStoreFiles.CheckFreshInput(root, allowOwnerLock: true);
            _ = DirectoryFrameStoreFiles.ReadConfiguration(root);
        }
        root = FrameStorePlatform.AdmitRoot(root, createIfMissing: true);

        IDisposable? ownerLock = null;
        FrameStoreCore? core = null;
        try {
            operations.BeforeCreateOwnerLock(root);
            ownerLock = operations.AcquireOwnerLock(root, create: true, readOnly: false);
            // A competing Create may have completed after the preflight and before lock acquisition.
            DirectoryFrameStoreFiles.CheckFreshInput(root, allowOwnerLock: true);
            var config = DirectoryFrameStoreFiles.ReadConfiguration(root);
            var identity = DirectoryFrameStoreFiles.InitializeEmptyStore(root, operations);
            var files = new DirectoryFrameStoreFiles(root, identity);
            core = new FrameStoreCore(files, ownerLock, 0, writable: true,
                rotationThresholdBytes, config.MaxOutstandingBuilders);
            ownerLock = null;
            var store = new FrameStore(core, identity, readOnly: false, EmptyReports);
            core = null;
            return store;
        }
        catch (Exception error) {
            CleanupErrors errors = default;
            errors.Add(error);
            Close(ref core, ref errors);
            Close(ref ownerLock, ref errors);
            errors.ThrowIfAny();
            throw;
        }
    }

    internal static FrameStore Open(string rootPath, long rotationThresholdBytes, bool readOnly,
        FrameStoreFactoryOperations? operations = null) {
        operations ??= FrameStoreFactoryOperations.Default;
        if (!readOnly) { RotationThreshold.Validate(rotationThresholdBytes); }
        string root = FrameStorePlatform.AdmitRoot(rootPath, createIfMissing: false);
        IDisposable? ownerLock = null;
        FrameStoreCore? core = null;
        IRbfFile? pendingFile = null;
        try {
            ownerLock = operations.AcquireOwnerLock(root, create: false, readOnly);
            var config = readOnly ? FrameStoreConfiguration.Default : DirectoryFrameStoreFiles.ReadConfiguration(root);
            var identity = DirectoryFrameStoreFiles.ReadGateAndCheckLayout(root, operations);
            var files = new DirectoryFrameStoreFiles(root, identity);
            var discovery = files.DiscoverFormalFiles();
            files.QualifyPrivateCreation(discovery.MaxPublishedFileId, readOnly, operations: operations);

            var reports = new Dictionary<uint, RbfTailRecoveryReport>(readOnly ? 0 : discovery.ActiveFileIds.Count);
            var publicReports = new ReadOnlyDictionary<uint, RbfTailRecoveryReport>(reports);
            core = new FrameStoreCore(files, ownerLock, discovery.MaxPublishedFileId, writable: !readOnly,
                rotationThresholdBytes, config.MaxOutstandingBuilders);
            ownerLock = null;
            foreach (uint fileId in discovery.ActiveFileIds) {
                if (readOnly) {
                    files.CheckReadOnlyActive(fileId);
                }
                else {
                    pendingFile = files.OpenQualifiedActive(fileId, out var recovery);
                    reports.Add(fileId, recovery);
                    core.AdoptQualifiedActive(fileId, pendingFile);
                    pendingFile = null;
                }
            }
            if (!readOnly) { core.CompleteWritableHandoff(); }
            var store = new FrameStore(core, identity, readOnly, publicReports);
            core = null;
            return store;
        }
        catch (Exception error) {
            CleanupErrors errors = default;
            errors.Add(error);
            // An untransferred file is cleaned before core.Dispose releases retained files and the lock.
            Close(ref pendingFile, ref errors);
            Close(ref core, ref errors);
            Close(ref ownerLock, ref errors);
            errors.ThrowIfAny();
            throw;
        }
    }

    private static void Close<T>(ref T? slot, ref CleanupErrors errors) where T : class, IDisposable {
        var resource = slot;
        slot = null;
        if (resource is null) { return; }
        try { resource.Dispose(); }
        catch (Exception error) { errors.Add(error); }
    }
}
