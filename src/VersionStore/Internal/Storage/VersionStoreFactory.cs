using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Platform;
using Atelia.VersionStore.Internal.Runtime;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore.Internal.Storage;

/// <summary>公开 owner 的根、锁、门与目录资格移交入口。</summary>
internal static class VersionStoreFactory {
    internal static VersionStore Create(string rootPath, DataStore data, uint reservedRefIdUpperBound,
        VersionStoreFactoryOperations? operations = null, VersionStoreTestHooks? testHooks = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(data);
        if (reservedRefIdUpperBound == uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(reservedRefIdUpperBound));
        }
        string root = Path.GetFullPath(rootPath);
        StoreIdentity dataIdentity = CheckBorrowedData(data, readOnly: false);
        operations ??= VersionStoreFactoryOperations.Default;

        // Pure input and borrowed-owner checks precede every VS path operation.
        if (VersionStorePlatform.TryAdmitRoot(root)) {
            root = VersionStorePlatform.AdmitRoot(root, createIfMissing: false);
            DirectoryVersionStoreFiles.CheckFreshInput(root, operations);
        }
        root = VersionStorePlatform.AdmitRoot(root, createIfMissing: true);

        IDisposable? ownerLock = null;
        try {
            operations.BeforeCreateOwnerLock(root);
            ownerLock = operations.AcquireOwnerLock(root, create: true, readOnly: false);
            // Another Create can finish between preflight and our exclusive acquisition.
            DirectoryVersionStoreFiles.CheckFreshInput(root, operations);
            FormatGate gate = DirectoryVersionStoreFiles.InitializeEmptyStore(root, dataIdentity,
                reservedRefIdUpperBound, operations);
            operations.BeforeOwnerHandoff(root);
            // Only the explicitly internal hook path hands runtime observers to the owner.
            // Public calls and existing factory failure tests retain default runtime operations.
            var recoveryLog = new VersionStoreRecoveryLog();
            var files = new DirectoryVersionStoreFiles(root, testHooks is null ? null : operations, recoveryLog);
            var store = new VersionStore(data, gate.VersionStoreId, gate.DataStoreId,
                gate.ReservedRefIdUpperBound, readOnly: false, files,
                ownerLock, testHooks, autoRefIdHighWatermark: gate.ReservedRefIdUpperBound, recoveryLog: recoveryLog);
            ownerLock = null;
            return store;
        }
        catch (Exception error) {
            CleanupErrors errors = default;
            errors.Add(error);
            Close(ref ownerLock, ref errors);
            errors.ThrowIfAny();
            throw;
        }
    }

    internal static VersionStore Open(string rootPath, DataStore data, bool readOnly,
        VersionStoreFactoryOperations? operations = null, VersionStoreTestHooks? testHooks = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(data);
        string root = Path.GetFullPath(rootPath);
        StoreIdentity dataIdentity = CheckBorrowedData(data, readOnly);
        operations ??= VersionStoreFactoryOperations.Default;
        root = VersionStorePlatform.AdmitRoot(root, createIfMissing: false);

        IDisposable? ownerLock = null;
        try {
            ownerLock = operations.AcquireOwnerLock(root, create: false, readOnly);
            // The gate owns a temporary handle; its successful close precedes layout and discovery.
            FormatGate gate = DirectoryVersionStoreFiles.ReadGate(root, dataIdentity, operations);
            DirectoryVersionStoreFiles.CheckLayout(root);
            var files = new DirectoryVersionStoreFiles(root, operations);
            uint highWatermark = gate.ReservedRefIdUpperBound;
            if (!readOnly) {
                AteliaError? discoveryError = files.VisitRefTargets(target => {
                    highWatermark = Math.Max(highWatermark, target.LocalId);
                    return null;
                }, static () => { }, static () => { });
                if (discoveryError is not null) { throw new InvalidDataException(discoveryError.ToString()); }
            }
            files.QualifyPending(readOnly);
            operations.BeforeOwnerHandoff(root);
            // As in Create, runtime interception requires explicit internal test hooks.
            var recoveryLog = new VersionStoreRecoveryLog();
            var runtimeFiles = new DirectoryVersionStoreFiles(root, testHooks is null ? null : operations, recoveryLog);
            var store = new VersionStore(data, gate.VersionStoreId, gate.DataStoreId,
                gate.ReservedRefIdUpperBound, readOnly, runtimeFiles, ownerLock, testHooks,
                autoRefIdHighWatermark: highWatermark, recoveryLog: recoveryLog);
            ownerLock = null;
            return store;
        }
        catch (Exception error) {
            CleanupErrors errors = default;
            errors.Add(error);
            Close(ref ownerLock, ref errors);
            errors.ThrowIfAny();
            throw;
        }
    }

    private static StoreIdentity CheckBorrowedData(DataStore data, bool readOnly) {
        data.EnsureUsable();
        if (!readOnly && data.IsReadOnly) {
            throw new ArgumentException("A writable VersionStore requires writable borrowed data.", nameof(data));
        }
        if (!StoreIdentity.TryRead(data.StoreId, out StoreIdentity identity)) {
            throw new ArgumentException("The borrowed data identity is invalid.", nameof(data));
        }
        return identity;
    }

    private static void Close<T>(ref T? slot, ref CleanupErrors errors) where T : class, IDisposable {
        T? toClose = slot;
        slot = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
    }
}
