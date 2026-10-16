using System.Collections;
using System.Globalization;
using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Platform;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.ForkQueryFaults;
using Atelia.HeadStore.Tests.Storage;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.AuditFaults;

/// <summary>公开创建的真实根/data/锁、生产目录 backend 与 RBF reader；故障限于窄观察接缝。</summary>
/// <remarks>受控 read/close/枚举异常及字节镜像不是 OS 设备故障、进程终止或断电证据。</remarks>
internal sealed class AuditFaultFixture : IDisposable {
    internal const string TagName = "tag-48";
    internal const string SameBucketName = "tag-65";
    internal const string OtherBucketName = "Release";
    internal readonly StorageFixture Storage = new();
    internal readonly List<string> Events = [];
    internal readonly StoreIdentity Identity;
    internal readonly AuditOperations Operations;
    internal readonly AuditFiles Files;
    internal readonly ForkQueryLock Lock;
    internal readonly HeadStore Store;
    internal DataStore Data => Storage.Data;

    internal AuditFaultFixture(bool readOnly = true) {
        byte[] identity = Storage.Create();
        if (!StoreIdentity.TryRead(identity, out Identity)
            || !StoreIdentity.TryRead(Data.StoreId, out StoreIdentity dataIdentity)) {
            throw new InvalidOperationException("Invalid fixture identity.");
        }
        Operations = new AuditOperations(Events);
        Files = new AuditFiles(new DirectoryHeadStoreFiles(Storage.Root, Operations), Storage.BasePath, Events);
        Lock = new ForkQueryLock(HeadStorePlatform.AcquireOwnerLock(Storage.Root, create: false, readOnly), Events);
        var hooks = new HeadStoreTestHooks {
            ConfirmDataDurable = () => { Events.Add("data:confirm"); throw new InvalidOperationException("Audit confirmed data."); },
            GetRecordedAtUnixMs = () => { Events.Add("clock"); throw new InvalidOperationException("Audit requested publication time."); }
        };
        Store = new HeadStore(Data, Identity, dataIdentity, 17, readOnly, Files, Lock, hooks,
            autoHeadIdHighWatermark: 17, tagFiles: Files);
    }

    internal AuditHeadSeed SeedHead(uint localId, ForkOrigin? origin = null, params byte[][] payloads) {
        if (payloads.Length == 0) { payloads = [OpaquePayload()]; }
        HeadId id = HeadId.Create(Identity, localId);
        byte[] header = new byte[origin.HasValue ? HeadHeaderCodec.WithOriginPayloadSize : HeadHeaderCodec.NoOriginPayloadSize];
        if (!HeadHeaderCodec.TryWrite(Identity, id, origin, header)) { throw new InvalidOperationException("Invalid seed header."); }
        var tickets = new SizedPtr[payloads.Length];
        using IRbfFile file = RbfFile.CreateNew(Storage.HeadPath(localId), RbfCacheMode.Off);
        SizedPtr headerTicket = file.Append(HeadRecordCodec.HeaderTag, header).Unwrap();
        Span<byte> meta = stackalloc byte[RecordMetadataCodec.EncodedSize];
        for (int index = 0; index < payloads.Length; index++) {
            RecordMetadataCodec.TryWrite(123 + index, meta);
            tickets[index] = file.Append(HeadRecordCodec.SingleEntryTag, payloads[index], meta).Unwrap();
        }
        file.DurableFlush();
        return new AuditHeadSeed(id, headerTicket, tickets, payloads);
    }

    internal AuditTagSeed SeedTagBucket(string name = TagName, params TagPublicRecord[] records) {
        Directory.CreateDirectory(Path.Combine(Storage.Root, "tags"));
        uint bucketId = TagPublicFixture.Bucket(name);
        var header = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        Identity.TryWrite(header.AsSpan(4, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), bucketId);
        using IRbfFile file = RbfFile.CreateNew(BucketPath(name), RbfCacheMode.Off);
        SizedPtr headerTicket = file.Append(0, header).Unwrap();
        var tickets = new SizedPtr[records.Length];
        for (int index = 0; index < records.Length; index++) {
            TagPublicRecord record = records[index];
            tickets[index] = file.Append(record.Tag, record.Root,
                record.Metadata ?? TagPublicFixture.Metadata(record.Name)).Unwrap();
        }
        file.DurableFlush();
        return new AuditTagSeed(bucketId, headerTicket, tickets, records);
    }

