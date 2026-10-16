using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.Public;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.ConditionalUpdatePublic;

/// <summary>真实公开工厂与独立 CU envelope；不复制 RBF framing/CRC，也不作为进程终止证据。</summary>
internal sealed class ConditionalUpdatePublicFixture : IDisposable {
    private readonly PublicHeadStoreFixture _storage = new();
    internal string Root => _storage.Root;
    internal string DataRoot => _storage.DataRoot;
    internal DataStore Data => _storage.Data;
    internal static HeadHistoryBudget Unlimited => new(long.MaxValue, long.MaxValue);
    internal Store Create(uint reservation = 256) => Store.Create(Root, Data, reservation);
    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal string HeadPath(uint localId) => _storage.HeadPath(localId);
    internal Dictionary<string, byte[]?> CaptureFiles() => _storage.CaptureFiles();
    internal void AssertUnchanged(Dictionary<string, byte[]?> before) => _storage.AssertUnchanged(before);

    internal static List<HeadEntry> History(Store store, HeadId headId) {
        var entries = new List<HeadEntry>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(headId, Unlimited, entry => {
            entries.Add(entry);
            return true;
        }).Unwrap());
        return entries;
    }

    internal static IReadOnlyDictionary<HeadId, ReadOnlyMemory<byte>> Changes(params (HeadId Head, byte[] Bytes)[] entries) =>
        entries.ToDictionary(entry => entry.Head, entry => (ReadOnlyMemory<byte>)entry.Bytes);

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
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(HeadPath(localId), RbfCacheMode.Off);
        int length = RbfFile.MeasureWriteSize(checked(4 + 12 * memberCount + rootLength), 8).Unwrap().FrameLength;
        return SizedPtr.Create(file.TailOffset, length);
    }

    internal SizedPtr AppendEnvelope(uint localId, byte[] payload, byte[]? metadata = null) {
        using IRbfFile file = RbfFile.OpenExisting(HeadPath(localId), out _, RbfCacheMode.Off);
        SizedPtr ticket = file.Append(2, payload, metadata ?? new byte[8]).Unwrap();
        file.DurableFlush();
        return ticket;
    }

    internal void AssertWire(HeadEntryId entryId, IReadOnlyDictionary<HeadId, HeadEntryId> entryIds,
        byte[] root, long minimumTime, long maximumTime, long? expectedTime = null) {
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(HeadPath(entryId.HeadId.LocalId), RbfCacheMode.Off);
        using RbfPooledFrame frame = file.ReadPooledFrame(entryId.Ticket).Unwrap();
        Assert.Equal(entryId.Ticket, frame.Ticket);
        Assert.Equal(2u, frame.Tag);
        Assert.False(frame.IsTombstone);
        Assert.Equal(8, frame.TailMetaLength);
        ReadOnlySpan<byte> payload = frame.PayloadAndMeta[..^8];
        long time = BinaryPrimitives.ReadInt64LittleEndian(frame.PayloadAndMeta[^8..]);
        Assert.InRange(time, minimumTime, maximumTime);
        if (expectedTime.HasValue) { Assert.Equal(expectedTime.Value, time); }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.Equal(checked((uint)entryIds.Count), count);
        Assert.Equal(4 + 12 * entryIds.Count + root.Length, payload.Length);
        int index = 0;
        foreach (var (headId, member) in entryIds.OrderBy(pair => pair.Key.LocalId)) {
            int start = 4 + 12 * index++;
            Assert.Equal(headId.LocalId, BinaryPrimitives.ReadUInt32LittleEndian(payload[start..]));
            Assert.Equal(member.Ticket.Packed, BinaryPrimitives.ReadUInt64LittleEndian(payload[(start + 4)..]));
        }
        Assert.Equal(root, payload[(4 + 12 * entryIds.Count)..].ToArray());
    }

    public void Dispose() => _storage.Dispose();
}
