using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Storage;
using Atelia.VersionStore.Tests.Public;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdateFaults;

/// <summary>公开创建的真实根/data/锁；窄代理全部转发 public RBF，不模拟底层 framing 或 CRC。</summary>
/// <remarks>手工追加/改字节仅构造输入镜像，不能作为 Kill、设备故障或断电证据。</remarks>
internal sealed class ConditionalUpdateFaultFixture : IDisposable {
    private readonly PublicVersionStoreFixture _public = new();
    internal readonly List<string> Events = [];
    internal readonly VersionStoreTestHooks Hooks = new();
    internal readonly ConditionalUpdateOperations Operations;
    internal readonly Dictionary<uint, RefRevision> Initial = [];
    internal readonly StoreIdentity Identity;
    internal VersionStore? Owner;
    internal string Root => _public.Root;
    internal Atelia.FrameStore.FrameStore Data => _public.Data;
    internal string RefPath(uint id) => _public.RefPath(id);

    internal ConditionalUpdateFaultFixture() {
        using (var store = VersionStore.Create(Root, Data, 256)) {
            StoreIdentity.TryRead(store.VersionStoreId, out Identity);
            for (uint id = 1; id <= 3; id++) {
                Initial.Add(id, store.CreateRef(id, Old(id), out _).Unwrap());
            }
        }
        Operations = new ConditionalUpdateOperations(Events);
        Hooks.ConfirmDataDurable = () => { Events.Add("data:confirm"); Data.ConfirmDurable(); };
        Hooks.GetRecordedAtUnixMs = () => 456;
    }

    internal VersionStore Open(bool readOnly = false) {
        Close();
        Owner = VersionStoreFactory.Open(Root, Data, readOnly, Operations, Hooks);
        return Owner;
    }

    internal VersionStore OpenPublic(bool readOnly = false) =>
        readOnly ? VersionStore.OpenReadOnly(Root, Data) : VersionStore.Open(Root, Data);

    internal void Close() { Owner?.Dispose(); Owner = null; }
    internal Dictionary<string, byte[]?> Snapshot() => _public.Snapshot();
    internal void AssertUnchanged(Dictionary<string, byte[]?> before) => _public.AssertUnchanged(before);
    internal RefId Id(uint localId) => Initial[localId].RefId;
    internal static byte[] Old(uint id) => [(byte)id, 0x11, 0x12];
    internal static byte[] Bytes(byte salt, int length = 64) => Enumerable.Range(0, length).Select(i => (byte)(salt + i)).ToArray();
    internal IReadOnlyDictionary<RefId, ReadOnlyMemory<byte>> Changes(params (uint Id, byte[] Bytes)[] entries) =>
        entries.ToDictionary(entry => Id(entry.Id), entry => (ReadOnlyMemory<byte>)entry.Bytes);

    internal static RefBytesSnapshot[] History(VersionStore store, RefId id, HistoryBudget? budget = null) {
        var values = new List<RefBytesSnapshot>();
        Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(id, budget ?? new(long.MaxValue, long.MaxValue), value => {
            values.Add(value);
            return true;
        }).Unwrap());
        return values.ToArray();
    }

    internal void AssertColdOld() {
        Close();
        using var reader = OpenPublic(readOnly: true);
        foreach (var pair in Initial) {
            Assert.Equal(pair.Value, reader.ReadRef(pair.Key).Unwrap().Revision);
            Assert.Equal(new[] { pair.Value }, History(reader, pair.Value.RefId).Select(value => value.Revision));
        }
    }

    internal void AssertFaultCleanup() {
        Assert.NotNull(Owner);
        Assert.Throws<InvalidOperationException>(() => Owner!.ListRefs());
        Assert.All(Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, Operations.Locks[^1].DisposeCalls);
        Assert.Equal("lock:close", Events[^1]);
        Owner!.Dispose();
        Owner.Dispose();
        Assert.All(Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, Operations.Locks[^1].DisposeCalls);
        Data.EnsureUsable();
    }

    internal static uint[] OwnedRefIds(VersionStore store) {
        var owned = (IDictionary)typeof(VersionStore).GetField("_ownedRefs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        return owned.Keys.Cast<uint>().Order().ToArray();
    }

    // Independent encoding of the selected VersionStore envelope; RBF constructs every physical frame.
    internal static byte[] Envelope(IEnumerable<(uint Id, SizedPtr Ticket)> members, byte[] root) {
        var entries = members.ToArray();
        byte[] bytes = new byte[checked(4 + 12 * entries.Length + root.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + 12 * i), entries[i].Id);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8 + 12 * i), entries[i].Ticket.Packed);
        }
        root.CopyTo(bytes, 4 + 12 * entries.Length);
        return bytes;
    }

    internal static byte[] Metadata(int length = 8) {
        var bytes = new byte[length];
        if (length >= 8) { BinaryPrimitives.WriteInt64LittleEndian(bytes, 789); }
        return bytes;
    }

    internal SizedPtr Predict(uint id, int payloadLength) {
        using var file = RbfFile.OpenReadOnlyExisting(RefPath(id), RbfCacheMode.Off);
        return SizedPtr.Create(file.TailOffset, RbfFile.MeasureWriteSize(payloadLength, 8).Unwrap().FrameLength);
    }

    internal SizedPtr Append(uint id, uint tag, byte[] payload, byte[]? metadata = null) {
        using var file = RbfFile.OpenExisting(RefPath(id), out _, RbfCacheMode.Off);
        SizedPtr ticket = file.Append(tag, payload, metadata ?? Metadata()).Unwrap();
        file.DurableFlush();
        return ticket;
    }

    internal void Flip(uint id, long offset) {
        using var stream = new FileStream(RefPath(id), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int value = stream.ReadByte();
        Assert.True(value >= 0);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 1));
    }

    public void Dispose() {
        try { Close(); }
        finally { _public.Dispose(); }
    }
}