    internal string BucketPath(string name) => Path.Combine(Storage.Root, "tags", $"{TagPublicFixture.Bucket(name):x8}.rbf");
    internal void OrderHeads(params AuditHeadSeed[] seeds) =>
        Operations.HeadPaths = seeds.Select(seed => Storage.HeadPath(seed.HeadId.LocalId)).ToArray();
    internal void OrderTags(params string[] names) => Operations.TagPaths = names.Select(BucketPath).ToArray();

    internal void CorruptHeadPayload(AuditHeadSeed seed, int index) => FlipClosedByte(
        Storage.HeadPath(seed.HeadId.LocalId), seed.Tickets[index].Offset + 4);

    internal static void FlipClosedByte(string path, long offset) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = offset;
        int original = file.ReadByte();
        if (original < 0) { throw new InvalidOperationException("Missing seed byte."); }
        file.Position--;
        file.WriteByte((byte)(original ^ 1));
    }

    internal static byte[] OpaquePayload(int salt = 0) =>
        Enumerable.Range(0, 256).Select(index => (byte)(index + salt)).ToArray();

    internal void AssertNoWrites() {
        Assert.Equal(0, Files.MutationCalls);
        Assert.Equal(0, Operations.WritableTagOpens);
        Assert.All(Files.Opened, file => {
            Assert.True(file.ReadOnly);
            Assert.Equal(0, file.MutationCalls);
        });
        Assert.DoesNotContain("data:confirm", Events);
        Assert.DoesNotContain("clock", Events);
    }

    internal void AssertFaultedAndCleaned() {
        Assert.Throws<InvalidOperationException>(() => Store.Audit());
        Assert.All(Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(0, Files.ActiveFiles);
        Assert.Equal(1, Lock.DisposeCalls);
        Assert.Equal("lock:close", Events[^1]);
        Store.Dispose();
        Store.Dispose();
        Assert.All(Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(1, Lock.DisposeCalls);
        Data.EnsureUsable();
    }

    internal string[] Closes() => Events.Where(entry => entry.Contains("close", StringComparison.Ordinal)).ToArray();
    internal static Exception[] Flatten(Exception error) {
        var flattened = new List<Exception>();
        void Visit(Exception current) {
            if (current is AggregateException aggregate) {
                foreach (Exception inner in aggregate.InnerExceptions) { Visit(inner); }
            }
            else { flattened.Add(current); }
        }
        // BCL Flatten traverses nested aggregates breadth first; preserve observed primary-first tree order.
        Visit(error);
        return flattened.ToArray();
    }

    public void Dispose() {
        try { Store.Dispose(); }
        finally { Storage.Dispose(); }
    }
}

internal sealed record AuditHeadSeed(HeadId HeadId, SizedPtr Header, SizedPtr[] Tickets, byte[][] Payloads);
internal sealed record AuditTagSeed(uint BucketId, SizedPtr Header, SizedPtr[] Tickets, TagPublicRecord[] Records);
internal sealed record AuditCursor(bool IsTag, ForkQueryEnumerator Cursor);
internal readonly record struct AuditReadRequest(long Offset, int Length, bool Raw);

internal sealed class AuditOperations(List<string> events) : HeadStoreFactoryOperations {
    internal string[]? HeadPaths;
    internal string[]? TagPaths;
    internal Action<bool, ForkQueryEnumerator>? ConfigureCursor;
    internal readonly List<AuditCursor> Cursors = [];
    internal int WritableTagOpens;

    internal override IEnumerator<string> EnumerateDirectory(string directory) {
        bool tags = Path.GetFileName(directory) == "tags";
        events.Add(tags ? "enumerate:tags" : "enumerate:refs");
        string[]? paths = tags ? TagPaths : HeadPaths;
        IEnumerator<string> inner = paths is null ? base.EnumerateDirectory(directory) : ((IEnumerable<string>)paths).GetEnumerator();
        var cursor = new ForkQueryEnumerator(inner, events);
        ConfigureCursor?.Invoke(tags, cursor);
        Cursors.Add(new AuditCursor(tags, cursor));
        return cursor;
    }

    internal override IRbfFile OpenTagBucket(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        if (!readOnly) { WritableTagOpens++; }
        return base.OpenTagBucket(path, readOnly, out recovery);
    }
}

/// <summary>保持正式路径/工厂资格；所有业务写入口立即拒绝，读模式与同时打开数量单独取证。</summary>
internal sealed class AuditFiles(DirectoryHeadStoreFiles inner, string basePath, List<string> events)
    : IHeadStoreFiles, IHeadStoreTagFiles {
    internal readonly List<AuditFile> Opened = [];
    internal Action<AuditFile>? ConfigureOpen;
    internal Action<AuditFile>? AfterOpen;
    internal Exception? HeadOpenFailure;
    internal Exception? TagOpenFailure;
    internal int ActiveFiles;
    internal int MaximumActiveFiles;
    internal int MutationCalls;
    internal int ListCalls;

    private IRbfFile Track(IRbfFile acquired, uint id, bool isTag, bool readOnly, Action markOwnedCleanupFault) {
        IRbfFile? resource = acquired;
        try {
            var tracked = new AuditFile(id, isTag, readOnly, acquired,
                Path.Combine(basePath, $"audit-read-{Opened.Count}.csv"), events, () => ActiveFiles--);
            ConfigureOpen?.Invoke(tracked);
            // Keep the real resource local until the fallible handoff observer completes.
            AfterOpen?.Invoke(tracked);
            Opened.Add(tracked);
            ActiveFiles++;
            MaximumActiveFiles = Math.Max(MaximumActiveFiles, ActiveFiles);
            resource = null;
            return tracked;
        }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            IRbfFile? closing = resource;
            resource = null;
            try { closing?.Dispose(); }
            catch (Exception cleanup) { markOwnedCleanupFault(); errors.Add(cleanup); }
            errors.ThrowIfAny();
            throw;
        }
    }

    public IRbfFile OpenHead(QualifiedHeadTarget target, bool readOnly, Action markOwnedCleanupFault) {
        events.Add($"ref:open:{target.LocalId}:{(readOnly ? "ro" : "rw")}");
        if (HeadOpenFailure is { } failure) { throw failure; }
        return Track(inner.OpenHead(target, readOnly, markOwnedCleanupFault), target.LocalId, false, readOnly, markOwnedCleanupFault);
    }

    public IRbfFile OpenTagBucket(QualifiedTagBucketTarget target, bool readOnly, Action markOwnedCleanupFault) {
        events.Add($"tag:open:{target.BucketId}:{(readOnly ? "ro" : "rw")}");
        if (TagOpenFailure is { } failure) { throw failure; }
        return Track(inner.OpenTagBucket(target, readOnly, markOwnedCleanupFault), target.BucketId, true, readOnly, markOwnedCleanupFault);
    }

    public AteliaResult<QualifiedHeadTarget> ResolveHead(uint localId, Action markOwnedCleanupFault) => inner.ResolveHead(localId, markOwnedCleanupFault);
    public AteliaError? VisitHeadTargets(Func<QualifiedHeadTarget, AteliaError?> visit, Action checkState,
        Action markOwnedCleanupFault) => inner.VisitHeadTargets(visit, checkState, markOwnedCleanupFault);
    public AteliaResult<IReadOnlyList<uint>> ListHeadIds(Action markOwnedCleanupFault) {
        ListCalls++;
        return inner.ListHeadIds(markOwnedCleanupFault);
    }
    public QualifiedTagBucketTarget? ResolveTagBucket(uint bucketId, Action markOwnedCleanupFault) => inner.ResolveTagBucket(bucketId, markOwnedCleanupFault);
    public IEnumerator<QualifiedTagBucketTarget> EnumerateTagBucketTargets(Action checkState, Action markOwnedCleanupFault) =>
        inner.EnumerateTagBucketTargets(checkState, markOwnedCleanupFault);

    private InvalidOperationException Mutation() { MutationCalls++; return new InvalidOperationException("Audit attempted mutation."); }
    public AteliaResult<QualifiedHeadTarget> PrepareNewHead(uint localId, Action markOwnedCleanupFault) => throw Mutation();
    public IRbfFile CreatePending(Action markOwnedCleanupFault) => throw Mutation();
    public void PublishPending(QualifiedHeadTarget target, Action markOwnedCleanupFault) => throw Mutation();
    public void EnsureTagsDirectory(Action markOwnedCleanupFault) => throw Mutation();
    public AteliaResult<QualifiedTagBucketTarget> PrepareNewTagBucket(uint bucketId, Action markOwnedCleanupFault) => throw Mutation();
    public void PublishPendingTagBucket(QualifiedTagBucketTarget target, Action markOwnedCleanupFault) => throw Mutation();
}

