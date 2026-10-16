using System.Buffers;
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.Public;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.TagPublic;

/// <summary>W: 优先的真实公开工厂；手工镜像只证明损坏/恢复边界，不是进程终止证据。</summary>
internal sealed class TagPublicFixture : IDisposable {
    private readonly PublicHeadStoreFixture _public = new();
    internal string Root => _public.Root;
    internal string DataRoot => _public.DataRoot;
    internal string TagsPath => Path.Combine(Root, "tags");
    internal string PendingPath => _public.PendingPath;
    internal DataStore Data => _public.Data;
    internal byte[] Identity { get; }
    internal static int InitializedLength => checked((int)(RbfScanBoundary.Empty.EndExclusive
        + RbfFile.MeasureWriteSize(24, 0).Unwrap().AppendLength));

    internal TagPublicFixture() {
        using var store = Store.Create(Root, Data, 256);
        Identity = store.HeadStoreId.ToArray();
    }

    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal string BucketPath(string name) => Path.Combine(TagsPath, $"{Bucket(name):x8}.rbf");
    internal string HeadPath(uint localId) => _public.HeadPath(localId);
    internal Dictionary<string, byte[]?> CaptureFiles() => _public.CaptureFiles();
    internal void AssertUnchanged(Dictionary<string, byte[]?> before) => _public.AssertUnchanged(before);

    // Independent published format assembly: public VSID + public BPV/CRC/RBF only.
    internal byte[] Header(string name) {
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        Identity.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), Bucket(name));
        return bytes;
    }

    internal SizedPtr[] WriteMirror(string name, params TagPublicRecord[] records) =>
        WriteMirrorHeader(name, Header(name), records);

    internal SizedPtr[] WriteMirrorHeader(string name, byte[] header, params TagPublicRecord[] records) {
        Directory.CreateDirectory(TagsPath);
        using IRbfFile file = RbfFile.CreateNew(BucketPath(name), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        var tickets = new SizedPtr[records.Length];
        for (int index = 0; index < records.Length; index++) {
            TagPublicRecord record = records[index];
            tickets[index] = file.Append(record.Tag, record.Root, record.Metadata ?? Metadata(record.Name)).Unwrap();
        }
        file.DurableFlush();
        return tickets;
    }

    internal static byte[] Metadata(string name, long time = 0, int entryCount = 1) =>
        Tags.TagTestWire.Metadata(name, time, checked((uint)entryCount));

    internal static byte[] Members(uint localId = 1, SizedPtr? ticket = null) => Tags.TagTestWire.Members(localId, ticket);

    internal HeadId SeedHead() {
        using var store = Open();
        return store.CreateHead(1, [0x41], out _).Unwrap().HeadId;
    }

    internal static uint Bucket(string name) {
        ulong hash = 14695981039346656037;
        unchecked {
            foreach (char codeUnit in name) {
                hash = (hash ^ (byte)codeUnit) * 1099511628211;
                hash = (hash ^ (byte)(codeUnit >> 8)) * 1099511628211;
            }
        }
        return (uint)((hash ^ (hash >> 10)) & 1023);
    }

    internal static string Collision(string name, params string[] excluded) {
        for (int suffix = 0; suffix < 100_000; suffix++) {
            string candidate = $"public-collision-{suffix}";
            if (candidate != name && !excluded.Contains(candidate, StringComparer.Ordinal) && Bucket(candidate) == Bucket(name)) {
                return candidate;
            }
        }
        throw new InvalidOperationException("Could not construct a bounded collision vector.");
    }

    internal void FlipRoot(string name, SizedPtr ticket) {
        using var stream = new FileStream(BucketPath(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + 4; // Publicly written RBF3 payload's first physical byte.
        int original = stream.ReadByte();
        Assert.True(original >= 0);
        stream.Position--;
        stream.WriteByte((byte)(original ^ 1));
    }

    internal void AssertBucketReleased(string name) {
        using var exclusive = new FileStream(BucketPath(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => _public.Dispose();
}

internal sealed record TagPublicRecord(string Name, byte[] Root, byte[]? Metadata = null, uint Tag = 1);
