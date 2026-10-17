using System.Buffers.Binary;
using Atelia.Data;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Tags;

/// <summary>格式4的 canonical 成员表：递增所选 Head、实际 Entry 所属 Head 与完整 Packed SizedPtr。</summary>
internal static class TagEntriesCodec {
    internal const int MemberEncodedSize = sizeof(uint) + sizeof(uint) + sizeof(ulong);

    internal static byte[] Encode(ReadOnlySpan<HeadId> selectedHeads, ReadOnlySpan<HeadEntryId> entries, StoreIdentity identity) {
        if (identity == default) { throw new ArgumentException("A checked store identity is required.", nameof(identity)); }
        if (selectedHeads.Length != entries.Length) { throw new ArgumentException("Every selected Head requires one Entry.", nameof(entries)); }
        var measured = TagRecordCodec.MeasurePayloadLength(entries.Length, TagMetadataCodec.MinEncodedSize);
        if (measured.IsFailure) { throw new ArgumentOutOfRangeException(nameof(entries), "The member table exceeds the record capacity."); }
        uint previous = 0;
        for (int index = 0; index < entries.Length; index++) {
            HeadId selected = selectedHeads[index];
            HeadEntryId entry = entries[index];
            if (!selected.TryGetLocalId(identity, out _) || selected.GetLocalId(identity) <= previous
                || !entry.HeadId.TryGetLocalId(identity, out _) || entry.Ticket.Length == 0) {
                throw new ArgumentException("Selected Heads must decode in range and strictly increase; Entries must have in-range owners and complete tickets.", nameof(entries));
            }
            previous = selected.GetLocalId(identity);
        }
        byte[] payload = new byte[measured.Value];
        for (int index = 0; index < entries.Length; index++) {
            Span<byte> member = payload.AsSpan(index * MemberEncodedSize, MemberEncodedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(member, selectedHeads[index].GetLocalId(identity));
            BinaryPrimitives.WriteUInt32LittleEndian(member[sizeof(uint)..], entries[index].HeadId.GetLocalId(identity));
            BinaryPrimitives.WriteUInt64LittleEndian(member[(2 * sizeof(uint))..], entries[index].Ticket.Packed);
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
            uint ownerLocalId = BinaryPrimitives.ReadUInt32LittleEndian(member[sizeof(uint)..]);
            SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(member[(2 * sizeof(uint))..]));
            if (localId <= previous || localId > HeadStore.MaxLocalHeadId || ownerLocalId is 0 or > HeadStore.MaxLocalHeadId || ticket.Length == 0) { return false; }
            previous = localId;
        }

        var ownedEntries = new Dictionary<HeadId, HeadEntryId>(entryCount);
        for (int index = 0; index < entryCount; index++) {
            ReadOnlySpan<byte> member = payload.Slice(index * MemberEncodedSize, MemberEncodedSize);
            HeadId headId = HeadId.Create(identity, BinaryPrimitives.ReadUInt32LittleEndian(member));
            HeadId ownerId = HeadId.Create(identity, BinaryPrimitives.ReadUInt32LittleEndian(member[sizeof(uint)..]));
            SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(member[(2 * sizeof(uint))..]));
            ownedEntries.Add(headId, HeadEntryId.Create(ownerId, ticket));
        }
        entries = ownedEntries;
        return true;
    }
}
