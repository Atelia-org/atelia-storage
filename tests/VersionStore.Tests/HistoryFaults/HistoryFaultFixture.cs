using System.Globalization;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore.Tests.HistoryFaults;

/// <summary>真实 public RBF/data 加窄资源故障 seam；不提供 VS 目录、no-follow 或实际 owner 锁的平台资格。</summary>
internal sealed class HistoryFaultFixture : IDisposable {
    private readonly string _parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
    internal readonly string Root;
    internal readonly List<string> Operations = [];
    internal readonly StoreIdentity Identity;
    internal readonly DataStore Data;
    internal readonly HistoryFaultFiles Files;
    internal readonly HistoryFaultLock Lock;
    internal readonly VersionStore Store;

    internal HistoryFaultFixture(bool readOnly = false) {
        Root = Path.Combine(_parent, "VersionStore-history-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Data = DataStore.Create(Path.Combine(Root, "data"));
        if (!StoreIdentity.TryRead(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), out Identity)
            || !StoreIdentity.TryRead(Data.StoreId, out StoreIdentity dataIdentity)) {
            throw new InvalidOperationException("Fixture identities must be valid.");
        }
        Files = new HistoryFaultFiles(Root, Operations);
        Lock = new HistoryFaultLock(Operations);
        var hooks = new VersionStoreTestHooks {
            ConfirmDataDurable = () => { Operations.Add("data:confirm"); Data.ConfirmDurable(); }
        };
        Store = new VersionStore(Data, Identity, dataIdentity, 17, readOnly, Files, Lock, hooks);
    }

    internal HistorySeed Seed(uint localId, params byte[][] payloads) => Seed(localId, null, payloads);

    internal HistorySeed SeedFork(uint localId, HistorySeed source, int sourceIndex = 0) {
        var origin = new ForkOrigin(source.RefId, source.Tickets[sourceIndex]);
        return Seed(localId, origin, [source.Payloads[sourceIndex]]);
    }

