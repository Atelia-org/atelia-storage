using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Platform;
using Atelia.VersionStore.Internal.Runtime;

namespace Atelia.VersionStore.Internal.Storage;

/// <summary>已准入根与持续 owner 锁之下的正式路径和唯一 pending 生命周期。</summary>
/// <remarks>不拥有锁，不解释 ref/tag 内容；公开工厂只将默认文件操作交给 owner。</remarks>
internal sealed partial class DirectoryVersionStoreFiles : IVersionStoreFiles, IVersionStoreTagFiles {
    private readonly string _root;
    private readonly VersionStoreFactoryOperations _operations;

    internal DirectoryVersionStoreFiles(string admittedRoot, VersionStoreFactoryOperations? operations = null) {
        ArgumentNullException.ThrowIfNull(admittedRoot);
        _root = admittedRoot;
        _operations = operations ?? VersionStoreFactoryOperations.Default;
    }

    internal static void CheckFreshInput(string root, VersionStoreFactoryOperations operations) {
        EnumerateDirect(root, operations, path => {
            string name = Path.GetFileName(path);
            if (name != VersionStorePaths.LockFileName) {
                throw new InvalidDataException($"Create requires a fresh root; unexpected entry '{name}'.");
            }
            VersionStorePlatform.RequireEnumeratedFile(path);
            VersionStorePlatform.RequireFileOnFileSystem(root, path);
        });
    }

