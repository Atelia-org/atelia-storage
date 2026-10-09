using System.Security.Cryptography;
using Atelia.FrameStore.Internal.Admission;
using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Platform;
using Atelia.FrameStore.Internal.Runtime;
using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Storage;

/// <summary>已准入根、持续 owner 锁及 checked 格式门之下的文件生命周期。</summary>
/// <remarks>不取得或释放 owner 锁；工厂必须按门、完整名称、私有资格、active 恢复的顺序调用。</remarks>
internal sealed class DirectoryFrameStoreFiles : IFrameStoreFiles {
    internal const string GateFileName = "framestore.format";
    internal const string ConfigurationFileName = "framestore.config.json";
    internal const string OwnerLockFileName = "framestore.lock";
    internal const string CreatingDirectoryName = "creating";

    private readonly string _root;
    private readonly StoreIdentity _identity;

    internal DirectoryFrameStoreFiles(string admittedRoot, StoreIdentity identity) {
        ArgumentNullException.ThrowIfNull(admittedRoot);
        if (identity == default) { throw new ArgumentException("A checked store identity is required.", nameof(identity)); }
        _root = admittedRoot;
        _identity = identity;
    }

    internal static FrameStoreConfiguration ReadConfiguration(string root) {
        // Create preflight must not create a missing root merely to inspect optional config.
        if (!FrameStorePlatform.TryAdmitRoot(root)) { return FrameStoreConfiguration.Default; }
        string path = Path.Combine(root, ConfigurationFileName);
        // TryRequireFile checks the actual canonical spelling as well as native ordinary/no-follow type.
        if (!FrameStorePlatform.TryRequireFile(path)) { return FrameStoreConfiguration.Default; }
        FileStream? stream = null;
        FrameStoreConfiguration configuration = default;
        CleanupErrors errors = default;
        try {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (!FrameStoreConfiguration.TryParse(bytes, out configuration)) {
                throw new InvalidDataException("The FrameStore configuration is invalid.");
            }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        return configuration;
    }

    /// <summary>只验证 fresh 输入的名称与类型；config 内容和同句柄 lock 长度由各自入口检查。</summary>
    internal static void CheckFreshInput(string root, bool allowOwnerLock) {
        if (!FrameStorePlatform.TryAdmitRoot(root)) { return; }
        EnumerateDirect(root, path => {
            string name = Path.GetFileName(path);
            if (name == ConfigurationFileName || (allowOwnerLock && name == OwnerLockFileName)) {
                FrameStorePlatform.RequireEnumeratedFile(path);
                return;
            }
            throw new InvalidDataException($"Create requires a fresh root; unexpected entry '{name}'.");
        });
    }

    /// <summary>已持独占锁的 fresh 根中先建必要空目录，最后直接 create-only 写门。</summary>
    internal static StoreIdentity InitializeEmptyStore(string root) {
        FrameStorePlatform.CreateDirectory(Path.Combine(root, FrameStorePaths.ActiveDirectoryName));
        FrameStorePlatform.CreateDirectory(Path.Combine(root, FrameStorePaths.ArchiveDirectoryName));
        FrameStorePlatform.CreateDirectory(Path.Combine(root, CreatingDirectoryName));
        CheckLayout(root);
        Span<byte> identityBytes = stackalloc byte[StoreIdentity.EncodedSize];
        StoreIdentity identity;
        do { RandomNumberGenerator.Fill(identityBytes); }
        while (!StoreIdentity.TryRead(identityBytes, out identity));
        Span<byte> gate = stackalloc byte[FormatGateCodec.EncodedSize];
        if (!FormatGateCodec.TryWrite(identity, gate)) { throw new InvalidOperationException("Cannot encode the store identity."); }
        FileStream? stream = null;
        CleanupErrors errors = default;
        try {
            stream = new FileStream(Path.Combine(root, GateFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(gate);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        return identity;
    }

    /// <summary>共同只读门检查正常关闭后才认领身份；不补造布局。</summary>
    internal static StoreIdentity ReadGateAndCheckLayout(string root) {
        string path = Path.Combine(root, GateFileName);
        FrameStorePlatform.RequireFile(path);
        FileStream? stream = null;
        StoreIdentity identity = default;
        CleanupErrors errors = default;
        try {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != FormatGateCodec.EncodedSize) { throw new InvalidDataException("The FrameStore format gate must contain exactly 24 bytes."); }
            Span<byte> bytes = stackalloc byte[FormatGateCodec.EncodedSize];
            stream.ReadExactly(bytes);
            if (!FormatGateCodec.TryRead(bytes, out identity)) { throw new InvalidDataException("The FrameStore format gate is invalid."); }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        CheckLayout(root);
        return identity;
    }

    private static void CheckLayout(string root) {
        foreach (string name in new[] { FrameStorePaths.ActiveDirectoryName, FrameStorePaths.ArchiveDirectoryName, CreatingDirectoryName }) {
            string path = Path.Combine(root, name);
            FrameStorePlatform.RequireDirectory(path);
            FrameStorePlatform.RequireSameFileSystem(root, path);
        }
    }

    internal FrameStoreDiscovery DiscoverFormalFiles() {
        var active = new List<uint>();
        var activeIds = new HashSet<uint>();
        uint max = 0;
        EnumerateDirect(Path.Combine(_root, FrameStorePaths.ActiveDirectoryName), path => {
            string name = Path.GetFileName(path);
            if (!FrameStorePaths.TryParseFileName(name, out uint id)) { throw InvalidEntry(name); }
            FrameStorePlatform.RequireEnumeratedFile(path);
            if (!activeIds.Add(id)) { throw new InvalidDataException("An active FileId occurs more than once."); }
            active.Add(id);
            max = Math.Max(max, id);
        });
        EnumerateDirect(Path.Combine(_root, FrameStorePaths.ArchiveDirectoryName), bucketPath => {
            string bucketName = Path.GetFileName(bucketPath);
            if (!FrameStorePaths.TryParseBucketName(bucketName, out _)) { throw InvalidEntry(bucketName); }
            FrameStorePlatform.RequireEnumeratedDirectory(bucketPath);
            FrameStorePlatform.RequireSameFileSystem(_root, bucketPath);
            EnumerateDirect(bucketPath, path => {
                string name = Path.GetFileName(path);
                if (!FrameStorePaths.TryParseArchiveFileName(bucketName, name, out uint id)) { throw InvalidEntry(name); }
                FrameStorePlatform.RequireEnumeratedFile(path);
                if (activeIds.Contains(id)) { throw new InvalidDataException($"FileId {id} occurs in both active and archive."); }
                max = Math.Max(max, id);
            });
        });
        active.Sort();
        return new FrameStoreDiscovery(active, max);
    }

    /// <summary>完整正式发现之后、任何恢复之前裁决唯一私有项；只读保留，可写只删合格项。</summary>
    internal void QualifyPrivateCreation(uint maxPublishedFileId, bool readOnly) {
        string? candidate = null;
        uint candidateId = 0;
        EnumerateDirect(Path.Combine(_root, CreatingDirectoryName), path => {
            string name = Path.GetFileName(path);
            if (candidate is not null || !FrameStorePaths.TryParseFileName(name, out uint id)) { throw InvalidEntry(name); }
            FrameStorePlatform.RequireEnumeratedFile(path);
            candidate = path;
            candidateId = id;
        });
        if (candidate is null) { return; }
        if (maxPublishedFileId == uint.MaxValue || candidateId != checked(maxPublishedFileId + 1)) {
            throw new InvalidDataException("The private creation FileId is not the next formal FileId.");
        }
        FileStream? stream = null;
        CleanupErrors errors = default;
        try {
            stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length < 0 || length > FileHeaderCodec.InitializationBoundary) {
                throw new InvalidDataException("The private creation file exceeds the initialization boundary.");
            }
            Span<byte> bytes = stackalloc byte[checked((int)FileHeaderCodec.InitializationBoundary)];
            Span<byte> actual = bytes[..checked((int)length)];
            stream.ReadExactly(actual);
            Span<byte> header = stackalloc byte[FileHeaderCodec.PayloadSize];
            FileHeaderCodec.TryWrite(_identity, candidateId, header);
            if (!RbfFile.IsInitialFramePrefix(actual, 0, header)) {
                throw new InvalidDataException("The private creation file is not an allowed initialization prefix.");
            }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
        if (!readOnly) { FrameStorePlatform.DeleteFile(candidate); }
    }

    internal IRbfFile OpenQualifiedActive(uint fileId, out RbfTailRecoveryReport recovery) {
        string path = ActivePath(fileId);
        // The complete discovery or our own canonical publication already qualified this name.
        FrameStorePlatform.RequireEnumeratedFile(path);
        CheckInitializedLength(path);
        IRbfFile? file = null;
        CleanupErrors errors = default;
        recovery = default;
        try {
            file = RbfFile.OpenExisting(path, out recovery, RbfCacheMode.Off);
            if (recovery.Action != RbfTailRecoveryAction.None &&
                (!recovery.AffectedFrameOffset.HasValue || recovery.AffectedFrameOffset.Value < FileHeaderCodec.InitializationBoundary)) {
                throw new InvalidDataException("RBF recovery affected the required initialization region.");
            }
            var header = FrameHeaderReader.Check(file, _identity, fileId);
            if (header.IsFailure) { throw new InvalidDataException(header.Error!.ToString()); }
            var output = file;
            file = null;
            return output;
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref file, ref errors);
        errors.ThrowIfAny();
        throw new InvalidOperationException("Unreachable active-file handoff.");
    }

    internal void CheckReadOnlyActive(uint fileId) {
        string path = ActivePath(fileId);
        FrameStorePlatform.RequireEnumeratedFile(path);
        IRbfFile? file = null;
        CleanupErrors errors = default;
        try {
            file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
            var header = FrameHeaderReader.Check(file, _identity, fileId);
            if (header.IsFailure) { throw new InvalidDataException(header.Error!.ToString()); }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref file, ref errors);
        errors.ThrowIfAny();
    }

    public IRbfFile CreateActive(uint fileId) {
        string privatePath = Path.Combine(_root, CreatingDirectoryName, FrameStorePaths.GetFileName(fileId));
        IRbfFile? file = null;
        CleanupErrors errors = default;
        try {
            file = RbfFile.CreateNew(privatePath, RbfCacheMode.Off);
            Span<byte> header = stackalloc byte[FileHeaderCodec.PayloadSize];
            FileHeaderCodec.TryWrite(_identity, fileId, header);
            var append = file.Append(0, header);
            if (append.IsFailure) { throw new InvalidDataException(append.Error!.ToString()); }
            var check = FrameHeaderReader.Check(file, _identity, fileId);
            if (check.IsFailure) { throw new InvalidDataException(check.Error!.ToString()); }
            var boundaryResult = file.GetScanBoundaryAfter(check.Value);
            if (boundaryResult.IsFailure) { throw new InvalidDataException(boundaryResult.Error!.ToString()); }
            var appended = append.Value;
            var checkedTicket = check.Value;
            var boundary = boundaryResult.Value;
            if (appended != checkedTicket || file.TailOffset != FileHeaderCodec.InitializationBoundary ||
                boundary.EndExclusive != FileHeaderCodec.InitializationBoundary) {
                throw new InvalidDataException("Private initialization did not produce exactly the required header.");
            }
            file.DurableFlush();
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref file, ref errors);
        errors.ThrowIfAny();
        // Even a qualified private file is preserved if publish/open fails; the next Open adjudicates actual facts.
        FrameStorePlatform.MoveFileNoOverwrite(privatePath, ActivePath(fileId));
        return OpenQualifiedActive(fileId, out _);
    }

    public void ArchiveClosed(uint fileId) {
        string bucket = Path.Combine(_root, FrameStorePaths.ArchiveDirectoryName, FrameStorePaths.GetBucketName(fileId));
        FrameStorePlatform.CreateDirectory(bucket);
        FrameStorePlatform.MoveFileNoOverwrite(ActivePath(fileId), ArchivePath(fileId));
    }

    public AteliaResult<FrameRead> ReadUnowned(FrameAddress address, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        // Only genuine absence permits the alternative location; all other errors retain their qualification.
        string path = ArchivePath(address.FileId);
        if (!FrameStorePlatform.TryRequireFile(path)) {
            path = ActivePath(address.FileId);
            FrameStorePlatform.RequireFile(path);
        }
        IRbfFile? file = null;
        RbfPooledFrame? frame = null;
        FrameRead? output = null;
        AteliaError? failedResult = null;
        CleanupErrors errors = default;
        bool exceptional = false;
        try {
            file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
            var header = FrameHeaderReader.Check(file, _identity, address.FileId);
            if (header.IsFailure) { failedResult = header.Error; }
            else {
                var result = file.ReadPooledFrame(address.Ticket);
                if (result.IsFailure) { failedResult = result.Error; }
                else {
                    frame = result.Value;
                    output = new FrameRead(address, frame!);
                    frame = null;
                }
            }
        }
        catch (Exception error) { exceptional = true; errors.Add(error); }
        var toClose = file;
        file = null;
        if (toClose is not null) {
            try { toClose.Dispose(); }
            catch (Exception error) { exceptional = true; markOwnedCleanupFault(); errors.Add(error); }
        }
        if (exceptional) {
            var abandonedFrame = frame;
            frame = null;
            if (abandonedFrame is not null) {
                try { abandonedFrame.Dispose(); }
                catch (Exception error) { markOwnedCleanupFault(); errors.Add(error); }
            }
            var abandonedOutput = output;
            output = null;
            if (abandonedOutput is not null) {
                try { abandonedOutput.Dispose(); }
                catch (Exception error) { markOwnedCleanupFault(); errors.Add(error); }
            }
            errors.ThrowIfAny();
        }
        if (failedResult is not null) { return failedResult; }
        return output!;
    }

    private static void CheckInitializedLength(string path) {
        FileStream? stream = null;
        CleanupErrors errors = default;
        try {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < FileHeaderCodec.InitializationBoundary) {
                throw new InvalidDataException("A formal active file has an incomplete required initialization.");
            }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref stream, ref errors);
        errors.ThrowIfAny();
    }

    private string ActivePath(uint fileId) => Path.Combine(_root, FrameStorePaths.GetActiveRelativePath(fileId));
    private string ArchivePath(uint fileId) => Path.Combine(_root, FrameStorePaths.GetArchiveRelativePath(fileId));

    private static InvalidDataException InvalidEntry(string name) => new($"Unexpected FrameStore directory entry '{name}'.");

    private static void EnumerateDirect(string directory, Action<string> visit) {
        // Root/layout or an outer actual enumeration has already established exact component spelling.
        FrameStorePlatform.RequireEnumeratedDirectory(directory);
        IEnumerator<string>? enumerator = null;
        CleanupErrors errors = default;
        try {
            enumerator = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false
            }).GetEnumerator();
            while (enumerator.MoveNext()) { visit(enumerator.Current); }
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref enumerator, ref errors);
        errors.ThrowIfAny();
    }

    private static void CloseOwned<T>(ref T? slot, ref CleanupErrors errors) where T : class, IDisposable {
        var resource = slot;
        slot = null;
        if (resource is null) { return; }
        try { resource.Dispose(); }
        catch (Exception error) { errors.Add(error); }
    }
}