internal sealed class ConditionalUpdateOperations(List<string> events) : VersionStoreFactoryOperations {
    internal readonly List<ConditionalUpdateFile> Files = [];
    internal readonly List<ConditionalUpdateLock> Locks = [];
    internal Action<ConditionalUpdateFile>? ConfigureOpen;

    internal override IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) {
        var ownerLock = new ConditionalUpdateLock(base.AcquireOwnerLock(root, create, readOnly), events);
        Locks.Add(ownerLock);
        return ownerLock;
    }

    internal override IRbfFile OpenRef(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        var file = new ConditionalUpdateFile(uint.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.NumberStyles.HexNumber),
            base.OpenRef(path, readOnly, out recovery), events);
        try { ConfigureOpen?.Invoke(file); Files.Add(file); return file; }
        catch { file.Dispose(); throw; }
    }
}

internal sealed class ConditionalUpdateLock(IDisposable inner, List<string> events) : IDisposable {
    internal int DisposeCalls;
    internal Exception? DisposeFailure;
    public void Dispose() {
        DisposeCalls++;
        events.Add("lock:close");
        inner.Dispose();
        if (DisposeFailure is { } failure) { throw failure; }
    }
}

internal sealed class ConditionalUpdateFile(uint id, IRbfFile inner, List<string> events) : IRbfFile {
    internal readonly uint Id = id;
    internal int BeginCalls;
    internal int FlushCalls;
    internal int DisposeCalls;
    internal int CompleteReadCalls;
    internal int SuccessorCalls;
    internal int IdleCloseProbes;
    internal bool WrongBeginTicket;
    internal bool ProbeIdleBeforeClose;
    internal Exception? DisposeFailure;
    internal Exception? ReadFailure;
    internal Exception? SuccessorFailure;
    internal Exception? FlushFailure;
    internal bool FlushThenThrow;
    internal Action<SizedPtr>? BeforeCompleteRead;
    internal Action? BeforeSuccessor;
    internal RbfFrameBuilder? LastBuilder;

    public RbfFormat Format => inner.Format;
    public long TailOffset => inner.TailOffset;
    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) {
        BeginCalls++;
        events.Add($"begin:{Id}");
        var builder = inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
        LastBuilder = builder;
        if (WrongBeginTicket) { ticket = SizedPtr.Create(ticket.Offset + 4, ticket.Length); }
        return builder;
    }
    public void DurableFlush() {
        FlushCalls++;
        events.Add($"flush:{Id}");
        if (FlushFailure is { } before && !FlushThenThrow) { throw before; }
        inner.DurableFlush();
        if (FlushFailure is { } after) { throw after; }
    }
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) {
        CompleteReadCalls++;
        events.Add($"read:{Id}");
        BeforeCompleteRead?.Invoke(ticket);
        if (ReadFailure is { } failure) { throw failure; }
        return inner.ReadPooledFrame(ticket);
    }
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) {
        SuccessorCalls++;
        events.Add($"successor:{Id}");
        BeforeSuccessor?.Invoke();
        if (SuccessorFailure is { } failure) { throw failure; }
        return inner.ReadFrameInfoImmediatelyAfter(ticket);
    }
    public void Dispose() {
        DisposeCalls++;
        events.Add($"close:{Id}");
        if (ProbeIdleBeforeClose) { inner.ScanReverse(showTombstone: true); IdleCloseProbes++; }
        inner.Dispose();
        if (DisposeFailure is { } failure) { throw failure; }
    }
    public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
    public RbfForwardSequence ScanForward(bool showTombstone = false) => inner.ScanForward(showTombstone);
    public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanReverse(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => inner.GetScanBoundaryAfter(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
    public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
}
