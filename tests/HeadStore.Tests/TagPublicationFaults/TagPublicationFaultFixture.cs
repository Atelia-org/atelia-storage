using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.Runtime;
using Atelia.HeadStore.Tests.Storage;
using Atelia.HeadStore.Tests.TagFaults;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.TagPublicationFaults;

/// <summary>真实 VS 根、生产 tag backend 与真实 RBF；异常由窄代理注入，不等于原生设备或进程终止证据。</summary>
internal sealed class TagPublicationFaultFixture : IDisposable {
    internal const string Name = "tag-48";
    internal const string OtherName = "tag-65";
    internal const uint BucketId = 0x106;
    internal readonly StorageFixture Storage = new();
    internal readonly List<string> Events = [];
    internal readonly StoreIdentity Identity;
    internal readonly TestHeadStoreFiles Heads;
    internal readonly TagPublicationFiles Tags;
    internal readonly RecordingOwnerLock Lock;
    internal readonly HeadStoreTestHooks Hooks = new();
    internal readonly HeadStore Store;
    internal DataStore Data => Storage.Data;
    internal string PendingPath => Storage.PendingPath;
    internal string BucketPath => Path.Combine(Storage.Root, "tags", "00000106.rbf");

    internal TagPublicationFaultFixture(bool readOnly = false, HeadStoreFactoryOperations? operations = null) {
        byte[] identity = Storage.Create();
        if (!StoreIdentity.TryRead(identity, out Identity)
            || !StoreIdentity.TryRead(Data.StoreId, out StoreIdentity dataIdentity)) {
            throw new InvalidOperationException("Fixture identity is invalid.");
        }
        // Reuse the ref fault seam for the sole pending path and cached ref cleanup.
        Heads = new TestHeadStoreFiles(Events, Storage.CreatingPath);
        Tags = new TagPublicationFiles(new DirectoryHeadStoreFiles(Storage.Root, operations), Storage.BasePath, Events);
        Lock = new RecordingOwnerLock(Events);
        Hooks.ConfirmDataDurable = () => { Events.Add("data:confirm"); Data.ConfirmDurable(); };
        Hooks.GetRecordedAtUnixMs = () => { Events.Add("clock"); return -17; };
        Store = new HeadStore(Data, Identity, dataIdentity, 17, readOnly, Heads, Lock, Hooks,
            tagFiles: Tags);
    }

    internal HeadId SeedHead(uint localId = 1) {
        Storage.SeedFormal(localId);
        Heads.Paths.Add(localId, Storage.HeadPath(localId));
        return HeadId.Create(Identity, localId);
    }

    internal TagBucketSeed SeedBucket(params TagFaultRecord[] records) => SeedBucketWithVersion(1, records);