    internal static FormatGate InitializeEmptyStore(string root, StoreIdentity dataIdentity, uint reservedUpperBound,
        VersionStoreFactoryOperations operations) {
        VersionStorePlatform.CreateDirectory(Path.Combine(root, VersionStorePaths.RefsDirectoryName));
        VersionStorePlatform.CreateDirectory(Path.Combine(root, VersionStorePaths.CreatingDirectoryName));
        CheckLayout(root);
        var gate = new FormatGate(StoreIdentity.CreateRandom(), dataIdentity, reservedUpperBound);
        Span<byte> bytes = stackalloc byte[FormatGateCodec.EncodedSize];
        if (!FormatGateCodec.TryWrite(gate, bytes)) { throw new InvalidOperationException("Cannot encode the VersionStore format gate."); }
        string path = Path.Combine(root, VersionStorePaths.FormatFileName);
        FileStream? stream = null;
        CleanupErrors errors = default;
        try {
            stream = operations.CreateGate(path);
            VersionStorePlatform.RequireFileOnFileSystem(root, path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        return gate;
    }

    internal static FormatGate ReadGate(string root, StoreIdentity expectedDataIdentity,
        VersionStoreFactoryOperations operations) {
        string path = Path.Combine(root, VersionStorePaths.FormatFileName);
        VersionStorePlatform.RequireFile(path);
        VersionStorePlatform.RequireFileOnFileSystem(root, path);
        FileStream? stream = null;
        FormatGate gate = default;
        CleanupErrors errors = default;
        try {
            stream = operations.ReadGate(path);
            if (stream.Length != FormatGateCodec.EncodedSize) {
                throw new InvalidDataException("The VersionStore format gate must contain exactly 44 bytes.");
            }
            Span<byte> bytes = stackalloc byte[FormatGateCodec.EncodedSize];
            stream.ReadExactly(bytes);
            if (!FormatGateCodec.TryRead(bytes, out gate)) {
                throw new InvalidDataException("The VersionStore format gate is invalid.");
            }
            if (gate.DataStoreId != expectedDataIdentity) {
                throw new InvalidDataException("The VersionStore format gate is bound to different data.");
            }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        return gate;
    }

    internal static void CheckLayout(string root) {
        foreach (string name in new[] { VersionStorePaths.RefsDirectoryName, VersionStorePaths.CreatingDirectoryName }) {
            string path = Path.Combine(root, name);
            VersionStorePlatform.RequireDirectory(path);
            VersionStorePlatform.RequireSameFileSystem(root, path);
        }
        string tags = Path.Combine(root, VersionStorePaths.TagsDirectoryName);
        if (VersionStorePlatform.TryRequireDirectory(tags)) {
            VersionStorePlatform.RequireSameFileSystem(root, tags);
        }
    }

    public AteliaResult<QualifiedRefTarget> ResolveRef(uint localId, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        if (localId == 0) { return InvalidLocalId(); }
        string path = RefPath(localId);
        VersionStorePlatform.RequireDirectory(RefsPath, markOwnedCleanupFault);
        if (!VersionStorePlatform.TryRequireFile(path, markOwnedCleanupFault)) {
            return new VersionStoreRuntimeError("RefNotFound", "The formal ref file does not exist.");
        }
        VersionStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
        return new QualifiedRefTarget(localId, path);
    }

    public IRbfFile OpenRef(QualifiedRefTarget target, bool readOnly, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        // The target token obtained actual spelling/type qualification; content recovery is lazy.
        VersionStorePlatform.RequireEnumeratedFile(target.Path, markOwnedCleanupFault);
        VersionStorePlatform.RequireFileOnFileSystem(_root, target.Path, markOwnedCleanupFault);
        return _operations.OpenRef(target.Path, readOnly, out _);
    }

    public AteliaResult<IReadOnlyList<uint>> ListRefIds(Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        var ids = new List<uint>();
        AteliaError? error = VisitRefTargets(target => {
            ids.Add(target.LocalId);
            return null;
        }, static () => { }, markOwnedCleanupFault);
        if (error is not null) { return error; }
        return AteliaResult<IReadOnlyList<uint>>.Success(ids.AsReadOnly());
    }

    public AteliaError? VisitRefTargets(Func<QualifiedRefTarget, AteliaError?> visit, Action checkState,
        Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(checkState);
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        IEnumerator<string>? entries = null;
        AteliaError? result = null;
        CleanupErrors errors = default;
        try {
            checkState();
            VersionStorePlatform.RequireDirectory(RefsPath, markOwnedCleanupFault);
            checkState();
            VersionStorePlatform.RequireSameFileSystem(_root, RefsPath, markOwnedCleanupFault);
            checkState();
            VersionStorePlatform.RequireEnumeratedDirectory(RefsPath, markOwnedCleanupFault);
            checkState();
            entries = _operations.EnumerateDirectory(RefsPath);
            while (true) {
                checkState();
                bool hasEntry = entries.MoveNext();
                checkState();
                if (!hasEntry) { break; }
                string path = entries.Current;
                checkState();
                string name = Path.GetFileName(path);
                if (!VersionStorePaths.TryParseRefFileName(name, out uint id)) { throw InvalidEntry(name); }
                VersionStorePlatform.RequireEnumeratedFile(path, markOwnedCleanupFault);
                checkState();
                VersionStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
                checkState();
                result = visit(new QualifiedRefTarget(id, path));
                checkState();
                if (result is not null) { break; }
            }
            checkState();
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref entries, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
        checkState();
        return result;
    }

    public AteliaResult<QualifiedRefTarget> PrepareNewRef(uint localId, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        if (localId == 0) { return InvalidLocalId(); }
        VersionStorePlatform.RequireDirectory(RefsPath, markOwnedCleanupFault);
        VersionStorePlatform.RequireSameFileSystem(_root, RefsPath, markOwnedCleanupFault);
        string path = RefPath(localId);
        if (VersionStorePlatform.TryRequireFile(path, markOwnedCleanupFault)) {
            VersionStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
            return new VersionStoreRuntimeError("RefAlreadyExists", "The formal ref file already exists.");
        }
        return new QualifiedRefTarget(localId, path);
    }

    public IRbfFile CreatePending(Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        VersionStorePlatform.RequireDirectory(CreatingPath, markOwnedCleanupFault);
        VersionStorePlatform.RequireSameFileSystem(_root, CreatingPath, markOwnedCleanupFault);
        VersionStorePlatform.RequireExactComponent(CreatingPath, VersionStorePaths.PendingFileName,
            allowMissing: true, markOwnedCleanupFault);
        IRbfFile? file = null;
        CleanupErrors errors = default;
        try {
            file = _operations.CreatePending(PendingPath);
            VersionStorePlatform.RequireFileOnFileSystem(_root, PendingPath, markOwnedCleanupFault);
            IRbfFile output = file;
            file = null;
            return output;
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref file, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
        throw new InvalidOperationException("Unreachable pending handoff.");
    }

    public void PublishPending(QualifiedRefTarget target, Action markOwnedCleanupFault) {
        VersionStorePlatform.MoveFileNoOverwrite(PendingPath, target.Path, markOwnedCleanupFault);
    }

    /// <summary>完整发现正式 ref（writer）之后才调用；只按 pending 名称/类型取消，不读字节。</summary>
    internal void QualifyPending(bool readOnly, Action? markOwnedCleanupFault = null) {
        string? pending = null;
        EnumerateDirect(CreatingPath, _operations, path => {
            string name = Path.GetFileName(path);
            if (pending is not null || name != VersionStorePaths.PendingFileName) { throw InvalidEntry(name); }
            VersionStorePlatform.RequireEnumeratedFile(path, markOwnedCleanupFault);
            VersionStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
            pending = path;
        }, markOwnedCleanupFault);
        if (pending is not null && !readOnly) { _operations.DeletePending(pending); }
    }

    private string RefsPath => Path.Combine(_root, VersionStorePaths.RefsDirectoryName);
    private string CreatingPath => Path.Combine(_root, VersionStorePaths.CreatingDirectoryName);
    private string PendingPath => Path.Combine(CreatingPath, VersionStorePaths.PendingFileName);
    private string RefPath(uint localId) => Path.Combine(_root, VersionStorePaths.GetRefRelativePath(localId));

    private static AteliaError InvalidLocalId() =>
        new VersionStoreRuntimeError("ArgumentError", "A nonzero local ref number is required.");

    private static InvalidDataException InvalidEntry(string name) =>
        new($"Unexpected VersionStore directory entry '{name}'.");

    private static void EnumerateDirect(string directory, VersionStoreFactoryOperations operations,
        Action<string> visit, Action? markOwnedCleanupFault = null) {
        IEnumerator<string>? entries = null;
        CleanupErrors errors = default;
        try {
            VersionStorePlatform.RequireEnumeratedDirectory(directory, markOwnedCleanupFault);
            entries = operations.EnumerateDirectory(directory);
            while (entries.MoveNext()) { visit(entries.Current); }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref entries, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
    }

    private static void CloseOwned<T>(ref T? slot, ref CleanupErrors errors,
        Action? markOwnedCleanupFault = null) where T : class, IDisposable {
        T? toClose = slot;
        slot = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { markOwnedCleanupFault?.Invoke(); errors.Add(error); }
    }
}
