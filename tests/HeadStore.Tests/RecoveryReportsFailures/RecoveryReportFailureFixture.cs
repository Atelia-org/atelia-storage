using System.Buffers.Binary;
using System.Globalization;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.Public;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.RecoveryReportsFailures;

/// <summary>公开工厂创建的真实根、data 和 RBF；代理只观察工厂/关闭边界，不模拟恢复算法。</summary>
/// <remarks>手工输入镜像及工厂返回前异常只证明合同边界，不作为进程崩溃或设备故障证据。</remarks>
internal sealed class RecoveryReportFailureFixture : IDisposable {
    private readonly PublicHeadStoreFixture _public = new();
    internal const string TagName = "A";
    internal readonly List<string> Events = [];
    internal readonly HeadStoreTestHooks Hooks = new();
    internal readonly RecoveryReportOperations Operations;
    internal readonly StoreIdentity Identity;
    internal readonly Dictionary<uint, HeadEntryId> Initial = [];
    internal HeadStore? Owner;
    internal int DataConfirmCalls;
    internal string Root => _public.Root;
    internal Atelia.FrameStore.FrameStore Data => _public.Data;
    internal string HeadPath(uint id) => _public.HeadPath(id);
    internal string TagPath => Path.Combine(Root, "tags", $"{TagPublicFixture.Bucket(TagName):x8}.rbf");

    internal RecoveryReportFailureFixture() {
        using (var store = HeadStore.Create(Root, Data, 256)) {
            Assert.True(StoreIdentity.TryRead(store.HeadStoreId, out Identity));
            Initial.Add(1, store.CreateHead(1, [0x11], out _).Unwrap());
            Initial.Add(2, store.CreateHead(2, [0x22], out _).Unwrap());
            store.CreateTag(TagName, [Initial[1].HeadId], out _).Unwrap();
        }
        Operations = new RecoveryReportOperations(Events);
        Hooks.ConfirmDataDurable = () => {
            DataConfirmCalls++;
            Events.Add("data:confirm");
            Data.ConfirmDurable();
        };
    }

    internal HeadStore Open() {
        Owner = HeadStoreFactory.Open(Root, Data, readOnly: false, Operations, Hooks);
        return Owner;
    }

    // Explicit shared-log seam tests ordering without adding a production observer or simulating OOM.
    internal HeadStore OpenWithLog(HeadStoreRecoveryLog log) {
        Assert.True(StoreIdentity.TryRead(Data.StoreId, out StoreIdentity dataIdentity));
        IDisposable ownerLock = Operations.AcquireOwnerLock(Root, create: false, readOnly: false);
        try {
            var files = new DirectoryHeadStoreFiles(Root, Operations, log);
            Owner = new HeadStore(Data, Identity, dataIdentity, 256, false, files, ownerLock,
                Hooks, recoveryLog: log);
            return Owner;
        }
        catch { ownerLock.Dispose(); throw; }
    }

    internal void RewriteHead(uint id, uint headerId, bool entry = true) {
        File.Delete(HeadPath(id));
        byte[] header = new byte[HeadHeaderCodec.NoOriginPayloadSize];
        Assert.True(HeadHeaderCodec.TryWrite(Identity, HeadId.Create(Identity, headerId), null, header));
        using var file = RbfFile.CreateNew(HeadPath(id), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        if (entry) { file.Append(1, [(byte)id], Metadata()).Unwrap(); }
        file.DurableFlush();
    }

    internal void RewriteTagHeader(uint headerBucket) {
        File.Delete(TagPath);
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 3);
        Assert.True(Identity.TryWrite(header.AsSpan(4, 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), headerBucket);
        using var file = RbfFile.CreateNew(TagPath, RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        file.DurableFlush();
    }

    internal void WriteIncompleteInitialization() {
        File.Delete(TagPath);
        using (var file = RbfFile.CreateNew(TagPath, RbfCacheMode.Off)) {
            file.Append(0, new byte[128]).Unwrap();
            file.DurableFlush();
        }
        byte[] bytes = File.ReadAllBytes(TagPath);
        // Length preflight permits this image, but the actual first RBF frame body is incomplete.
        File.WriteAllBytes(TagPath, bytes[..TagPublicFixture.InitializedLength]);
    }

    internal static byte[] PartialNextFrame(string path) {
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. complete, 1]);
        return complete;
    }