    private HistorySeed Seed(uint localId, ForkOrigin? origin, byte[][] payloads) {
        if (payloads.Length == 0) { throw new ArgumentException("Seed requires at least one Snapshot.", nameof(payloads)); }
        RefId id = RefId.Create(Identity, localId);
        string path = Path.Combine(Root, localId.ToString("x8", CultureInfo.InvariantCulture) + ".rbf");
        byte[] header = new byte[origin.HasValue ? RefHeaderCodec.WithOriginPayloadSize : RefHeaderCodec.NoOriginPayloadSize];
        if (!RefHeaderCodec.TryWrite(Identity, id, origin, header)) { throw new InvalidOperationException("Invalid seed header."); }
        var tickets = new SizedPtr[payloads.Length];
        using (IRbfFile file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, header).Unwrap();
            Span<byte> metadata = stackalloc byte[RecordMetadataCodec.EncodedSize];
            for (int index = 0; index < payloads.Length; index++) {
                RecordMetadataCodec.TryWrite(100 + index, metadata);
                tickets[index] = file.Append(RefRecordCodec.SnapshotTag, payloads[index], metadata).Unwrap();
            }
            file.DurableFlush();
        }
        Files.Paths.Add(localId, path);
        return new HistorySeed(id, tickets, payloads);
    }

    internal void CorruptPayload(HistorySeed seed, int index) {
        SizedPtr ticket = seed.Tickets[index];
        using var stream = new FileStream(Files.Paths[seed.RefId.LocalId], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Mutate a publicly generated frame; no RBF wire construction or resealing is used.
        long offset = ticket.Offset + ticket.Length / 2;
        stream.Position = offset;
        int original = stream.ReadByte();
        if (original < 0) { throw new InvalidOperationException("Seed frame was truncated."); }
        stream.Position = offset;
        stream.WriteByte((byte)(original ^ 1));
    }

    internal static byte[] Payload(int salt = 0) => Enumerable.Range(0, 256).Select(i => (byte)(i + salt)).ToArray();

    public void Dispose() {
        try { Store.Dispose(); }
        finally {
            Data.Dispose();
            string resolved = Path.GetFullPath(Root);
            string parent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("VersionStore-history-fault-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
}

internal sealed record HistorySeed(RefId RefId, SizedPtr[] Tickets, byte[][] Payloads);

internal sealed class HistoryFaultLock(List<string> operations) : IDisposable {
    internal int DisposeCalls;
    internal Exception? DisposeFailure;

    public void Dispose() {
        DisposeCalls++;
        operations.Add("lock:close");
        if (DisposeFailure is { } error) { throw error; }
    }
}

internal sealed class HistoryFaultFiles(string root, List<string> operations) : IVersionStoreFiles {
    internal readonly Dictionary<uint, string> Paths = [];
    internal readonly List<HistoryFaultRefFile> Opened = [];
    internal Action<HistoryFaultRefFile>? ConfigureOpen;
    internal Action<uint, bool>? BeforeOpen;
    internal Exception? OpenCleanupFailure;
    internal uint? OpenCleanupFailureId;

    public AteliaResult<QualifiedRefTarget> ResolveRef(uint localId, Action markOwnedCleanupFault) {
        operations.Add($"resolve:{localId}");
        return Paths.TryGetValue(localId, out string? path)
            ? new QualifiedRefTarget(localId, path)
            : new VersionStoreRuntimeError("RefNotFound", "The controlled backend has no formal target.");
    }

    public IRbfFile OpenRef(QualifiedRefTarget target, bool readOnly, Action markOwnedCleanupFault) {
        operations.Add($"open:{target.LocalId}:{(readOnly ? "ro" : "rw")}");
        BeforeOpen?.Invoke(target.LocalId, readOnly);
        if (OpenCleanupFailure is { } failure && (OpenCleanupFailureId is null || OpenCleanupFailureId == target.LocalId)) {
            markOwnedCleanupFault();
            throw failure;
        }
        IRbfFile? acquired = readOnly
            ? RbfFile.OpenReadOnlyExisting(target.Path, RbfCacheMode.Off)
            : RbfFile.OpenExisting(target.Path, out _, RbfCacheMode.Off);
        try {
            string logPath = Path.Combine(root, $"reads-{target.LocalId}-{Opened.Count}.csv");
            var tracked = new HistoryFaultRefFile(target.LocalId, acquired, operations, logPath);
            acquired.SetupReadLog(logPath);
            ConfigureOpen?.Invoke(tracked);
            Opened.Add(tracked);
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

    public AteliaResult<IReadOnlyList<uint>> ListRefIds(Action markOwnedCleanupFault) =>
        AteliaResult<IReadOnlyList<uint>>.Success(Array.AsReadOnly(Paths.Keys.ToArray()));

    public AteliaResult<QualifiedRefTarget> PrepareNewRef(uint localId, Action markOwnedCleanupFault) {
        operations.Add("prepare");
        throw new InvalidOperationException("History fault tests do not create through their controlled backend.");
    }

    public IRbfFile CreatePending(Action markOwnedCleanupFault) {
        operations.Add("pending:create");
        throw new InvalidOperationException("History fault tests do not create pending files.");
    }

    public void PublishPending(QualifiedRefTarget target, Action markOwnedCleanupFault) =>
        throw new InvalidOperationException("History fault tests do not publish pending files.");
}

/// <summary>普通 I/O 转发真实 public RBF；Scan 入口和实际 Dispose 完成后的故障均由显式 seam 注入。</summary>
internal sealed class HistoryFaultRefFile(uint localId, IRbfFile inner, List<string> operations, string readLog) : IRbfFile {
    internal uint LocalId => localId;
    internal string ReadLog => readLog;
    internal Exception? ScanFailure;
    internal Exception? DisposeFailure;
    internal Action? AfterDispose;
    internal int DisposeCalls;
    internal int GetBoundaryCalls;
    internal int BoundaryReverseCalls;
    internal int FrameInfoCalls;
    internal readonly List<(string Operation, bool ShowTombstone)> ScanArguments = [];

    public RbfFormat Format => inner.Format;
    public long TailOffset => inner.TailOffset;

    public void Dispose() {
        DisposeCalls++;
        operations.Add($"close:{localId}");
        inner.Dispose();
        AfterDispose?.Invoke();
        if (DisposeFailure is { } failure) { throw failure; }
    }

    internal int CountCompleteReads(SizedPtr ticket) {
        // Read logs are flushed by the real file Dispose. Infos bypass this proxy, so inspect real requests.
        return File.ReadAllLines(readLog).Count(line => {
            if (line.StartsWith('#') || line.StartsWith("seq,", StringComparison.Ordinal)) { return false; }
            string[] fields = line.Split(',');
            return long.Parse(fields[1], CultureInfo.InvariantCulture) == ticket.Offset
                && int.Parse(fields[2], CultureInfo.InvariantCulture) == ticket.Length;
        });
    }

    public RbfForwardSequence ScanForward(bool showTombstone = false) {
        operations.Add($"forward:{localId}");
        ScanArguments.Add(("forward", showTombstone));
        if (ScanFailure is { } failure) { throw failure; }
        return inner.ScanForward(showTombstone);
    }

    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        operations.Add($"reverse:{localId}");
        ScanArguments.Add(("reverse", showTombstone));
        if (ScanFailure is { } failure) { throw failure; }
        return inner.ScanReverse(showTombstone);
    }

    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) {
        GetBoundaryCalls++;
        operations.Add($"boundary:{localId}");
        return inner.GetScanBoundaryAfter(ticket);
    }

    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) {
        BoundaryReverseCalls++;
        operations.Add($"boundary-reverse:{localId}");
        ScanArguments.Add(("boundary-reverse", showTombstone));
        if (ScanFailure is { } failure) { throw failure; }
        return inner.ScanReverse(boundary, showTombstone);
    }

    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
        operations.Add($"append:{localId}");
        return inner.Append(tag, payload, tailMeta);
    }
    public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => inner.ReadPooledFrame(ticket);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) {
        FrameInfoCalls++;
        operations.Add($"info:{localId}");
        return inner.ReadFrameInfo(ticket);
    }
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
    public void DurableFlush() => inner.DurableFlush();
    public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
}
