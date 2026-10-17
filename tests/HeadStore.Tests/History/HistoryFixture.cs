using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.History;

/// <summary>公开目录生命周期及真实 RBF writer 镜像；来源镜像不证明 ForkHead 创建资格。</summary>
internal sealed class HistoryFixture : IDisposable {
    private readonly string _parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
    internal string Sandbox { get; }
    internal string Root { get; }
    internal string DataRoot { get; }
    internal DataStore Data { get; }
    internal StoreIdentity Identity { get; }
    internal static HeadHistoryBudget Unlimited => new(long.MaxValue, long.MaxValue);

    internal HistoryFixture() {
        Sandbox = Path.Combine(_parent, "HeadStore-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Sandbox);
        Root = Path.Combine(Sandbox, "store");
        DataRoot = Path.Combine(Sandbox, "data");
        Data = DataStore.Create(DataRoot);
        using var store = Store.Create(Root, Data, 256);
        Assert.True(StoreIdentity.TryRead(store.HeadStoreId, out var identity));
        Identity = identity;
    }

    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal HeadId Id(uint localId) => HeadId.Create(Identity, localId);
    internal string HeadPath(uint localId) => Path.Combine(Root, "refs", $"{localId:x8}.rbf");

    internal HeadEntryId[] SeedPublic(uint localId, params byte[][] payloads) {
        Assert.NotEmpty(payloads);
        using var store = Open();
        var entryIds = new HeadEntryId[payloads.Length];
        entryIds[0] = store.CreateHead(localId, payloads[0], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        for (int index = 1; index < payloads.Length; index++) {
            entryIds[index] = store.PublishHead(Id(localId), payloads[index], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        return entryIds;
    }

    internal HeadEntryId[] WriteMirror(uint localId, HeadEntryId? source, params HistoryRecord[] records) {
        byte[] header = new byte[source.HasValue ? HeadHeaderCodec.WithOriginPayloadSize : HeadHeaderCodec.NoOriginPayloadSize];
        ForkOrigin? origin = source.HasValue ? new ForkOrigin(source.Value.HeadId, source.Value.Ticket) : null;
        Assert.True(HeadHeaderCodec.TryWrite(Identity, Id(localId), origin, header));
        using IRbfFile file = RbfFile.CreateNew(HeadPath(localId), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        var entryIds = new HeadEntryId[records.Length];
        for (int index = 0; index < records.Length; index++) {
            HistoryRecord record = records[index];
            SizedPtr ticket = file.Append(record.Tag, record.Payload, record.Metadata).Unwrap();
            entryIds[index] = HeadEntryId.Create(Id(localId), ticket);
        }
        file.DurableFlush();
        return entryIds;
    }

    internal HeadEntryId DeclaredInitial(uint localId, int payloadLength) {
        // Only public size arithmetic, used to declare a controlled cycle before either file exists.
        long offset = RbfScanBoundary.Empty.EndExclusive
            + RbfFile.MeasureWriteSize(HeadHeaderCodec.WithOriginPayloadSize, 0).Unwrap().AppendLength;
        int length = RbfFile.MeasureWriteSize(payloadLength, RecordMetadataCodec.EncodedSize).Unwrap().FrameLength;
        return HeadEntryId.Create(Id(localId), SizedPtr.Create(offset, length));
    }

    internal static byte[] Bytes(int length = 128, byte value = 0x51) => Enumerable.Repeat(value, length).ToArray();

    internal void FlipPayload(HeadEntryId entryId) {
        FlipByte(entryId.HeadId.GetLocalId(Identity), entryId.Ticket.Offset + entryId.Ticket.Length / 2);
    }

    internal void FlipTrailer(HeadEntryId entryId) {
        // RBF3's documented encoded TrailerCodeword occupies [L-20,L-4); no CRC is recomputed.
        FlipByte(entryId.HeadId.GetLocalId(Identity), entryId.Ticket.Offset + entryId.Ticket.Length - 20);
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

    internal void AssertReleased(params uint[] localIds) {
        foreach (uint localId in localIds) {
            // An active writer ref handle excludes this real independent writer open on Windows.
            using IRbfFile file = RbfFile.OpenExisting(HeadPath(localId), out _, RbfCacheMode.Off);
        }
    }

    internal static List<HeadEntry> Collect(Store store, HeadId id, HeadHistoryBudget? budget = null) {
        var entries = new List<HeadEntry>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(id, budget ?? Unlimited, entry => {
            entries.Add(entry);
            return true;
        }).Unwrap());
        return entries;
    }

    public void Dispose() {
        Data.Dispose();
        string resolved = Path.GetFullPath(Sandbox);
        string parent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("HeadStore-history-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}

internal sealed record HistoryRecord(byte[] Payload, long Time = 0, uint Tag = 1, int MetadataLength = 8) {
    internal byte[] Metadata {
        get {
            byte[] result = new byte[MetadataLength];
            if (MetadataLength >= sizeof(long)) { BinaryPrimitives.WriteInt64LittleEndian(result, Time); }
            return result;
        }
    }
}
