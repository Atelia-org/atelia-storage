using System.Buffers.Binary;
using Atelia.Data;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Tags;

/// <summary>格式2的 canonical 成员表：递增非零 LocalHeadId 与完整 Packed SizedPtr。</summary>
internal static class TagEntriesCodec {
    internal const int MemberEncodedSize = sizeof(uint) + sizeof(ulong);

    internal static byte[] Encode(ReadOnlySpan<HeadEntryId> entries, StoreIdentity identity) {
        if (identity == default) { throw new ArgumentException("A checked store identity is required.", nameof(identity)); }
        var measured = TagRecordCodec.MeasurePayloadLength(entries.Length, TagMetadataCodec.MinEncodedSize);
        if (measured.IsFailure) { throw new ArgumentOutOfRangeException(nameof(entries), "The member table exceeds the record capacity."); }
        uint previous = 0;
        foreach (HeadEntryId entry in entries) {
            if (!entry.HeadId.BelongsTo(identity) || entry.HeadId.LocalId <= previous || entry.Ticket.Length == 0) {
                throw new ArgumentException("Entries must be same-store, strictly increasing, and have nonempty complete tickets.", nameof(entries));
            }
            previous = entry.HeadId.LocalId;
        }
        byte[] payload = new byte[measured.Value];
        for (int index = 0; index < entries.Length; index++) {
            Span<byte> member = payload.AsSpan(index * MemberEncodedSize, MemberEncodedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(member, entries[index].HeadId.LocalId);
            BinaryPrimitives.WriteUInt64LittleEndian(member[sizeof(uint)..], entries[index].Ticket.Packed);
        }
        return payload;
    }

    internal static bool TryRead(ReadOnlySpan<byte> payload, StoreIdentity identity, int entryCount,
        out Dictionary<HeadId, HeadEntryId> entries) {
        entries = null!;
        if (identity == default || entryCount <= 0
            || !TagRecordCodec.IsValidEntryCount((uint)entryCount, TagMetadataCodec.MinEncodedSize)
            || payload.Length != checked((long)MemberEncodedSize * entryCount)) { return false; }

        // 完整结构校验在集合分配之前；不能按未检查的持久 count 分配。
        uint previous = 0;
        for (int index = 0; index < entryCount; index++) {
            ReadOnlySpan<byte> member = payload.Slice(index * MemberEncodedSize, MemberEncodedSize);
            uint localId = BinaryPrimitives.ReadUInt32LittleEndian(member);
            SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(member[sizeof(uint)..]));
            if (localId <= previous || ticket.Length == 0) { return false; }
            previous = localId;
        }

        var ownedEntries = new Dictionary<HeadId, HeadEntryId>(entryCount);
        for (int index = 0; index < entryCount; index++) {
            ReadOnlySpan<byte> member = payload.Slice(index * MemberEncodedSize, MemberEncodedSize);
            HeadId headId = HeadId.Create(identity, BinaryPrimitives.ReadUInt32LittleEndian(member));
            SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(member[sizeof(uint)..]));
            ownedEntries.Add(headId, HeadEntryId.Create(headId, ticket));
        }
        entries = ownedEntries;
        return true;
    }
}