    internal TagBucketSeed SeedBucketWithVersion(uint version, params TagFaultRecord[] records) {
        Directory.CreateDirectory(Path.GetDirectoryName(BucketPath)!);
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, version);
        Identity.TryWrite(header.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), BucketId);
        using IRbfFile file = RbfFile.CreateNew(BucketPath, RbfCacheMode.Off);
        SizedPtr headerTicket = file.Append(0, header).Unwrap();
        SizedPtr[] tickets = records.Select(record => file.Append(record.Tag, record.Payload, record.Metadata).Unwrap()).ToArray();
        file.DurableFlush();
        return new TagBucketSeed(headerTicket, tickets, records);
    }

    internal static TagFaultRecord Record(string name = Name, byte[]? payload = null) =>
        new(payload ?? TagFaultFixture.Payload(), TagFaultFixture.Metadata(name));

    internal void CorruptRoot(SizedPtr ticket) {
        using var file = new FileStream(BucketPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = ticket.Offset + 4;
        int value = file.ReadByte();
        file.Position--;
        file.WriteByte((byte)(value ^ 1));
    }

    internal void AssertFaultedAndCleaned() {
        Assert.Throws<InvalidOperationException>(() => Store.ResolveTag(Name));
        Assert.All(Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(Heads.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(Heads.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, Lock.DisposeCalls);
        Assert.Equal("lock:close", Events[^1]);
        Store.Dispose();
        Store.Dispose();
        Assert.Equal(1, Lock.DisposeCalls);
        Assert.All(Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Data.EnsureUsable();
    }

    public void Dispose() {
        try { Store.Dispose(); }
        finally { Storage.Dispose(); }
    }
}

internal sealed record TagBucketSeed(SizedPtr Header, SizedPtr[] Tickets, TagFaultRecord[] Records);

/// <summary>保留生产目标资格、I 长度前检、工厂及不覆盖移动；代理仅记录调用并注入单一故障窗口。</summary>
internal sealed class TagPublicationFiles(IHeadStoreTagFiles inner, string root, List<string> events) : IHeadStoreTagFiles {
    internal readonly List<TagPublicationFile> Opened = [];
    internal Action<TagPublicationFile>? ConfigureOpen;
    internal Exception? ResolveFailure;
    internal Exception? ResolveCleanupFailure;
    internal Exception? OpenFailure;
    internal Exception? OpenCleanupFailure;
    internal Exception? EnsureFailure;
    internal bool EnsureThenThrow;
    internal Exception? PrepareFailure;
    internal Exception? RenameFailure;
    internal bool RenameThenThrow;
    internal int ResolveCalls;
    internal int OpenCalls;
    internal int EnsureCalls;
    internal int PrepareCalls;
    internal int RenameCalls;

    public IEnumerator<QualifiedTagBucketTarget> EnumerateTagBucketTargets(Action checkState, Action markOwnedCleanupFault) =>
        inner.EnumerateTagBucketTargets(checkState, markOwnedCleanupFault);

    public QualifiedTagBucketTarget? ResolveTagBucket(uint bucketId, Action markOwnedCleanupFault) {
        ResolveCalls++;
        events.Add("bucket:resolve");
        if (ResolveCleanupFailure is { } close) { markOwnedCleanupFault(); throw close; }
        if (ResolveFailure is { } error) { throw error; }
        return inner.ResolveTagBucket(bucketId, markOwnedCleanupFault);
    }

    public IRbfFile OpenTagBucket(QualifiedTagBucketTarget target, bool readOnly, Action markOwnedCleanupFault) {
        OpenCalls++;
        events.Add("bucket:open");
        if (OpenCleanupFailure is { } close) { markOwnedCleanupFault(); throw close; }
        if (OpenFailure is { } error) { throw error; }
        IRbfFile? file = inner.OpenTagBucket(target, readOnly, markOwnedCleanupFault);
        try {
            var tracked = new TagPublicationFile(file, root, Opened.Count, events);
            ConfigureOpen?.Invoke(tracked);
            Opened.Add(tracked);
            file = null;
            return tracked;
        }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            IRbfFile? closing = file;
            file = null;
            try { closing?.Dispose(); }
            catch (Exception cleanupError) { markOwnedCleanupFault(); errors.Add(cleanupError); }
            errors.ThrowIfAny();
            throw;
        }
    }

    public void EnsureTagsDirectory(Action markOwnedCleanupFault) {
        EnsureCalls++;
        events.Add("tags:ensure");
        if (EnsureFailure is { } error && !EnsureThenThrow) { throw error; }
        inner.EnsureTagsDirectory(markOwnedCleanupFault);
        if (EnsureFailure is { } later) { throw later; }
    }

    public AteliaResult<QualifiedTagBucketTarget> PrepareNewTagBucket(uint bucketId, Action markOwnedCleanupFault) {
        PrepareCalls++;
        events.Add("bucket:prepare");
        if (PrepareFailure is { } error) { throw error; }
        return inner.PrepareNewTagBucket(bucketId, markOwnedCleanupFault);
    }

    public void PublishPendingTagBucket(QualifiedTagBucketTarget target, Action markOwnedCleanupFault) {
        RenameCalls++;
        events.Add("bucket:rename");
        if (RenameFailure is { } error && !RenameThenThrow) { throw error; }
        inner.PublishPendingTagBucket(target, markOwnedCleanupFault);
        if (RenameFailure is { } later) { throw later; }
    }
}

/// <summary>FrameInfo 仍绑定真实 reader；完整读和小 meta 的证据来自真实 observer/read log。</summary>
internal sealed class TagPublicationFile : IRbfFile {
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    internal readonly IRbfFile Inner;
    private readonly List<string> _events;
    private readonly string _readLog;
    internal readonly List<TagReadRequest> Reads = [];
    internal readonly List<int> Rents = [];
    internal Action<long, int, bool>? BeforeRead;
    internal Action<int>? BeforeRent;
    internal long? TailOffsetOverride;
    internal Exception? AppendFailure;
    internal bool AppendThenThrow;
    internal AteliaError? AppendError;
    internal Exception? FlushFailure;
    internal bool FlushThenThrow;
    internal Exception? DisposeFailure;
    internal int AppendCalls;
    internal int FlushCalls;
    internal int DisposeCalls;
    internal byte[]? AppendedMeta;

    internal TagPublicationFile(IRbfFile inner, string root, int index, List<string> events) {
        Inner = inner;
        _events = events;
        _readLog = Path.Combine(root, $"tag-publish-reads-{index}.csv");
        inner.SetupReadLog(_readLog);
        object reader = inner.GetType().GetField("_reader", Members)?.GetValue(inner)
            ?? throw new InvalidOperationException("Missing actual RBF reader diagnostic seam.");
        reader.GetType().GetProperty("ReadObserver", Members)!.SetValue(reader,
            (Action<long, int, bool>)((offset, length, raw) => {
                Reads.Add(new TagReadRequest(offset, length, raw));
                BeforeRead?.Invoke(offset, length, raw);
            }));
        reader.GetType().GetProperty("BufferRentObserver", Members)!.SetValue(reader,
            (Action<int>)(length => { Rents.Add(length); BeforeRent?.Invoke(length); }));
    }

    internal TagReadRequest[] LoggedReads() {
        if (DisposeCalls == 0) { throw new InvalidOperationException("Close the actual reader before reading its log."); }
        return File.ReadAllLines(_readLog).Where(line => !line.StartsWith('#') && !line.StartsWith("seq,", StringComparison.Ordinal))
            .Select(line => line.Split(','))
            .Select(fields => new TagReadRequest(long.Parse(fields[1], CultureInfo.InvariantCulture),
                int.Parse(fields[2], CultureInfo.InvariantCulture), false, int.Parse(fields[3], CultureInfo.InvariantCulture))).ToArray();
    }

    public RbfFormat Format => Inner.Format;
    public long TailOffset => TailOffsetOverride ?? Inner.TailOffset;
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
        AppendCalls++;
        _events.Add("bucket:append");
        AppendedMeta = tailMeta.ToArray();
        if (AppendError is { } rejection) { return rejection; }
        if (AppendFailure is { } error && !AppendThenThrow) { throw error; }
        var result = Inner.Append(tag, payload, tailMeta);
        if (AppendFailure is { } later) { throw later; }
        return result;
    }
    public void DurableFlush() {
        FlushCalls++;
        _events.Add("bucket:flush");
        if (FlushFailure is { } error && !FlushThenThrow) { throw error; }
        Inner.DurableFlush();
        if (FlushFailure is { } later) { throw later; }
    }
    public void Dispose() {
        DisposeCalls++;
        _events.Add("bucket:close");
        Inner.Dispose();
        if (DisposeFailure is { } error) { throw error; }
    }
    public RbfForwardSequence ScanForward(bool showTombstone = false) => Inner.ScanForward(showTombstone);
    public RbfFrameBuilder BeginAppend() => Inner.BeginAppend();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => Inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => Inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => Inner.ReadPooledFrame(ticket);
    public RbfReverseSequence ScanReverse(bool showTombstone = false) => Inner.ScanReverse(showTombstone);
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => Inner.ScanReverse(boundary, showTombstone);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => Inner.ScanForward(boundary, showTombstone);
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => Inner.GetScanBoundaryAfter(ticket);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => Inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => Inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => Inner.ReadFrameInfo(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => Inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => Inner.ReadPooledTailMeta(ticket);
    public void SetupReadLog(string? logPath) => Inner.SetupReadLog(logPath);
}
