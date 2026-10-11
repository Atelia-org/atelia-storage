using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Tests.Format;

namespace Atelia.VersionStore.Tests.Tags;

// Independent field assembly: only the prescribed public CRC and BPV writer are reused.
// Header and metadata goldens are fixed separately, and alternate BPV representations are assembled by hand.
internal static class TagTestWire {
    internal const string AnonymousAHex = "0000000000000000000341DE80262D";
    internal const string RelatedAHex = "000000000000000001BE040341694583C3";
    internal static StoreIdentity Identity => FormatTestValues.Identity();

    internal static byte[] Header(uint bucketId) {
        byte[] result = Convert.FromHexString("01000000" + FormatTestValues.IdentityHex + "00000000");
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

    internal static byte[] Metadata(
        string name, long time = 0, uint id = 0, SizedPtr? ticket = null,
        byte[]? nameWire = null, byte[]? idWire = null, byte[]? ticketWire = null
    ) {
        byte[] timeBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(timeBytes, time);
        return Seal(Join(timeBytes, idWire ?? VarUInt(id),
            id == 0 ? [] : ticketWire ?? VarUInt(ticket!.Value.Serialize()), nameWire ?? Name(name)));
    }

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
