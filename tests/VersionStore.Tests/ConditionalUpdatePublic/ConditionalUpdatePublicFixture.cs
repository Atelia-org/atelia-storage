using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Tests.Public;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.ConditionalUpdatePublic;

/// <summary>真实公开工厂与独立 CU envelope；不复制 RBF framing/CRC，也不作为进程终止证据。</summary>
internal sealed class ConditionalUpdatePublicFixture : IDisposable {
    private readonly PublicVersionStoreFixture _storage = new();
    internal string Root => _storage.Root;
    internal string DataRoot => _storage.DataRoot;
    internal DataStore Data => _storage.Data;
    internal static HistoryBudget Unlimited => new(long.MaxValue, long.MaxValue);
    internal Store Create(uint reservation = 256) => Store.Create(Root, Data, reservation);
    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal string RefPath(uint localId) => _storage.RefPath(localId);
    internal Dictionary<string, byte[]?> Snapshot() => _storage.Snapshot();
    internal void AssertUnchanged(Dictionary<string, byte[]?> before) => _storage.AssertUnchanged(before);

    internal static List<RefBytesSnapshot> History(Store store, RefId refId) {
        var snapshots = new List<RefBytesSnapshot>();
        Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(refId, Unlimited, snapshot => {
            snapshots.Add(snapshot);
            return true;
        }).Unwrap());
        return snapshots;
    }

    internal static IReadOnlyDictionary<RefId, ReadOnlyMemory<byte>> Changes(params (RefId Ref, byte[] Bytes)[] entries) =>
        entries.ToDictionary(entry => entry.Ref, entry => (ReadOnlyMemory<byte>)entry.Bytes);

    internal static byte[] EncodeEnvelope(IReadOnlyList<(uint LocalId, SizedPtr Ticket)> members, ReadOnlySpan<byte> root) {
        byte[] payload = new byte[checked(4 + 12 * members.Count + root.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)members.Count));
        for (int index = 0; index < members.Count; index++) {
            int start = 4 + 12 * index;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(start), members[index].LocalId);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(start + 4), members[index].Ticket.Packed);
        }
        root.CopyTo(payload.AsSpan(4 + 12 * members.Count));
        return payload;
    }

    internal SizedPtr Predict(uint localId, int memberCount, int rootLength) {
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(RefPath(localId), RbfCacheMode.Off);
        int length = RbfFile.MeasureWriteSize(checked(4 + 12 * memberCount + rootLength), 8).Unwrap().FrameLength;
        return SizedPtr.Create(file.TailOffset, length);
    }

    internal SizedPtr AppendEnvelope(uint localId, byte[] payload, byte[]? metadata = null) {
        using IRbfFile file = RbfFile.OpenExisting(RefPath(localId), out _, RbfCacheMode.Off);
        SizedPtr ticket = file.Append(2, payload, metadata ?? new byte[8]).Unwrap();
        file.DurableFlush();
        return ticket;
    }

    internal void AssertWire(RefRevision revision, IReadOnlyDictionary<RefId, RefRevision> revisions,
        byte[] root, long minimumTime, long maximumTime, long? expectedTime = null) {
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(RefPath(revision.RefId.LocalId), RbfCacheMode.Off);
        using RbfPooledFrame frame = file.ReadPooledFrame(revision.Ticket).Unwrap();
        Assert.Equal(revision.Ticket, frame.Ticket);
        Assert.Equal(2u, frame.Tag);
        Assert.False(frame.IsTombstone);
        Assert.Equal(8, frame.TailMetaLength);
        ReadOnlySpan<byte> payload = frame.PayloadAndMeta[..^8];
        long time = BinaryPrimitives.ReadInt64LittleEndian(frame.PayloadAndMeta[^8..]);
        Assert.InRange(time, minimumTime, maximumTime);
        if (expectedTime.HasValue) { Assert.Equal(expectedTime.Value, time); }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.Equal(checked((uint)revisions.Count), count);
        Assert.Equal(4 + 12 * revisions.Count + root.Length, payload.Length);
        int index = 0;
        foreach (var (refId, member) in revisions.OrderBy(pair => pair.Key.LocalId)) {
            int start = 4 + 12 * index++;
            Assert.Equal(refId.LocalId, BinaryPrimitives.ReadUInt32LittleEndian(payload[start..]));
            Assert.Equal(member.Ticket.Packed, BinaryPrimitives.ReadUInt64LittleEndian(payload[(start + 4)..]));
        }
        Assert.Equal(root, payload[(4 + 12 * revisions.Count)..].ToArray());
    }

    public void Dispose() => _storage.Dispose();
}
