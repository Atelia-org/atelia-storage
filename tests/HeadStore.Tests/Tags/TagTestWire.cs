using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Tests.Format;

namespace Atelia.HeadStore.Tests.Tags;

// Independent field assembly: only the prescribed public CRC and BPV writer are reused.
// Header and metadata goldens are fixed separately, and alternate BPV representations are assembled by hand.
internal static class TagTestWire {
    internal static StoreIdentity Identity => FormatTestValues.Identity();

    internal static byte[] Header(uint bucketId) {
        byte[] result = Convert.FromHexString("04000000" + FormatTestValues.IdentityHex + "00000000");
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), bucketId);
        return result;
    }

    internal static byte[] Name(string value) {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(output);
        StringEncodingPlan plan = BareValueEncoding.PrepareString(value);
        writer.WriteString(in plan);
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] Utf16Name(string value, int headerWidth = 0) {
        byte[] payload = new byte[value.Length * 2];
        for (int i = 0; i < value.Length; i++) {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 2), value[i]);
        }
        return Join(VarUInt((ulong)payload.Length, headerWidth), payload);
    }

    internal static byte[] Utf8Name(string value, int headerWidth = 0) {
        byte[] payload = new UTF8Encoding(false, true).GetBytes(value);
        return Join(VarUInt((ulong)payload.Length * 2 + 1, headerWidth), payload);
    }

    internal static byte[] VarUInt(ulong value, int width = 0) {
        var result = new List<byte>();
        do {
            byte next = (byte)(value & 127);
            value >>= 7;
            result.Add(value == 0 ? next : (byte)(next | 128));
        }
        while (value != 0);
        while (result.Count < width) {
            result[^1] |= 128;
            result.Add(0);
        }
        return result.ToArray();
    }

    internal static byte[] Metadata(string name, long time = 0, uint count = 1, byte[]? nameWire = null) {
        byte[] fields = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(fields, time);
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(8), count);
        return Seal(Join(fields, nameWire ?? Name(name)));
    }

    internal static byte[] Members(uint localId = 1, SizedPtr? ticket = null, uint? ownerLocalId = null) {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, localId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ownerLocalId ?? localId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ticket ?? SizedPtr.Create(60, 40)).Packed);
        return bytes;
    }

    internal static byte[] MemberBytes(this HeadSetTag tag, StoreIdentity? identity = null) => Join(tag.Entries
        .OrderBy(pair => pair.Key.GetLocalId(identity ?? Identity)).Select(pair => Members(pair.Key.GetLocalId(identity ?? Identity), pair.Value.Ticket, pair.Value.HeadId.GetLocalId(identity ?? Identity))).ToArray());

    internal static byte[] MemberBytes(this HeadSetTag tag, byte[] identityBytes) {
        if (!StoreIdentity.TryRead(identityBytes, out StoreIdentity identity)) {
            throw new ArgumentException("Invalid fixture identity.", nameof(identityBytes));
        }
        return tag.MemberBytes(identity);
    }

    internal static byte[] MemberSequence(int count) => Join(Enumerable.Range(1, count).Select(id => Members((uint)id)).ToArray());

    internal static byte[] Seal(ReadOnlySpan<byte> prefix) {
        byte[] result = new byte[prefix.Length + 4];
        prefix.CopyTo(result);
        RollingCrc.SealCodewordForward(result);
        return result;
    }

    internal static byte[] Join(params byte[][] values) {
        byte[] result = new byte[values.Sum(value => value.Length)];
        int cursor = 0;
        foreach (byte[] value in values) {
            value.CopyTo(result, cursor);
            cursor += value.Length;
        }
        return result;
    }
}