    internal static byte[] MissingTail(string path) {
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^8]);
        return complete;
    }

    internal SizedPtr Predict(uint id, int payloadLength) {
        using var file = RbfFile.OpenReadOnlyExisting(HeadPath(id), RbfCacheMode.Off);
        return SizedPtr.Create(file.TailOffset, RbfFile.MeasureWriteSize(payloadLength, 8).Unwrap().FrameLength);
    }

    internal SizedPtr Append(uint id, uint tag, byte[] payload) {
        using var file = RbfFile.OpenExisting(HeadPath(id), out _, RbfCacheMode.Off);
        SizedPtr ticket = file.Append(tag, payload, Metadata()).Unwrap();
        file.DurableFlush();
        return ticket;
    }

    internal static byte[] Envelope(SizedPtr a, SizedPtr b) {
        byte[] payload = new byte[29];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), a.Packed);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), b.Packed);
        payload[28] = 0x44;
        return payload;
    }

    private static byte[] Metadata() {
        byte[] metadata = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(metadata, 123);
        return metadata;
    }

    internal void AssertSingleCloseAndLockLast() {
        Assert.All(Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, Assert.Single(Operations.Locks).DisposeCalls);
        Assert.Equal("lock:close", Events[^1]);
        Owner!.Dispose();
        Owner.Dispose();
        Assert.All(Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, Operations.Locks[0].DisposeCalls);
    }

    public void Dispose() {
        try { Owner?.Dispose(); }
        finally { _public.Dispose(); }
    }
}

internal sealed class RecoveryReportOperations(List<string> events) : HeadStoreFactoryOperations {
    internal readonly List<RecoveryReportFile> Files = [];
    internal readonly List<RecoveryReportLock> Locks = [];
    internal Action<HeadStoreRecoveryFileKind, uint>? BeforeOpen;
    internal Action<RecoveryReportFile, RbfTailRecoveryReport>? ConfigureOpen;
    internal Exception? ThrowAfterOpen;
    internal RbfTailRecoveryReport LastRecovery;

    internal override IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) {
        var ownerLock = new RecoveryReportLock(base.AcquireOwnerLock(root, create, readOnly), events);
        Locks.Add(ownerLock);
        return ownerLock;
    }

    internal override IRbfFile OpenHead(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        uint id = uint.Parse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        events.Add($"factory:Head:{id}");
        BeforeOpen?.Invoke(HeadStoreRecoveryFileKind.Head, id);
        return Wrap(base.OpenHead(path, readOnly, out recovery), HeadStoreRecoveryFileKind.Head, id, recovery);
    }

    internal override IRbfFile OpenTagBucket(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        uint id = uint.Parse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        events.Add($"factory:TagBucket:{id}");
        BeforeOpen?.Invoke(HeadStoreRecoveryFileKind.TagBucket, id);
        return Wrap(base.OpenTagBucket(path, readOnly, out recovery), HeadStoreRecoveryFileKind.TagBucket, id, recovery);
    }

    private IRbfFile Wrap(IRbfFile inner, HeadStoreRecoveryFileKind kind, uint id, RbfTailRecoveryReport recovery) {
        var file = new RecoveryReportFile(inner, kind, id, events);
        Files.Add(file);
        LastRecovery = recovery;
        try {
            ConfigureOpen?.Invoke(file, recovery);
            if (ThrowAfterOpen is { } failure) { throw failure; }
            return file;
        }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            try { file.Dispose(); }
            catch (Exception cleanup) { errors.Add(cleanup); }
            errors.ThrowIfAny();
            throw;
        }
    }
}

internal sealed class RecoveryReportLock(IDisposable inner, List<string> events) : IDisposable {
    internal int DisposeCalls;
    internal Exception? DisposeFailure;
    public void Dispose() {
        DisposeCalls++;
        events.Add("lock:close");
        inner.Dispose();
        if (DisposeFailure is { } failure) { throw failure; }
    }
}

internal sealed class RecoveryReportFile(IRbfFile inner, HeadStoreRecoveryFileKind kind, uint id,
    List<string> events) : IRbfFile {
    internal int DisposeCalls;
    internal int AppendCalls;
    internal int FlushCalls;
    internal Exception? DisposeFailure;
    internal Action? BeforeScan;
    public RbfFormat Format => inner.Format;
    public long TailOffset => inner.TailOffset;

    public void Dispose() {
        DisposeCalls++;
        events.Add($"close:{kind}:{id}");
        inner.Dispose();
        if (DisposeFailure is { } failure) { throw failure; }
    }
    public RbfForwardSequence ScanForward(bool showTombstone = false) {
        BeforeScan?.Invoke();
        return inner.ScanForward(showTombstone);
    }
    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        BeforeScan?.Invoke();
        return inner.ScanReverse(showTombstone);
    }
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
        AppendCalls++;
        return inner.Append(tag, payload, tailMeta);
    }
    public void DurableFlush() { FlushCalls++; inner.DurableFlush(); }
    public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => inner.ReadPooledFrame(ticket);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => inner.GetScanBoundaryAfter(ticket);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanReverse(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
    public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
}
