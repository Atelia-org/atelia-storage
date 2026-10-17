using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Platform;
using Atelia.HeadStore.Internal.Runtime;

namespace Atelia.HeadStore.Internal.Storage;

/// <summary>已准入根与持续 owner 锁之下的正式路径和唯一 pending 生命周期。</summary>
/// <remarks>不拥有锁，不解释 head/tag 内容；公开工厂只将默认文件操作交给 owner。</remarks>
internal sealed partial class DirectoryHeadStoreFiles : IHeadStoreFiles, IHeadStoreTagFiles {
    private readonly string _root;
    private readonly HeadStoreFactoryOperations _operations;
    private readonly HeadStoreRecoveryLog _recoveryLog;

    internal DirectoryHeadStoreFiles(string admittedRoot, HeadStoreFactoryOperations? operations = null,
        HeadStoreRecoveryLog? recoveryLog = null) {
        ArgumentNullException.ThrowIfNull(admittedRoot);
        _root = admittedRoot;
        _operations = operations ?? HeadStoreFactoryOperations.Default;
        _recoveryLog = recoveryLog ?? new HeadStoreRecoveryLog();
    }

    internal static void CheckFreshInput(string root, HeadStoreFactoryOperations operations) {
        EnumerateDirect(root, operations, path => {
            string name = Path.GetFileName(path);
            if (name != HeadStorePaths.LockFileName) {
                throw new InvalidDataException($"Create requires a fresh root; unexpected entry '{name}'.");
            }
            HeadStorePlatform.RequireEnumeratedFile(path);
            HeadStorePlatform.RequireFileOnFileSystem(root, path);
        });
    }

