using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Runtime;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.Runtime;

/// <summary>真实 FrameStore/RBF 内核夹具；backend 不提供 VS 根、门、no-follow 或锁的生产平台资格。</summary>
internal sealed class RuntimeFixture : IDisposable {
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "HeadStore-runtime-" + Guid.NewGuid().ToString("N"));
    internal readonly List<string> Operations = [];
    internal readonly StoreIdentity Identity;
    internal readonly StoreIdentity DataIdentity;
    internal readonly DataStore Data;
    internal readonly TestHeadStoreFiles Files;
    internal readonly RecordingOwnerLock Lock;
    internal readonly HeadStoreTestHooks Hooks = new();
    internal readonly HeadStore Store;

    internal RuntimeFixture(bool readOnly = false, bool readOnlyData = false, uint reservedUpper = 17,
        uint? autoHeadIdHighWatermark = null) {
        Directory.CreateDirectory(Root);
        string dataPath = Path.Combine(Root, "data");
        Data = DataStore.Create(dataPath);
        if (readOnlyData) { Data.Dispose(); Data = DataStore.OpenReadOnly(dataPath); }
        StoreIdentity.TryRead(Data.StoreId, out DataIdentity);
        StoreIdentity.TryRead(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), out Identity);
        Files = new TestHeadStoreFiles(Operations, Root);
        Lock = new RecordingOwnerLock(Operations);
        Hooks.ConfirmDataDurable = () => { Operations.Add("data:confirm"); Data.ConfirmDurable(); };
        Store = new HeadStore(Data, Identity, DataIdentity, reservedUpper, readOnly, Files, Lock, Hooks,
            autoHeadIdHighWatermark);
    }

    internal HeadId Seed(uint localId = 1, byte[]? payload = null, long recordedAt = 123) {
        string path = Path.Combine(Root, localId.ToString("x8") + ".rbf");
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 2);
        Identity.TryWrite(header[4..20]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], localId);
        Span<byte> meta = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(meta, recordedAt);
        using (IRbfFile file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            file.Append(0, header).Unwrap();
            file.Append(1, payload ?? [1], meta).Unwrap();
            file.DurableFlush();
        }
        Files.Paths.Add(localId, path);
        return HeadId.Create(Identity, localId);
    }

    public void Dispose() {
        try { Store.Dispose(); }
        finally {
            Data.Dispose();
            string resolved = Path.GetFullPath(Root);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("HeadStore-runtime-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Fixture cleanup escaped its generated TEMP directory.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
}

internal sealed class RecordingOwnerLock(List<string> operations) : IDisposable {
    internal int DisposeCalls;
    internal Exception? DisposeFailure;
    public void Dispose() {
        DisposeCalls++;
        operations.Add("lock:close");
        if (DisposeFailure is { } error) { throw error; }
    }
}

internal sealed class TestHeadStoreFiles(List<string> operations, string? root = null) : IHeadStoreFiles {
    internal readonly Dictionary<uint, string> Paths = [];
    internal readonly Dictionary<uint, TrackedHeadFile> Opened = [];
    internal Exception? ResolveFailure;
    internal Exception? ResolveCleanupFailure;
    internal Exception? OpenFailure;
    internal Exception? OpenCleanupFailure;
    internal Exception? ListFailure;
    internal Exception? ListCleanupFailure;
    internal Exception? PrepareFailure;
    internal Exception? PrepareCleanupFailure;
    internal Exception? PendingCreateFailure;
    internal Exception? PendingCreateCleanupFailure;
    internal Exception? RenameFailure;
    internal bool RenameThenThrow;
    internal Action<TrackedHeadFile>? ConfigurePending;
    internal readonly List<TrackedHeadFile> PendingFiles = [];
    internal long? NextTailOffset;
    internal int ResolveCalls;
    internal int OpenCalls;
    internal int ListCalls;
    internal int PrepareCalls;
    internal int PendingCreateCalls;
    internal int RenameCalls;
    internal string PendingPath => Path.Combine(root ?? throw new InvalidOperationException("No fixture creation root."), "pending.rbf");

    public AteliaResult<IReadOnlyList<uint>> ListHeadIds(Action markOwnedCleanupFault) {
        ListCalls++;
        operations.Add("list");
        if (ListCleanupFailure is { } cleanupFailure) { markOwnedCleanupFault(); throw cleanupFailure; }
        if (ListFailure is { } failure) { throw failure; }
        return AteliaResult<IReadOnlyList<uint>>.Success(Array.AsReadOnly(Paths.Keys.ToArray()));
    }

    public AteliaError? VisitHeadTargets(Func<QualifiedHeadTarget, AteliaError?> visit, Action checkState,
        Action markOwnedCleanupFault) {
        ListCalls++;
        operations.Add("list");
        checkState();
        if (ListCleanupFailure is { } cleanupFailure) { markOwnedCleanupFault(); throw cleanupFailure; }
        if (ListFailure is { } failure) { throw failure; }
        foreach (var pair in Paths) {
            checkState();
            AteliaError? error = visit(new QualifiedHeadTarget(pair.Key, pair.Value));
            checkState();
            if (error is not null) { return error; }
        }
        checkState();
        return null;
    }

    public AteliaResult<QualifiedHeadTarget> PrepareNewHead(uint localId, Action markOwnedCleanupFault) {
        PrepareCalls++;
        operations.Add($"prepare:{localId}");
        if (PrepareCleanupFailure is { } cleanupFailure) { markOwnedCleanupFault(); throw cleanupFailure; }
        if (PrepareFailure is { } failure) { throw failure; }
        if (Paths.ContainsKey(localId)) {
            return new HeadStoreRuntimeError("HeadAlreadyExists", "The formal fixture target is occupied.");
        }
        return new QualifiedHeadTarget(localId,
            Path.Combine(root ?? throw new InvalidOperationException("No fixture creation root."), localId.ToString("x8") + ".rbf"));
    }

    public IRbfFile CreatePending(Action markOwnedCleanupFault) {
        PendingCreateCalls++;
        operations.Add("pending:create");
        if (PendingCreateCleanupFailure is { } cleanupFailure) { markOwnedCleanupFault(); throw cleanupFailure; }
        if (PendingCreateFailure is { } failure) { throw failure; }
        IRbfFile? pending = RbfFile.CreateNew(PendingPath, RbfCacheMode.Off);
        try {
            var tracked = new TrackedHeadFile(0, pending, operations);
            ConfigurePending?.Invoke(tracked);
            PendingFiles.Add(tracked);
            pending = null;
            return tracked;
        }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            IRbfFile? toClose = pending;
            pending = null;
            try { toClose?.Dispose(); }
            catch (Exception cleanupError) { markOwnedCleanupFault(); errors.Add(cleanupError); }
            errors.ThrowIfAny();
            throw;
        }
    }

    public void PublishPending(QualifiedHeadTarget target, Action markOwnedCleanupFault) {
        RenameCalls++;
        operations.Add($"rename:{target.LocalId}");
        if (RenameFailure is { } failure && !RenameThenThrow) { throw failure; }
        // Fixture bookkeeping is prepared before the real move. Production uses its native rename seam.
        Paths.Add(target.LocalId, target.Path);
        try { File.Move(PendingPath, target.Path, overwrite: false); }
        catch { Paths.Remove(target.LocalId); throw; }
        if (RenameFailure is { } laterFailure) { throw laterFailure; }
    }

    public AteliaResult<QualifiedHeadTarget> ResolveHead(uint localId, Action markOwnedCleanupFault) {
        ResolveCalls++;
        operations.Add($"resolve:{localId}");
        if (ResolveCleanupFailure is { } cleanupFailure) {
            markOwnedCleanupFault();
            throw cleanupFailure;
        }
        if (ResolveFailure is { } error) { throw error; }
        if (!Paths.TryGetValue(localId, out string? path)) {
            return new HeadStoreRuntimeError("HeadNotFound", "The test fixture has no formal target.");
        }
        // This generated fixture map is only an internal test target; it is not real VS platform admission.
        return new QualifiedHeadTarget(localId, path);
    }

    public IRbfFile OpenHead(QualifiedHeadTarget target, bool readOnly, Action markOwnedCleanupFault) {
        OpenCalls++;
        operations.Add($"open:{target.LocalId}:{(readOnly ? "ro" : "rw")}");
        if (OpenCleanupFailure is { } cleanupFailure) {
            markOwnedCleanupFault();
            throw cleanupFailure;
        }
        if (OpenFailure is { } error) { throw error; }
        IRbfFile? pending = readOnly
            ? RbfFile.OpenReadOnlyExisting(target.Path, RbfCacheMode.Off)
            : RbfFile.OpenExisting(target.Path, out _, RbfCacheMode.Off);
        try {
            var tracked = new TrackedHeadFile(target.LocalId, pending, operations) { TailOffsetOverride = NextTailOffset };
            Opened.Add(target.LocalId, tracked);
            pending = null;
            return tracked;
        }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            IRbfFile? toClose = pending;
            pending = null;
            try { toClose?.Dispose(); }
            catch (Exception cleanupError) { markOwnedCleanupFault(); errors.Add(cleanupError); }
            errors.ThrowIfAny();
            throw;
        }
    }
}

