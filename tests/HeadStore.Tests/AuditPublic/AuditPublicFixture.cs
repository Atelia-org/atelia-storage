using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.AuditPublic;

/// <summary>公开 VS 工厂及独立字段镜像；离线损坏不作为真实进程终止或设备故障证据。</summary>
internal sealed class AuditPublicFixture : IDisposable {
    internal TagPublicFixture Storage { get; } = new();
    internal string Root => Storage.Root;
    internal string Sandbox => Path.GetDirectoryName(Root)!;
    internal DataStore Data => Storage.Data;
    internal Store OpenReadOnly() => Storage.Open(readOnly: true);
    internal string HeadPath(uint id) => Storage.HeadPath(id);

    internal byte[] Header(uint id, uint sourceId = 0, SizedPtr sourceTicket = default) {
        byte[] bytes = new byte[sourceId == 0 ? 24 : 36];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        Storage.Identity.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), id);
        if (sourceId != 0) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), sourceId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(28), sourceTicket.Packed);
        }
        return bytes;
    }

    internal SizedPtr[] WriteHead(uint id, uint sourceId = 0, SizedPtr sourceTicket = default,
        params AuditHeadRecord[] records) {
        using IRbfFile file = RbfFile.CreateNew(HeadPath(id), RbfCacheMode.Off);
        file.Append(0, Header(id, sourceId, sourceTicket)).Unwrap();
        var tickets = new SizedPtr[records.Length];
        for (int index = 0; index < records.Length; index++) {
            AuditHeadRecord record = records[index];
            tickets[index] = file.Append(record.Tag, record.Payload, record.Metadata ?? new byte[8]).Unwrap();
        }
        file.DurableFlush();
        return tickets;
    }

    internal static byte[] Bytes(byte value = 0x51, int length = 128) => Enumerable.Repeat(value, length).ToArray();

    internal static void FlipPayload(string path, SizedPtr ticket) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + 4;
        int original = stream.ReadByte();
        Assert.True(original >= 0);
        stream.Position--;
        stream.WriteByte((byte)(original ^ 1));
    }

    internal static void MakeTombstone(string path, SizedPtr ticket) {
        byte[] frame = ReadFrameBytes(path, ticket);
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(frame.Length - 4));
        XorBody(frame, key);
        int trailer = frame.Length - 20;
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(trailer + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(trailer + 4), descriptor | 0x80000000u);
        RollingCrc.SealCodewordBackward(frame.AsSpan(trailer, 16));
        XorBody(frame, key);
        WriteFrameBytes(path, ticket, frame);
    }

    /// <summary>完整内帧与两侧 Fence 放进外帧物理 body，再封外层 CRC；仅为主链成员反例。</summary>
    internal SizedPtr EmbedReadableFrame(string path, SizedPtr outerTicket, byte[] payload) {
        string donor = Path.Combine(Sandbox, "embedded-donor.rbf");
        SizedPtr inner;
        using (IRbfFile file = RbfFile.CreateNew(donor, RbfCacheMode.Off)) {
            inner = file.Append(1, payload, new byte[8]).Unwrap();
            file.DurableFlush();
        }
        byte[] rawInner = File.ReadAllBytes(donor).AsSpan(checked((int)inner.Offset - 4), inner.Length + 8).ToArray();
        byte[] outer = ReadFrameBytes(path, outerTicket);
        uint outerKey = BinaryPrimitives.ReadUInt32LittleEndian(outer.AsSpan(outer.Length - 4));
        // First preserve the outer plain body. Install the desired encoded inner image at +8
        // by storing its XOR inverse in the outer plaintext. The true outer trailer is unchanged.
        XorBody(outer, outerKey);
        const int embeddedFenceOffset = 8;
        Assert.True(embeddedFenceOffset + rawInner.Length <= outer.Length - 32);
        rawInner.CopyTo(outer, embeddedFenceOffset);
        for (int offset = embeddedFenceOffset; offset < embeddedFenceOffset + rawInner.Length; offset += 4) {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(outer.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(outer.AsSpan(offset), value ^ outerKey);
        }
        RollingCrc.SealCodewordForward(outer.AsSpan(4, outer.Length - 24));
        XorBody(outer, outerKey);
        WriteFrameBytes(path, outerTicket, outer);
        return SizedPtr.Create(outerTicket.Offset + embeddedFenceOffset + 4, inner.Length);
    }

    private static byte[] ReadFrameBytes(string path, SizedPtr ticket) =>
        File.ReadAllBytes(path).AsSpan(checked((int)ticket.Offset), ticket.Length).ToArray();

    private static void WriteFrameBytes(string path, SizedPtr ticket, byte[] bytes) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = ticket.Offset;
        stream.Write(bytes);
    }

    private static void XorBody(byte[] frame, uint key) {
        for (int offset = 4; offset < frame.Length - 4; offset += 4) {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), value ^ key);
        }
    }

    // Capture both VS and borrowed data. Empty permanent lock files need no conflicting read handle.
    internal Dictionary<string, byte[]?> CaptureFiles() {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(Sandbox, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(Sandbox, directory) + Path.DirectorySeparatorChar, null);
        }
        foreach (string file in Directory.EnumerateFiles(Sandbox, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(Sandbox, file), new FileInfo(file).Length == 0 ? [] : File.ReadAllBytes(file));
        }
        return result;
    }

    internal void AssertUnchanged(Dictionary<string, byte[]?> before) {
        var after = CaptureFiles();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); }
    }

    internal static void AssertCounts(HeadStoreAudit audit, long refs, long entries, long forks,
        long buckets = 0, long tags = 0) {
        Assert.Equal(refs, audit.HeadCount);
        Assert.Equal(entries, audit.EntryCount);
        Assert.Equal(forks, audit.OriginCount);
        Assert.Equal(buckets, audit.TagBucketCount);
        Assert.Equal(tags, audit.TagCount);
    }

    public void Dispose() => Storage.Dispose();
}

internal sealed record AuditHeadRecord(byte[] Payload, uint Tag = 1, byte[]? Metadata = null);
