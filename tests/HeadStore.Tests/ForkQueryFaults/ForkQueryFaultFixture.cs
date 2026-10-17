using System.Collections;
using System.Globalization;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Platform;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.ForkQueryFaults;

/// <summary>公开创建的真实根/data/owner 锁与生产目录 backend；只包枚举器和 RBF 访问/关闭故障。</summary>
internal sealed class ForkQueryFaultFixture : IDisposable {
    private readonly string _parent = OperatingSystem.IsWindows() && Directory.Exists(@"W:\")
        ? @"W:\" : Path.GetTempPath();
    internal readonly string BasePath;
    internal readonly string Root;
    internal readonly List<string> Events = [];
    internal readonly StoreIdentity Identity;
    internal readonly DataStore Data;
    internal readonly ForkQueryOperations Operations;
    internal readonly ForkQueryFiles Files;
    internal readonly ForkQueryLock Lock;
    internal readonly HeadStore Store;

    internal ForkQueryFaultFixture(bool readOnly = false) {
        BasePath = Path.Combine(_parent, "HeadStore-fork-query-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BasePath);
        Root = Path.Combine(BasePath, "version");
        Data = DataStore.Create(Path.Combine(BasePath, "data"));
        using (var initial = HeadStore.Create(Root, Data, 17)) {
            if (!StoreIdentity.TryRead(initial.HeadStoreId, out Identity)) {
                throw new InvalidOperationException("Invalid public store identity.");
            }
        }
        StoreIdentity.TryRead(Data.StoreId, out StoreIdentity dataIdentity);
        Operations = new ForkQueryOperations(Events);
        Files = new ForkQueryFiles(new DirectoryHeadStoreFiles(Root, Operations), BasePath, Events);
        Lock = new ForkQueryLock(HeadStorePlatform.AcquireOwnerLock(Root, create: false, readOnly), Events);
        Store = new HeadStore(Data, Identity, dataIdentity, 17, readOnly, Files, Lock,
            autoHeadIdHighWatermark: 17);
    }

    internal string HeadPath(uint localId) => Path.Combine(Root, HeadStorePaths.GetHeadRelativePath(localId));

    internal ForkQuerySeed Seed(uint localId, ForkOrigin? origin = null, params byte[][] payloads) {
        if (payloads.Length == 0) { payloads = [Payload()]; }
        HeadId id = HeadId.Create(Identity, localId);
        byte[] header = new byte[origin.HasValue ? HeadHeaderCodec.WithOriginPayloadSize : HeadHeaderCodec.NoOriginPayloadSize];
        if (!HeadHeaderCodec.TryWrite(Identity, id, origin, header)) { throw new InvalidOperationException("Invalid seed header."); }
        var tickets = new SizedPtr[payloads.Length];
        SizedPtr headerTicket;
        using (IRbfFile file = RbfFile.CreateNew(HeadPath(localId), RbfCacheMode.Off)) {
            headerTicket = file.Append(HeadRecordCodec.HeaderTag, header).Unwrap();
            Span<byte> meta = stackalloc byte[RecordMetadataCodec.EncodedSize];
            for (int i = 0; i < payloads.Length; i++) {
                RecordMetadataCodec.TryWrite(123 + i, meta);
                tickets[i] = file.Append(HeadRecordCodec.SingleEntryTag, payloads[i], meta).Unwrap();
            }
            file.DurableFlush();
        }
        return new ForkQuerySeed(id, headerTicket, tickets, payloads);
    }

    internal void Order(params ForkQuerySeed[] seeds) =>
        Operations.EntryPaths = seeds.Select(seed => HeadPath(seed.HeadId.GetLocalId(Identity))).ToArray();

    internal void CorruptPayload(ForkQuerySeed seed, int index) {
        SizedPtr ticket = seed.Tickets[index];
        // Only use this before the corresponding writer/reader is acquired.
        using var stream = new FileStream(HeadPath(seed.HeadId.GetLocalId(Identity)), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long offset = ticket.Offset + ticket.Length / 2;
        stream.Position = offset;
        int value = stream.ReadByte();
        if (value < 0) { throw new InvalidOperationException("Seed frame was truncated."); }
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 1));
    }

    internal static byte[] Payload(int salt = 0) => Enumerable.Range(0, 256).Select(i => (byte)(i + salt)).ToArray();

    public void Dispose() {
        try { Store.Dispose(); }
        finally {
            Data.Dispose();
            string resolved = Path.GetFullPath(BasePath);
            string expected = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("HeadStore-fork-query-fault-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
}

internal sealed record ForkQuerySeed(HeadId HeadId, SizedPtr HeaderTicket, SizedPtr[] Tickets, byte[][] Payloads);

internal sealed class ForkQueryLock(IDisposable inner, List<string> events) : IDisposable {
    internal int DisposeCalls;
    internal Exception? DisposeFailure;

    public void Dispose() {
        DisposeCalls++;
        events.Add("lock:close");
        inner.Dispose();
        if (DisposeFailure is { } failure) { throw failure; }
    }
}

internal sealed class ForkQueryOperations(List<string> events) : HeadStoreFactoryOperations {
    internal string[]? EntryPaths;
    internal Action<ForkQueryEnumerator>? ConfigureEnumerator;
    internal readonly List<ForkQueryEnumerator> Enumerators = [];

    internal override IEnumerator<string> EnumerateDirectory(string directory) {
        events.Add("directory:enumerate");
        IEnumerator<string> inner = EntryPaths is { } paths
            ? ((IEnumerable<string>)paths).GetEnumerator()
            : base.EnumerateDirectory(directory);
        var tracked = new ForkQueryEnumerator(inner, events);
        ConfigureEnumerator?.Invoke(tracked);
        Enumerators.Add(tracked);
        return tracked;
    }
}

internal sealed class ForkQueryEnumerator(IEnumerator<string> inner, List<string> events) : IEnumerator<string> {
    internal int MoveNextCalls;
    internal int CurrentCalls;
    internal int DisposeCalls;
    internal Exception? MoveNextFailure;
    internal int FailMoveNextAt = 1;
    internal Exception? CurrentFailure;
    internal Exception? DisposeFailure;
    internal Action<ForkQueryEnumerator, bool>? AfterMoveNext;
    internal Action? AfterCurrent;
    internal Action? BeforeDispose;
    internal Action? AfterDispose;

    public bool MoveNext() {
        MoveNextCalls++;
        events.Add("directory:move");
        if (MoveNextCalls == FailMoveNextAt && MoveNextFailure is { } failure) { throw failure; }
        bool result = inner.MoveNext();
        AfterMoveNext?.Invoke(this, result);
        return result;
    }

    public string Current {
        get {
            CurrentCalls++;
            events.Add("directory:current");
            if (CurrentFailure is { } failure) { throw failure; }
            string path = inner.Current;
            AfterCurrent?.Invoke();
            return path;
        }
    }

    object IEnumerator.Current => Current;
    public void Reset() => throw new NotSupportedException();

    public void Dispose() {
        DisposeCalls++;
        events.Add("directory:close");
        BeforeDispose?.Invoke();
        inner.Dispose();
        AfterDispose?.Invoke();
        if (DisposeFailure is { } failure) { throw failure; }
    }
}

internal sealed class ForkQueryFiles(DirectoryHeadStoreFiles inner, string basePath, List<string> events) : IHeadStoreFiles {
    internal readonly List<ForkQueryHeadFile> Opened = [];
    internal Action<ForkQueryHeadFile>? ConfigureOpen;
    internal Action<uint, bool>? AfterOpen;
    internal Exception? OpenFailure;
    internal int VisitCalls;
    internal int ListCalls;
    internal int ActiveFiles;
    internal int MaximumActiveFiles;

    public AteliaResult<QualifiedHeadTarget> ResolveHead(uint localId, Action markOwnedCleanupFault) =>
        inner.ResolveHead(localId, markOwnedCleanupFault);

    public IRbfFile OpenHead(QualifiedHeadTarget target, bool readOnly, Action markOwnedCleanupFault) {
        events.Add($"open:{target.LocalId}:{(readOnly ? "ro" : "rw")}");
        if (OpenFailure is { } failure) { throw failure; }
        IRbfFile? acquired = inner.OpenHead(target, readOnly, markOwnedCleanupFault);
        try {
            string log = Path.Combine(basePath, $"fork-query-reads-{target.LocalId}-{Opened.Count}.csv");
            acquired.SetupReadLog(log);
            var tracked = new ForkQueryHeadFile(target.LocalId, acquired, log, events, () => ActiveFiles--);
            ConfigureOpen?.Invoke(tracked);
            // Keep the raw acquired resource owned locally until every fallible handoff hook has completed.
            AfterOpen?.Invoke(target.LocalId, readOnly);
            Opened.Add(tracked);
            ActiveFiles++;
            MaximumActiveFiles = Math.Max(MaximumActiveFiles, ActiveFiles);
            acquired = null;
            return tracked;
        }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            IRbfFile? toClose = acquired;
            acquired = null;
            try { toClose?.Dispose(); }
            catch (Exception cleanup) { markOwnedCleanupFault(); errors.Add(cleanup); }
            errors.ThrowIfAny();
            throw;
        }
    }

    public AteliaResult<IReadOnlyList<uint>> ListHeadIds(Action markOwnedCleanupFault) {
        ListCalls++;
        return inner.ListHeadIds(markOwnedCleanupFault);
    }

    public AteliaError? VisitHeadTargets(Func<QualifiedHeadTarget, AteliaError?> visit,
        Action checkState, Action markOwnedCleanupFault) {
        VisitCalls++;
        return inner.VisitHeadTargets(visit, checkState, markOwnedCleanupFault);
    }

    public AteliaResult<QualifiedHeadTarget> PrepareNewHead(uint localId, Action markOwnedCleanupFault) =>
        inner.PrepareNewHead(localId, markOwnedCleanupFault);
    public IRbfFile CreatePending(Action markOwnedCleanupFault) => inner.CreatePending(markOwnedCleanupFault);
    public void PublishPending(QualifiedHeadTarget target, Action markOwnedCleanupFault) => inner.PublishPending(target, markOwnedCleanupFault);
}

/// <summary>RbfFrameInfo 完整读取绕过此代理；内容范围断言因此检查真实 RBF 的读日志。</summary>
internal sealed class ForkQueryHeadFile(uint localId, IRbfFile inner, string readLog, List<string> events,
    Action closed) : IRbfFile {
    internal uint LocalId => localId;
    internal int DisposeCalls;
    internal int ReverseCalls;
    internal int BoundaryCalls;
    internal int FrameInfoCalls;
    internal Exception? ScanFailure;
    internal Exception? DisposeFailure;
    internal Action<string>? AfterAccess;
    internal Action? AfterDispose;

    public RbfFormat Format {
        get { RbfFormat format = inner.Format; AfterAccess?.Invoke("format"); return format; }
    }
    public long TailOffset => inner.TailOffset;

    internal int CountReadRequests() => File.Exists(readLog)
        ? File.ReadAllLines(readLog).Count(line => !line.StartsWith('#') && !line.StartsWith("seq,", StringComparison.Ordinal))
        : 0; // Real RBF creates the log lazily on the first request.

    internal int CountCompleteReads(SizedPtr ticket) => !File.Exists(readLog) ? 0 : File.ReadAllLines(readLog).Count(line => {
        if (line.StartsWith('#') || line.StartsWith("seq,", StringComparison.Ordinal)) { return false; }
        string[] fields = line.Split(',');
        return long.Parse(fields[1], CultureInfo.InvariantCulture) == ticket.Offset
            && int.Parse(fields[2], CultureInfo.InvariantCulture) == ticket.Length;
    });

    public RbfForwardSequence ScanForward(bool showTombstone = false) {
        events.Add($"header-scan:{localId}");
        if (ScanFailure is { } failure) { throw failure; }
        var sequence = inner.ScanForward(showTombstone);
        AfterAccess?.Invoke("forward");
        return sequence;
    }
    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        ReverseCalls++;
        return inner.ScanReverse(showTombstone);
    }
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) {
        long offset = inner.GetPhysicalOffsetImmediatelyAfter(ticket);
        AfterAccess?.Invoke("successor");
        return offset;
    }
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) {
        FrameInfoCalls++;
        return inner.ReadFrameInfo(ticket);
    }
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) {
        BoundaryCalls++;
        return inner.GetScanBoundaryAfter(ticket);
    }
    public void Dispose() {
        DisposeCalls++;
        events.Add($"ref:close:{localId}");
        try { inner.Dispose(); }
        finally { closed(); }
        AfterDispose?.Invoke();
        if (DisposeFailure is { } failure) { throw failure; }
    }
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);
    public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => inner.ReadPooledFrame(ticket);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanReverse(boundary, showTombstone);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
    public void DurableFlush() => inner.DurableFlush();
    public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
}
