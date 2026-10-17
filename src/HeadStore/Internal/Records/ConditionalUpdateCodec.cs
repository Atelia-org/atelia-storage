using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Records;

internal readonly record struct ConditionalUpdateMember(uint LocalHeadId, SizedPtr Ticket);

/// <summary>CU 的 canonical 固定宽成员表与 opaque HeadEntry payload bytes 边界；不读取 peer 或证明发布。</summary>
internal static class ConditionalUpdateCodec {
    internal const int CountSize = sizeof(uint);
    internal const int MemberSize = sizeof(uint) + sizeof(ulong);
    internal const int MinimumPayloadLength = CountSize + 2 * MemberSize + 1;
    internal const int MaxPayloadLength = RbfFile.MaxPayloadAndMetaLength - RecordMetadataCodec.EncodedSize;

    private static readonly long s_minimumPublicationOffset = RbfScanBoundary.Empty.EndExclusive +
        RbfFile.MeasureWriteSize(HeadHeaderCodec.NoOriginPayloadSize, 0).Value.AppendLength;

    internal static bool TryRead(ReadOnlySpan<byte> payload, uint hostLocalId,
        SizedPtr selfTicket, out int rootOffset) {
        rootOffset = default;
        if (hostLocalId is 0 or > HeadStore.MaxLocalHeadId || payload.Length < MinimumPayloadLength || payload.Length > MaxPayloadLength) {
            return false;
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        long tableLength = CountSize + (long)MemberSize * count;
        // Bound the complete table before any per-member access, reserving a nonempty root.
        if (count < 2 || tableLength >= payload.Length
            || payload.Length - tableLength > HeadStore.MaxHeadEntryPayloadLength) {
            return false;
        }
        int tableEnd = (int)tableLength;
        GetMemberLengthBounds(tableEnd, out int minimumLength, out int maximumLength);
        uint previous = 0;
        bool foundSelf = false;
        for (int index = 0; index < (int)count; index++) {
            GetMember(payload, index, out uint localId, out SizedPtr ticket);
            if (localId <= previous || localId > HeadStore.MaxLocalHeadId || !IsValidMemberTicket(ticket, minimumLength, maximumLength)) { return false; }
            previous = localId;
            if (localId == hostLocalId) {
                if (ticket != selfTicket) { return false; }
                foundSelf = true;
            }
        }
        var measured = RbfFile.MeasureWriteSize(payload.Length, RecordMetadataCodec.EncodedSize);
        if (!foundSelf || measured.IsFailure || selfTicket.Length != measured.Value.FrameLength) { return false; }
        rootOffset = tableEnd;
        return true;
    }

    // The following accessors consume an already checked payload; no member collection is allocated.
    internal static int GetMemberCount(ReadOnlySpan<byte> payload) =>
        checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload));

    internal static void GetMember(ReadOnlySpan<byte> payload, int index, out uint localId, out SizedPtr ticket) {
        int offset = checked(CountSize + MemberSize * index);
        localId = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(payload[(offset + sizeof(uint))..]));
    }

    internal static bool MembersEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
        int leftCount = GetMemberCount(left);
        int rightCount = GetMemberCount(right);
        if (leftCount != rightCount) { return false; }
        int length = checked(CountSize + MemberSize * leftCount);
        return left[..length].SequenceEqual(right[..length]);
    }

    internal static byte[] EncodeMembers(ReadOnlySpan<ConditionalUpdateMember> members) {
        long tableLength = CountSize + (long)MemberSize * members.Length;
        if (members.Length < 2 || tableLength >= MaxPayloadLength) {
            throw new ArgumentException("A CU requires a bounded table with at least two members.", nameof(members));
        }
        GetMemberLengthBounds((int)tableLength, out int minimumLength, out int maximumLength);
        uint previous = 0;
        foreach (ConditionalUpdateMember member in members) {
            if (member.LocalHeadId <= previous || member.LocalHeadId > HeadStore.MaxLocalHeadId || !IsValidMemberTicket(member.Ticket, minimumLength, maximumLength)) {
                throw new ArgumentException("CU members must have strictly increasing nonzero local identifiers and valid tickets.", nameof(members));
            }
            previous = member.LocalHeadId;
        }
        byte[] encoded = new byte[(int)tableLength];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, (uint)members.Length);
        for (int index = 0; index < members.Length; index++) {
            int offset = CountSize + MemberSize * index;
            BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(offset), members[index].LocalHeadId);
            BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(offset + sizeof(uint)), members[index].Ticket.Packed);
        }
        return encoded;
    }

    private static void GetMemberLengthBounds(int tableLength, out int minimumLength, out int maximumLength) {
        minimumLength = RbfFile.MeasureWriteSize(tableLength + 1, RecordMetadataCodec.EncodedSize).Value.FrameLength;
        int maximumPayloadLength = (int)Math.Min((long)tableLength + HeadStore.MaxHeadEntryPayloadLength, MaxPayloadLength);
        maximumLength = RbfFile.MeasureWriteSize(maximumPayloadLength, RecordMetadataCodec.EncodedSize).Value.FrameLength;
    }

    private static bool IsValidMemberTicket(SizedPtr ticket, int minimumLength, int maximumLength) =>
        ticket.Offset >= s_minimumPublicationOffset && ticket.Length >= minimumLength
        && ticket.Length <= maximumLength;
}
