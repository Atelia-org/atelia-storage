using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Tests.History;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdateIntegration;

/// <summary>独立固定宽 wire 镜像；只声明受控坐标，不作为公开 mutation 签发 revision 的证据。</summary>
internal static class ConditionalIntegrationRecords {
    internal static Dictionary<uint, RefRevision> AppendGroup(HistoryFixture fixture,
        IReadOnlyDictionary<uint, byte[]> roots, params uint[] completedMembers) {
        var tickets = new Dictionary<uint, SizedPtr>();
        foreach (var pair in roots) {
            using IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(pair.Key), RbfCacheMode.Off);
            int payloadLength = checked(sizeof(uint) + 12 * roots.Count + pair.Value.Length);
            tickets.Add(pair.Key, SizedPtr.Create(file.TailOffset,
                RbfFile.MeasureWriteSize(payloadLength, sizeof(long)).Unwrap().FrameLength));
        }
        foreach (uint localId in completedMembers) {
            byte[] payload = Encode(tickets, roots[localId]);
            using IRbfFile file = RbfFile.OpenExisting(fixture.RefPath(localId), out _, RbfCacheMode.Off);
            Assert.Equal(tickets[localId], file.Append(2, payload, Metadata(-123)).Unwrap());
            file.DurableFlush();
        }
        return tickets.ToDictionary(pair => pair.Key, pair => RefRevision.Create(fixture.Id(pair.Key), pair.Value));
    }

    internal static byte[] Encode(IReadOnlyDictionary<uint, SizedPtr> members, byte[] root) {
        byte[] payload = new byte[checked(sizeof(uint) + 12 * members.Count + root.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)members.Count));
        int offset = sizeof(uint);
        foreach (var pair in members.OrderBy(pair => pair.Key)) {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), pair.Key);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(offset + sizeof(uint)), pair.Value.Packed);
            offset += 12;
        }
        root.CopyTo(payload, offset);
        return payload;
    }

    internal static byte[] Metadata(long time) {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, time);
        return bytes;
    }

    internal static RefRevision WriteCompleteGroupWithForbiddenCuInitial(HistoryFixture fixture, uint initialId, uint peerId) {
        const int payloadLength = sizeof(uint) + 2 * 12 + 1;
        int frameLength = RbfFile.MeasureWriteSize(payloadLength, sizeof(long)).Unwrap().FrameLength;
        long initialOffset = RbfScanBoundary.Empty.EndExclusive
            + RbfFile.MeasureWriteSize(RefHeaderCodec.NoOriginPayloadSize, 0).Unwrap().AppendLength;
        SizedPtr initialTicket = SizedPtr.Create(initialOffset, frameLength);
        SizedPtr peerTicket;
        using (IRbfFile peer = RbfFile.OpenReadOnlyExisting(fixture.RefPath(peerId), RbfCacheMode.Off)) {
            peerTicket = SizedPtr.Create(peer.TailOffset, frameLength);
        }
        var members = new Dictionary<uint, SizedPtr> { [initialId] = initialTicket, [peerId] = peerTicket };
        RefRevision initial = fixture.WriteMirror(initialId, null,
            new HistoryRecord(Encode(members, [0x31]), Tag: 2))[0];
        Assert.Equal(initialTicket, initial.Ticket);
        using (IRbfFile peer = RbfFile.OpenExisting(fixture.RefPath(peerId), out _, RbfCacheMode.Off)) {
            Assert.Equal(peerTicket, peer.Append(2, Encode(members, [0x21]), Metadata(0)).Unwrap());
            peer.DurableFlush();
        }
        return initial;
    }
}
