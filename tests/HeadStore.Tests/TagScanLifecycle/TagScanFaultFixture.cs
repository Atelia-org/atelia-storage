using System.Buffers.Binary;
using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Platform;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.ForkQueryFaults;
using Atelia.HeadStore.Tests.Runtime;
using Atelia.HeadStore.Tests.Storage;
using Atelia.HeadStore.Tests.TagPublic;
using Atelia.HeadStore.Tests.TagPublicationFaults;
using Xunit;
using Microsoft.Win32.SafeHandles;

namespace Atelia.HeadStore.Tests.TagScanLifecycle;

/// <summary>真实公开创建的根、生产 tag backend、公共 RBF；复用窄枚举/读取/关闭故障接缝。</summary>
internal sealed class TagScanFaultFixture : IDisposable {
    internal const string Name = "tag-48";
    internal const string SameBucketName = "tag-65";
    internal const string OtherBucketName = "Release";
    internal readonly StorageFixture Storage = new();
    internal readonly List<string> Events = [];
    internal readonly StoreIdentity Identity;
    internal readonly TestHeadStoreFiles Heads;
    internal readonly ForkQueryOperations Operations;
    internal readonly TagPublicationFiles Tags;
    internal readonly ForkQueryLock Lock;
    internal readonly HeadStore Store;

    internal TagScanFaultFixture(bool readOnly = false) {
        byte[] identity = Storage.Create();
        if (!StoreIdentity.TryRead(identity, out Identity)
            || !StoreIdentity.TryRead(Storage.Data.StoreId, out StoreIdentity dataIdentity)) {
            throw new InvalidOperationException("Invalid fixture identity.");
        }
        Heads = new TestHeadStoreFiles(Events, Storage.CreatingPath);
        Operations = new ForkQueryOperations(Events);
        Tags = new TagPublicationFiles(new DirectoryHeadStoreFiles(Storage.Root, Operations), Storage.BasePath, Events);
        Lock = new ForkQueryLock(HeadStorePlatform.AcquireOwnerLock(Storage.Root, create: false, readOnly), Events);
        var hooks = new HeadStoreTestHooks {
            ConfirmDataDurable = () => { Events.Add("data:confirm"); Storage.Data.ConfirmDurable(); }
        };
        Store = new HeadStore(Storage.Data, Identity, dataIdentity, 17, readOnly, Heads, Lock, hooks, tagFiles: Tags);
    }

    internal string BucketPath(string name) => Path.Combine(Storage.Root, "tags", $"{TagPublicFixture.Bucket(name):x8}.rbf");

    internal TagBucketSeed SeedBucket(string name = Name, params TagPublicRecord[] records) {
        Directory.CreateDirectory(Path.GetDirectoryName(BucketPath(name))!);
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 4);
        Identity.TryWrite(header.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), TagPublicFixture.Bucket(name));
        using IRbfFile file = RbfFile.CreateNew(BucketPath(name), RbfCacheMode.Off);
        SizedPtr headerTicket = file.Append(0, header).Unwrap();
        var tickets = new SizedPtr[records.Length];
        var physicalRecords = new TagFaults.TagFaultRecord[records.Length];
        for (int index = 0; index < records.Length; index++) {
            TagPublicRecord record = records[index];
            byte[] meta = record.Metadata ?? TagPublicFixture.Metadata(record.Name);
            tickets[index] = file.Append(record.Tag, record.Root, meta).Unwrap();
            physicalRecords[index] = new TagFaults.TagFaultRecord(record.Root, meta, record.Tag);
        }
        file.DurableFlush();
        return new TagBucketSeed(headerTicket, tickets, physicalRecords);
    }

    internal void Order(params string[] names) => Operations.EntryPaths = names.Select(BucketPath).ToArray();

    internal static void TruncateOwnedBucket(TagPublicationFile file, long length) {
        // A controlled same-handle mutation at the read observer; no second writer bypasses sharing.
        var handle = file.Inner.GetType().GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(file.Inner) as SafeFileHandle ?? throw new InvalidOperationException("Missing owned RBF handle.");
        RandomAccess.SetLength(handle, length);
    }

    internal void FlipClosedBucketByte(string name, long offset) {
        using SafeFileHandle handle = File.OpenHandle(BucketPath(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> value = stackalloc byte[1];
        if (RandomAccess.Read(handle, value, offset) != 1) { throw new InvalidOperationException("Missing test byte."); }
        value[0] ^= 1;
        RandomAccess.Write(handle, value, offset);
    }

    internal HeadId SeedHead(uint localId = 1, bool twoEntries = false) {
        Storage.SeedFormal(localId);
        Heads.Paths.Add(localId, Storage.HeadPath(localId));
        if (twoEntries) {
            using IRbfFile file = RbfFile.OpenExisting(Storage.HeadPath(localId), out _, RbfCacheMode.Off);
            byte[] meta = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(meta, 124);
            file.Append(1, [2], meta).Unwrap();
            file.DurableFlush();
        }
        return HeadId.Create(Identity, localId);
    }

    internal void AssertFaultedAndCleaned() {
        Assert.Throws<InvalidOperationException>(() => Store.ScanTags(_ => true));
        Assert.All(Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(Heads.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(Operations.Enumerators, cursor => Assert.Equal(1, cursor.DisposeCalls));
        Assert.Equal(1, Lock.DisposeCalls);
        Assert.Equal("lock:close", Events[^1]);
        Store.Dispose();
        Store.Dispose();
        Assert.Equal(1, Lock.DisposeCalls);
        Storage.Data.EnsureUsable();
    }

    public void Dispose() {
        try { Store.Dispose(); }
        finally { Storage.Dispose(); }
    }
}