/// <summary>FrameInfo 仍绑定真实 reader；读取与租用观察因此装在实际 RBF reader 上。</summary>
internal sealed class AuditFile : IRbfFile {
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly IRbfFile _inner;
    private readonly string _readLog;
    private readonly List<string> _events;
    private readonly Action _closed;
    internal readonly uint Id;
    internal readonly bool IsTag;
    internal readonly bool ReadOnly;
    internal readonly List<AuditReadRequest> Reads = [];
    internal readonly List<int> Rents = [];
    internal Action<long, int, bool>? BeforeRead;
    internal Action<int>? BeforeRent;
    internal Action<string>? AfterAccess;
    internal Action? AfterDispose;
    internal Exception? DisposeFailure;
    internal int DisposeCalls;
    internal int MutationCalls;

    internal AuditFile(uint id, bool isTag, bool readOnly, IRbfFile inner, string readLog, List<string> events, Action closed) {
        Id = id;
        IsTag = isTag;
        ReadOnly = readOnly;
        _inner = inner;
        _readLog = readLog;
        _events = events;
        _closed = closed;
        inner.SetupReadLog(readLog);
        object reader = inner.GetType().GetField("_reader", Members)?.GetValue(inner)
            ?? throw new InvalidOperationException("Missing actual RBF reader diagnostic seam.");
        reader.GetType().GetProperty("ReadObserver", Members)!.SetValue(reader,
            (Action<long, int, bool>)((offset, length, raw) => {
                Reads.Add(new AuditReadRequest(offset, length, raw));
                BeforeRead?.Invoke(offset, length, raw);
            }));
        reader.GetType().GetProperty("BufferRentObserver", Members)!.SetValue(reader,
            (Action<int>)(length => { Rents.Add(length); BeforeRent?.Invoke(length); }));
    }

