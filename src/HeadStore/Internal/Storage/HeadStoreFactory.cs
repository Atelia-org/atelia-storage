using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Platform;
using Atelia.HeadStore.Internal.Runtime;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Internal.Storage;

/// <summary>公开 owner 的根、锁、门与目录资格移交入口。</summary>
internal static class HeadStoreFactory {
    internal static HeadStore Create(string rootPath, DataStore data, uint reservedHeadIdUpperBound,
        HeadStoreFactoryOperations? operations = null, HeadStoreTestHooks? testHooks = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(data);
        if (reservedHeadIdUpperBound == uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(reservedHeadIdUpperBound));
        }
        string root = Path.GetFullPath(rootPath);
        StoreIdentity dataIdentity = CheckBorrowedData(data, readOnly: false);
        operations ??= HeadStoreFactoryOperations.Default;

        // Pure input and borrowed-owner checks precede every HeadStore path operation.
        if (HeadStorePlatform.TryAdmitRoot(root)) {
            root = HeadStorePlatform.AdmitRoot(root, createIfMissing: false);
            DirectoryHeadStoreFiles.CheckFreshInput(root, operations);
        }
        root = HeadStorePlatform.AdmitRoot(root, createIfMissing: true);

        IDisposable? ownerLock = null;
        try {
            operations.BeforeCreateOwnerLock(root);
            ownerLock = operations.AcquireOwnerLock(root, create: true, readOnly: false);
            // Another Create can finish between preflight and our exclusive acquisition.
            DirectoryHeadStoreFiles.CheckFreshInput(root, operations);
            FormatGate gate = DirectoryHeadStoreFiles.InitializeEmptyStore(root, dataIdentity,
                reservedHeadIdUpperBound, operations);
            operations.BeforeOwnerHandoff(root);
            // Only the explicitly internal hook path hands runtime observers to the owner.
            // Public calls and existing factory failure tests retain default runtime operations.
            var recoveryLog = new HeadStoreRecoveryLog();
            var files = new DirectoryHeadStoreFiles(root, testHooks is null ? null : operations, recoveryLog);
            var store = new HeadStore(data, gate.HeadStoreId, gate.DataStoreId,
                gate.ReservedHeadIdUpperBound, readOnly: false, files,
                ownerLock, testHooks, autoHeadIdHighWatermark: gate.ReservedHeadIdUpperBound, recoveryLog: recoveryLog);
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

    internal static HeadStore Open(string rootPath, DataStore data, bool readOnly,
        HeadStoreFactoryOperations? operations = null, HeadStoreTestHooks? testHooks = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(data);
        string root = Path.GetFullPath(rootPath);
        StoreIdentity dataIdentity = CheckBorrowedData(data, readOnly);
        operations ??= HeadStoreFactoryOperations.Default;
        root = HeadStorePlatform.AdmitRoot(root, createIfMissing: false);

        IDisposable? ownerLock = null;
        try {
            ownerLock = operations.AcquireOwnerLock(root, create: false, readOnly);
            // The gate owns a temporary handle; its successful close precedes layout and discovery.
            FormatGate gate = DirectoryHeadStoreFiles.ReadGate(root, dataIdentity, operations);
            DirectoryHeadStoreFiles.CheckLayout(root);
            var files = new DirectoryHeadStoreFiles(root, operations);
            uint highWatermark = gate.ReservedHeadIdUpperBound;
            if (!readOnly) {
                AteliaError? discoveryError = files.VisitHeadTargets(target => {
                    highWatermark = Math.Max(highWatermark, target.LocalId);
                    return null;
                }, static () => { }, static () => { });
                if (discoveryError is not null) { throw new InvalidDataException(discoveryError.ToString()); }
            }
            files.QualifyPending(readOnly);
            operations.BeforeOwnerHandoff(root);
            // As in Create, runtime interception requires explicit internal test hooks.
            var recoveryLog = new HeadStoreRecoveryLog();
            var runtimeFiles = new DirectoryHeadStoreFiles(root, testHooks is null ? null : operations, recoveryLog);
            var store = new HeadStore(data, gate.HeadStoreId, gate.DataStoreId,
                gate.ReservedHeadIdUpperBound, readOnly, runtimeFiles, ownerLock, testHooks,
                autoHeadIdHighWatermark: highWatermark, recoveryLog: recoveryLog);
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
            throw new ArgumentException("A writable HeadStore requires writable borrowed data.", nameof(data));
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