/// <summary>正常 I/O 全部转发真实 public RBF；故障仅由窄的内部 seam 注入。</summary>
internal sealed class TrackedHeadFile(uint localId, IRbfFile inner, List<string> operations) : IRbfFile {
    internal readonly IRbfFile Inner = inner;
    internal long? TailOffsetOverride;
    internal AteliaError? AppendError;
    internal Exception? AppendFailure;
    internal int? AppendFailureCall;
    internal int? AppendErrorCall;
    internal bool AppendThenThrow;
    internal Exception? FlushFailure;
    internal bool FlushThenThrow;
    internal Exception? DisposeFailure;
    internal Exception? ScanFailure;
    internal int DisposeCalls;
    internal int AppendCalls;
    internal int FlushCalls;
    internal int ReverseScanCalls;
    internal readonly List<SizedPtr> PooledReads = [];
    internal byte[]? AppendedPayload;
    internal byte[]? AppendedMeta;

    public RbfFormat Format => Inner.Format;
    public long TailOffset => TailOffsetOverride ?? Inner.TailOffset;

    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
        AppendCalls++;
        operations.Add($"append:{localId}");
        AppendedPayload = payload.ToArray();
        AppendedMeta = tailMeta.ToArray();
        bool rejectThisCall = AppendErrorCall is null || AppendErrorCall == AppendCalls;
        bool failThisCall = AppendFailureCall is null || AppendFailureCall == AppendCalls;
        if (AppendError is { } rejected && rejectThisCall) { return rejected; }
        if (AppendFailure is { } error && failThisCall && !AppendThenThrow) { throw error; }
        var result = Inner.Append(tag, payload, tailMeta);
        if (AppendFailure is { } laterError && failThisCall) { throw laterError; }
        return result;
    }

    public void DurableFlush() {
        FlushCalls++;
        operations.Add($"flush:{localId}");
        if (FlushFailure is { } error && !FlushThenThrow) { throw error; }
        Inner.DurableFlush();
        if (FlushFailure is { } laterError) { throw laterError; }
    }

    public void Dispose() {
        DisposeCalls++;
        operations.Add($"close:{localId}");
        Inner.Dispose();
        if (DisposeFailure is { } error) { throw error; }
    }

    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) {
        operations.Add($"read:{localId}");
        PooledReads.Add(ptr);
        return Inner.ReadPooledFrame(ptr);
    }
    public RbfFrameBuilder BeginAppend() => Inner.BeginAppend();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => Inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => Inner.ReadFrame(ptr, buffer);
    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        ReverseScanCalls++;
        if (ScanFailure is { } error) { throw error; }
        return Inner.ScanReverse(showTombstone);
    }
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) {
        ReverseScanCalls++;
        if (ScanFailure is { } error) { throw error; }
        return Inner.ScanReverse(boundary, showTombstone);
    }
    public RbfForwardSequence ScanForward(bool showTombstone = false) {
        if (ScanFailure is { } error) { throw error; }
        return Inner.ScanForward(showTombstone);
    }
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => Inner.GetScanBoundaryAfter(ticket);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => Inner.ScanForward(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => Inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => Inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => Inner.ReadFrameInfo(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => Inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => Inner.ReadPooledTailMeta(ticket);
    public void SetupReadLog(string? logPath) => Inner.SetupReadLog(logPath);
}
