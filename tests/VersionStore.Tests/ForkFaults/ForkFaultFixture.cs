using System.Globalization;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Tests.Runtime;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore.Tests.ForkFaults;

/// <summary>真实 RBF/data 与现有创建故障 seam；不替代公开 VS 根、原生 rename 或 owner 锁验收。</summary>
internal sealed class ForkFaultFixture : IDisposable {
    private readonly string _parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
    internal readonly string Root;
    internal readonly List<string> Operations = [];
    internal readonly StoreIdentity Identity;
    internal readonly DataStore Data;
    internal readonly TestVersionStoreFiles Backend;
    internal readonly ForkFaultFiles Files;
    internal readonly RecordingOwnerLock Lock;
    internal readonly VersionStoreTestHooks Hooks = new();
    internal readonly VersionStore Store;

    internal ForkFaultFixture(uint reservedUpper = 17, uint? highWatermark = null, bool readOnly = false) {
        Root = Path.Combine(_parent, "VersionStore-fork-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Data = DataStore.Create(Path.Combine(Root, "data"));
        if (!StoreIdentity.TryRead(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), out Identity)
            || !StoreIdentity.TryRead(Data.StoreId, out StoreIdentity dataIdentity)) {
            throw new InvalidOperationException("Fixture identities must be valid.");
        }
        Backend = new TestVersionStoreFiles(Operations, Root);
        Files = new ForkFaultFiles(Backend, Root, Operations);
        Lock = new RecordingOwnerLock(Operations);
        Hooks.ConfirmDataDurable = () => { Operations.Add("data:confirm"); Data.ConfirmDurable(); };
        Store = new VersionStore(Data, Identity, dataIdentity, reservedUpper, readOnly, Files, Lock, Hooks, highWatermark);
    }

    internal RefId Seed(uint localId = 1, byte[]? payload = null, long recordedAt = 123) {
        RefId id = RefId.Create(Identity, localId);
        string path = Path.Combine(Root, localId.ToString("x8", CultureInfo.InvariantCulture) + ".rbf");
        Span<byte> header = stackalloc byte[RefHeaderCodec.NoOriginPayloadSize];
        if (!RefHeaderCodec.TryWrite(Identity, id, null, header)) { throw new InvalidOperationException("Invalid seed header."); }
        Span<byte> metadata = stackalloc byte[RecordMetadataCodec.EncodedSize];
        RecordMetadataCodec.TryWrite(recordedAt, metadata);
        using (IRbfFile file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, header).Unwrap();
            file.Append(RefRecordCodec.SnapshotTag, payload ?? [1], metadata).Unwrap();
            file.DurableFlush();
        }
        Backend.Paths.Add(localId, path);
        return id;
    }

    public void Dispose() {
        try { Store.Dispose(); }
        finally {
            Data.Dispose();
            string resolved = Path.GetFullPath(Root);
            string parent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("VersionStore-fork-fault-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
}

internal sealed class ForkFaultFiles(TestVersionStoreFiles inner, string root, List<string> operations) : IVersionStoreFiles {
    internal readonly Dictionary<uint, ForkSourceFile> Sources = [];

    public AteliaResult<QualifiedRefTarget> ResolveRef(uint localId, Action markOwnedCleanupFault) =>
        inner.ResolveRef(localId, markOwnedCleanupFault);

    public IRbfFile OpenRef(QualifiedRefTarget target, bool readOnly, Action markOwnedCleanupFault) {
        IRbfFile acquired = inner.OpenRef(target, readOnly, markOwnedCleanupFault);
        var tracked = new ForkSourceFile(target.LocalId, acquired, root, operations);
        Sources.Add(target.LocalId, tracked);
        tracked.ResetReadLog();
        return tracked;
    }

    public AteliaResult<IReadOnlyList<uint>> ListRefIds(Action markOwnedCleanupFault) => inner.ListRefIds(markOwnedCleanupFault);
    public AteliaError? VisitRefTargets(Func<QualifiedRefTarget, AteliaError?> visit, Action checkState,
        Action markOwnedCleanupFault) => inner.VisitRefTargets(visit, checkState, markOwnedCleanupFault);
    public AteliaResult<QualifiedRefTarget> PrepareNewRef(uint localId, Action markOwnedCleanupFault) => inner.PrepareNewRef(localId, markOwnedCleanupFault);
    public IRbfFile CreatePending(Action markOwnedCleanupFault) => inner.CreatePending(markOwnedCleanupFault);
    public void PublishPending(QualifiedRefTarget target, Action markOwnedCleanupFault) => inner.PublishPending(target, markOwnedCleanupFault);
}

/// <summary>完整读取及 Fence helper 均转发真实 public RBF；日志包括绕过代理的 FrameInfo 读取。</summary>
internal sealed class ForkSourceFile(uint localId, IRbfFile inner, string root, List<string> operations) : IRbfFile {
    internal readonly List<SizedPtr> BoundaryTickets = [];
    internal AteliaError? BoundaryError;
    internal Exception? FrameInfoFailure;
    internal int ReverseCalls;
    internal int BoundaryReverseCalls;
    private int _logGeneration;
    private string _readLog = "";

    public RbfFormat Format => inner.Format;
    public long TailOffset => inner.TailOffset;

    internal void ResetReadLog() {
        _readLog = Path.Combine(root, $"fork-reads-{localId}-{_logGeneration++}.csv");
        inner.SetupReadLog(_readLog);
    }

    internal int CountCompleteReads(SizedPtr ticket) => File.ReadAllLines(_readLog).Count(line => {
        if (line.StartsWith('#') || line.StartsWith("seq,", StringComparison.Ordinal)) { return false; }
        string[] fields = line.Split(',');
        return long.Parse(fields[1], CultureInfo.InvariantCulture) == ticket.Offset
            && int.Parse(fields[2], CultureInfo.InvariantCulture) == ticket.Length;
    });

    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) {
        BoundaryTickets.Add(ticket);
        operations.Add($"boundary:{localId}");
        var result = inner.GetScanBoundaryAfter(ticket);
        return BoundaryError is { } error ? error : result;
    }

    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) {
        operations.Add($"info:{localId}");
        if (FrameInfoFailure is { } error) { throw error; }
        return inner.ReadFrameInfo(ticket);
    }

    public RbfForwardSequence ScanForward(bool showTombstone = false) {
        operations.Add($"forward:{localId}");
        return inner.ScanForward(showTombstone);
    }

    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        ReverseCalls++;
        operations.Add($"reverse:{localId}");
        return inner.ScanReverse(showTombstone);
    }

    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) {
        BoundaryReverseCalls++;
        return inner.ScanReverse(boundary, showTombstone);
    }

    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);
    public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => inner.ReadPooledFrame(ticket);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
    public void DurableFlush() => inner.DurableFlush();
    public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
    public void Dispose() => inner.Dispose();
}