    internal int CompleteReads(SizedPtr ticket) {
        if (!File.Exists(_readLog)) { return 0; }
        return File.ReadAllLines(_readLog).Count(line => {
            if (line.StartsWith('#') || line.StartsWith("seq,", StringComparison.Ordinal)) { return false; }
            string[] fields = line.Split(',');
            return long.Parse(fields[1], CultureInfo.InvariantCulture) == ticket.Offset
                && int.Parse(fields[2], CultureInfo.InvariantCulture) == ticket.Length;
        });
    }

    public RbfFormat Format { get { var value = _inner.Format; AfterAccess?.Invoke("format"); return value; } }
    public long TailOffset => _inner.TailOffset;
    public RbfForwardSequence ScanForward(bool showTombstone = false) {
        var sequence = _inner.ScanForward(showTombstone);
        AfterAccess?.Invoke("forward");
        return sequence;
    }
    public RbfReverseSequence ScanReverse(bool showTombstone = false) => _inner.ScanReverse(showTombstone);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => _inner.ScanForward(boundary, showTombstone);
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => _inner.ScanReverse(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => _inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => _inner.GetScanBoundaryAfter(ticket);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => _inner.ReadFrameInfo(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => _inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => _inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => _inner.ReadPooledFrame(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => _inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => _inner.ReadPooledTailMeta(ticket);
    public void SetupReadLog(string? logPath) => _inner.SetupReadLog(logPath);
    private InvalidOperationException Mutation() { MutationCalls++; return new InvalidOperationException("Audit attempted file mutation."); }
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => throw Mutation();
    public RbfFrameBuilder BeginAppend() => throw Mutation();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => throw Mutation();
    public void DurableFlush() => throw Mutation();
    public void Dispose() {
        DisposeCalls++;
        _events.Add($"{(IsTag ? "tag" : "ref")}:close:{Id}");
        try { _inner.Dispose(); }
        finally { _closed(); }
        AfterDispose?.Invoke();
        if (DisposeFailure is { } failure) { throw failure; }
    }
}
