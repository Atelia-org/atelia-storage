using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Forks;

/// <summary>公开工厂与真实 RBF 文件夹具；受控改字节/记录镜像只验证读取边界，不签发公开创建资格。</summary>
internal sealed class ForkFixture : IDisposable {
    private readonly string _parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
    internal string Sandbox { get; }
    internal string Root { get; }
    internal string DataRoot { get; }
    internal string PendingPath => Path.Combine(Root, "creating", "pending.rbf");
    internal DataStore Data { get; }
    internal StoreIdentity Identity { get; private set; }
    internal static HeadHistoryBudget Unlimited => new(long.MaxValue, long.MaxValue);

    internal ForkFixture() {
        Sandbox = Path.Combine(_parent, "HeadStore-forks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Sandbox);
        Root = Path.Combine(Sandbox, "store");
        DataRoot = Path.Combine(Sandbox, "data");
        Data = DataStore.Create(DataRoot);
    }

    internal Store Create(uint reservedUpper = 256) {
        var store = Store.Create(Root, Data, reservedUpper);
        Assert.True(StoreIdentity.TryRead(store.HeadStoreId, out var identity));
        Identity = identity;
        return store;
    }

    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal HeadId Id(uint localId) => HeadId.Create(Identity, localId);
    internal string HeadPath(uint localId) => Path.Combine(Root, "refs", $"{localId:x8}.rbf");

    internal HeadEntryId[] SeedClosed(uint localId, params byte[][] payloads) {
        Assert.NotEmpty(payloads);
        using var store = Create();
        var entryIds = new HeadEntryId[payloads.Length];
        entryIds[0] = store.CreateHead(localId, payloads[0], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        for (int index = 1; index < payloads.Length; index++) {
            entryIds[index] = store.PublishHead(entryIds[0].HeadId, payloads[index], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        return entryIds;
    }

    /// <summary>仅用于坏 envelope/时间反例；返回值为受控内部坐标，不冒称公开 mutation 交付的 entryId。</summary>
    internal HeadEntryId[] WriteControlledHead(uint localId, params ForkRecord[] records) {
        byte[] header = new byte[HeadHeaderCodec.NoOriginPayloadSize];
        Assert.True(HeadHeaderCodec.TryWrite(Identity, Id(localId), null, header));
        using IRbfFile file = RbfFile.CreateNew(HeadPath(localId), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        var entryIds = new HeadEntryId[records.Length];
        for (int index = 0; index < records.Length; index++) {
            var record = records[index];
            SizedPtr ticket = file.Append(record.Tag, record.Payload, record.Metadata).Unwrap();
            entryIds[index] = HeadEntryId.Create(Id(localId), ticket);
        }
        file.DurableFlush();
        return entryIds;
    }

    internal static byte[] Bytes(int length = 128, byte value = 0x51) => Enumerable.Repeat(value, length).ToArray();

    internal void FlipPayload(HeadEntryId entryId) =>
        FlipByte(entryId.HeadId.GetLocalId(Identity), entryId.Ticket.Offset + entryId.Ticket.Length / 2);

    internal void FlipHeaderPayload(uint localId) {
        SizedPtr ticket;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(HeadPath(localId), RbfCacheMode.Off)) {
            var forward = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(forward.MoveNext());
            ticket = forward.Current.Ticket;
        }
        FlipByte(localId, ticket.Offset + ticket.Length / 2);
    }

    internal void FlipFollowingFence(HeadEntryId entryId) {
        long end;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(HeadPath(entryId.HeadId.GetLocalId(Identity)), RbfCacheMode.Off)) {
            end = file.GetPhysicalOffsetImmediatelyAfter(entryId.Ticket);
        }
        FlipByte(entryId.HeadId.GetLocalId(Identity), end - 1);
    }

    private void FlipByte(uint localId, long offset) {
        using var stream = new FileStream(HeadPath(localId), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int original = stream.ReadByte();
        Assert.True(original >= 0);
        stream.Position--;
        stream.WriteByte((byte)(original ^ 1));
    }

    internal void AssertNoChild(Store store, uint localId) {
        Assert.False(File.Exists(HeadPath(localId)));
        Assert.False(File.Exists(PendingPath));
        Assert.DoesNotContain(store.ListHeads().Unwrap(), id => id.GetLocalId(Identity) == localId);
    }

    internal static List<HeadEntry> Collect(Store store, HeadId headId) {
        var entries = new List<HeadEntry>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(headId, Unlimited, entry => {
            entries.Add(entry);
            return true;
        }).Unwrap());
        return entries;
    }

    internal Dictionary<string, byte[]> SnapshotFiles() {
        // Call only between owners: the permanent lock is deliberately excluded from byte reads.
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "versionstore.lock")
            .ToDictionary(path => Path.GetRelativePath(Root, path), File.ReadAllBytes, StringComparer.Ordinal);
    }

    internal void AssertFilesUnchanged(Dictionary<string, byte[]> before) {
        var after = SnapshotFiles();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); }
    }

    public void Dispose() {
        Data.Dispose();
        string resolved = Path.GetFullPath(Sandbox);
        string parent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("HeadStore-forks-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}

internal sealed record ForkRecord(byte[] Payload, long Time = 0, uint Tag = 1, int MetadataLength = 8) {
    internal byte[] Metadata {
        get {
            byte[] result = new byte[MetadataLength];
            if (MetadataLength >= sizeof(long)) { BinaryPrimitives.WriteInt64LittleEndian(result, Time); }
            return result;
        }
    }
}