    internal static FormatGate InitializeEmptyStore(string root, StoreIdentity dataIdentity, uint reservedUpperBound,
        HeadStoreFactoryOperations operations) {
        HeadStorePlatform.CreateDirectory(Path.Combine(root, HeadStorePaths.HeadsDirectoryName));
        HeadStorePlatform.CreateDirectory(Path.Combine(root, HeadStorePaths.CreatingDirectoryName));
        CheckLayout(root);
        var gate = new FormatGate(StoreIdentity.CreateRandom(), dataIdentity, reservedUpperBound);
        Span<byte> bytes = stackalloc byte[FormatGateCodec.EncodedSize];
        if (!FormatGateCodec.TryWrite(gate, bytes)) { throw new InvalidOperationException("Cannot encode the HeadStore format gate."); }
        string path = Path.Combine(root, HeadStorePaths.FormatFileName);
        FileStream? stream = null;
        CleanupErrors errors = default;
        try {
            stream = operations.CreateGate(path);
            HeadStorePlatform.RequireFileOnFileSystem(root, path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        return gate;
    }

    internal static FormatGate ReadGate(string root, StoreIdentity expectedDataIdentity,
        HeadStoreFactoryOperations operations) {
        string path = Path.Combine(root, HeadStorePaths.FormatFileName);
        HeadStorePlatform.RequireFile(path);
        HeadStorePlatform.RequireFileOnFileSystem(root, path);
        FileStream? stream = null;
        FormatGate gate = default;
        CleanupErrors errors = default;
        try {
            stream = operations.ReadGate(path);
            if (stream.Length != FormatGateCodec.EncodedSize) {
                throw new InvalidDataException("The HeadStore format gate must contain exactly 44 bytes.");
            }
            Span<byte> bytes = stackalloc byte[FormatGateCodec.EncodedSize];
            stream.ReadExactly(bytes);
            if (!FormatGateCodec.TryRead(bytes, out gate)) {
                throw new InvalidDataException("The HeadStore format gate is invalid.");
            }
            if (gate.DataStoreId != expectedDataIdentity) {
                throw new InvalidDataException("The HeadStore format gate is bound to different data.");
            }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        return gate;
    }

    internal static void CheckLayout(string root) {
        foreach (string name in new[] { HeadStorePaths.HeadsDirectoryName, HeadStorePaths.CreatingDirectoryName }) {
            string path = Path.Combine(root, name);
            HeadStorePlatform.RequireDirectory(path);
            HeadStorePlatform.RequireSameFileSystem(root, path);
        }
        string tags = Path.Combine(root, HeadStorePaths.TagsDirectoryName);
        if (HeadStorePlatform.TryRequireDirectory(tags)) {
            HeadStorePlatform.RequireSameFileSystem(root, tags);
        }
    }

    public AteliaResult<QualifiedHeadTarget> ResolveHead(uint localId, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        if (localId is 0 or > HeadStore.MaxLocalHeadId) { return InvalidLocalId(); }
        string path = HeadPath(localId);
        HeadStorePlatform.RequireDirectory(HeadsPath, markOwnedCleanupFault);
        if (!HeadStorePlatform.TryRequireFile(path, markOwnedCleanupFault)) {
            return new HeadStoreRuntimeError("HeadNotFound", "The formal head file does not exist.");
        }
        HeadStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
        return new QualifiedHeadTarget(localId, path);
    }

    public IRbfFile OpenHead(QualifiedHeadTarget target, bool readOnly, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        // The target token obtained actual spelling/type qualification; content recovery is lazy.
        HeadStorePlatform.RequireEnumeratedFile(target.Path, markOwnedCleanupFault);
        HeadStorePlatform.RequireFileOnFileSystem(_root, target.Path, markOwnedCleanupFault);
        if (!readOnly) { _recoveryLog.PrepareAppend(); }
        IRbfFile file = _operations.OpenHead(target.Path, readOnly, out RbfTailRecoveryReport recovery);
        if (!readOnly) { _recoveryLog.Record(HeadStoreRecoveryFileKind.Head, target.LocalId, recovery); }
        return file;
    }

    public AteliaResult<IReadOnlyList<uint>> ListHeadIds(Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        var ids = new List<uint>();
        AteliaError? error = VisitHeadTargets(target => {
            ids.Add(target.LocalId);
            return null;
        }, static () => { }, markOwnedCleanupFault);
        if (error is not null) { return error; }
        return AteliaResult<IReadOnlyList<uint>>.Success(ids.AsReadOnly());
    }

    public AteliaError? VisitHeadTargets(Func<QualifiedHeadTarget, AteliaError?> visit, Action checkState,
        Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(checkState);
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        IEnumerator<string>? entries = null;
        AteliaError? result = null;
        CleanupErrors errors = default;
        try {
            checkState();
            HeadStorePlatform.RequireDirectory(HeadsPath, markOwnedCleanupFault);
            checkState();
            HeadStorePlatform.RequireSameFileSystem(_root, HeadsPath, markOwnedCleanupFault);
            checkState();
            HeadStorePlatform.RequireEnumeratedDirectory(HeadsPath, markOwnedCleanupFault);
            checkState();
            entries = _operations.EnumerateDirectory(HeadsPath);
            while (true) {
                checkState();
                bool hasEntry = entries.MoveNext();
                checkState();
                if (!hasEntry) { break; }
                string path = entries.Current;
                checkState();
                string name = Path.GetFileName(path);
                if (!HeadStorePaths.TryParseHeadFileName(name, out uint id)) { throw InvalidEntry(name); }
                HeadStorePlatform.RequireEnumeratedFile(path, markOwnedCleanupFault);
                checkState();
                HeadStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
                checkState();
                result = visit(new QualifiedHeadTarget(id, path));
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

    public AteliaResult<QualifiedHeadTarget> PrepareNewHead(uint localId, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        if (localId is 0 or > HeadStore.MaxLocalHeadId) { return InvalidLocalId(); }
        HeadStorePlatform.RequireDirectory(HeadsPath, markOwnedCleanupFault);
        HeadStorePlatform.RequireSameFileSystem(_root, HeadsPath, markOwnedCleanupFault);
        string path = HeadPath(localId);
        if (HeadStorePlatform.TryRequireFile(path, markOwnedCleanupFault)) {
            HeadStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
            return new HeadStoreRuntimeError("HeadAlreadyExists", "The formal head file already exists.");
        }
        return new QualifiedHeadTarget(localId, path);
    }

    public IRbfFile CreatePending(Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        HeadStorePlatform.RequireDirectory(CreatingPath, markOwnedCleanupFault);
        HeadStorePlatform.RequireSameFileSystem(_root, CreatingPath, markOwnedCleanupFault);
        HeadStorePlatform.RequireExactComponent(CreatingPath, HeadStorePaths.PendingFileName,
            allowMissing: true, markOwnedCleanupFault);
        IRbfFile? file = null;
        CleanupErrors errors = default;
        try {
            file = _operations.CreatePending(PendingPath);
            HeadStorePlatform.RequireFileOnFileSystem(_root, PendingPath, markOwnedCleanupFault);
            IRbfFile output = file;
            file = null;
            return output;
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref file, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
        throw new InvalidOperationException("Unreachable pending handoff.");
    }

    public void PublishPending(QualifiedHeadTarget target, Action markOwnedCleanupFault) {
        HeadStorePlatform.MoveFileNoOverwrite(PendingPath, target.Path, markOwnedCleanupFault);
    }

    /// <summary>完整发现正式 head（writer）之后才调用；只按 pending 名称/类型取消，不读字节。</summary>
    internal void QualifyPending(bool readOnly, Action? markOwnedCleanupFault = null) {
        string? pending = null;
        EnumerateDirect(CreatingPath, _operations, path => {
            string name = Path.GetFileName(path);
            if (pending is not null || name != HeadStorePaths.PendingFileName) { throw InvalidEntry(name); }
            HeadStorePlatform.RequireEnumeratedFile(path, markOwnedCleanupFault);
            HeadStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
            pending = path;
        }, markOwnedCleanupFault);
        if (pending is not null && !readOnly) { _operations.DeletePending(pending); }
    }

    private string HeadsPath => Path.Combine(_root, HeadStorePaths.HeadsDirectoryName);
    private string CreatingPath => Path.Combine(_root, HeadStorePaths.CreatingDirectoryName);
    private string PendingPath => Path.Combine(CreatingPath, HeadStorePaths.PendingFileName);
    private string HeadPath(uint localId) => Path.Combine(_root, HeadStorePaths.GetHeadRelativePath(localId));

    private static AteliaError InvalidLocalId() =>
        new HeadStoreRuntimeError("HeadNotFound", "The head cannot be located in this HeadStore.");

    private static InvalidDataException InvalidEntry(string name) =>
        new($"Unexpected HeadStore directory entry '{name}'.");

    private static void EnumerateDirect(string directory, HeadStoreFactoryOperations operations,
        Action<string> visit, Action? markOwnedCleanupFault = null) {
        IEnumerator<string>? entries = null;
        CleanupErrors errors = default;
        try {
            HeadStorePlatform.RequireEnumeratedDirectory(directory, markOwnedCleanupFault);
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
