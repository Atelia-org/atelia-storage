using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.ForkQueries;

/// <summary>公开目录生命周期与真实 RBF writer 镜像；镜像只验证声明图读取，不证明 fork 发布或初始化资格。</summary>
internal sealed class ForkQueryFixture : IDisposable {
    private readonly string _parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
    internal string Sandbox { get; }
    internal string Root { get; }
    internal string DataRoot { get; }
    internal string PendingPath => Path.Combine(Root, "creating", "pending.rbf");
    internal DataStore Data { get; }
    internal StoreIdentity Identity { get; private set; }
    internal static HeadHistoryBudget Unlimited => new(long.MaxValue, long.MaxValue);

    internal ForkQueryFixture() {
        Sandbox = Path.Combine(_parent, "HeadStore-fork-queries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Sandbox);
        Root = Path.Combine(Sandbox, "store");
        DataRoot = Path.Combine(Sandbox, "data");
        Data = DataStore.Create(DataRoot);
    }

    internal Store Create(uint reservedUpper = 32) {
        var store = Store.Create(Root, Data, reservedUpper);
        Assert.True(StoreIdentity.TryRead(store.HeadStoreId, out var identity));
        Identity = identity;
        return store;
    }

    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal HeadId Id(uint localId) => HeadId.Create(Identity, localId);
    internal string HeadPath(uint localId) => Path.Combine(Root, "refs", $"{localId:x8}.rbf");
    internal static byte[] Bytes(byte value = 0x51) => Enumerable.Repeat(value, 128).ToArray();

    /// <summary>使用公开 RBF writer 保持 framing/CRC；来源与记录是受控镜像，不冒称公开 ForkHead 交付。</summary>
    internal HeadEntryId[] WriteMirror(uint localId, HeadEntryId? source, params QueryRecord[] records) {
        return WriteHeaderMirror(localId, Header(localId, source), records);
    }

    internal byte[] Header(uint localId, HeadEntryId? source = null) {
        byte[] bytes = new byte[source.HasValue ? HeadHeaderCodec.WithOriginPayloadSize : HeadHeaderCodec.NoOriginPayloadSize];
        ForkOrigin? origin = source.HasValue ? new ForkOrigin(source.Value.HeadId, source.Value.Ticket) : null;
        Assert.True(HeadHeaderCodec.TryWrite(Identity, Id(localId), origin, bytes));
        return bytes;
    }

    internal HeadEntryId[] WriteHeaderMirror(uint localId, byte[] header, params QueryRecord[] records) {
        using IRbfFile file = RbfFile.CreateNew(HeadPath(localId), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        var entryIds = new HeadEntryId[records.Length];
        for (int index = 0; index < records.Length; index++) {
            QueryRecord record = records[index];
            SizedPtr ticket = file.Append(record.Tag, record.Payload, record.Metadata).Unwrap();
            entryIds[index] = HeadEntryId.Create(Id(localId), ticket);
        }
        file.DurableFlush();
        return entryIds;
    }

    internal HeadEntryId DeclaredPoint(uint localId, long offset = 1_000_000) {
        int length = RbfFile.MeasureWriteSize(128, RecordMetadataCodec.EncodedSize).Unwrap().FrameLength;
        return HeadEntryId.Create(Id(localId), SizedPtr.Create(offset, length));
    }

    internal void FlipPayload(HeadEntryId entryId) =>
        FlipByte(entryId.HeadId.LocalId, entryId.Ticket.Offset + entryId.Ticket.Length / 2);

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
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(HeadPath(entryId.HeadId.LocalId), RbfCacheMode.Off)) {
            end = file.GetPhysicalOffsetImmediatelyAfter(entryId.Ticket);
        }
        FlipByte(entryId.HeadId.LocalId, end - 1);
    }

    private void FlipByte(uint localId, long offset) {
        using var stream = new FileStream(HeadPath(localId), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int original = stream.ReadByte();
        Assert.True(original >= 0);
        stream.Position--;
        stream.WriteByte((byte)(original ^ 1));
    }

    internal void AssertFactoryQualified(params uint[] localIds) {
        foreach (uint localId in localIds) {
            using IRbfFile file = RbfFile.OpenReadOnlyExisting(HeadPath(localId), RbfCacheMode.Off);
        }
    }

    internal void AssertReleased(params uint[] localIds) {
        foreach (uint localId in localIds) {
            // A separate real writer open detects a retained temporary reader/writer on Windows.
            using IRbfFile file = RbfFile.OpenExisting(HeadPath(localId), out _, RbfCacheMode.Off);
        }
    }

    internal static void AssertEdges(IReadOnlyList<HeadOriginInfo> actual,
        params (HeadId Child, HeadId Source, SizedPtr Ticket)[] expected) {
        Assert.Equal(expected.Length, actual.Count);
        var byChild = expected.ToDictionary(edge => edge.Child);
        var seen = new HashSet<HeadId>();
        foreach (HeadOriginInfo edge in actual) {
            Assert.True(seen.Add(edge.ChildHeadId), "A child must occur exactly once in the complete edge collection.");
            Assert.True(byChild.TryGetValue(edge.ChildHeadId, out var wanted));
            Assert.Equal(wanted.Source, edge.SourceHeadId);
            Assert.Equal(wanted.Ticket, edge.SourceEntryTicket);
            Assert.Equal(wanted.Ticket.Packed, edge.SourceEntryTicket.Packed);
        }
    }

    public void Dispose() {
        Data.Dispose();
        string resolved = Path.GetFullPath(Sandbox);
        string parent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("HeadStore-fork-queries-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}

internal sealed record QueryRecord(byte[] Payload, uint Tag = 1, int MetadataLength = 8, long Time = 0) {
    internal byte[] Metadata {
        get {
            byte[] result = new byte[MetadataLength];
            if (MetadataLength >= sizeof(long)) { BinaryPrimitives.WriteInt64LittleEndian(result, Time); }
            return result;
        }
    }
}
