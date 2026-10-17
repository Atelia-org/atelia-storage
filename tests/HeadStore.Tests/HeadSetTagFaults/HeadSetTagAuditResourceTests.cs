using Atelia.Data;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Platform;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.AuditFaults;
using Atelia.HeadStore.Tests.ConditionalUpdateFaults;
using Atelia.HeadStore.Tests.ForkQueryFaults;
using Atelia.HeadStore.Tests.Public;
using Xunit;

namespace Atelia.HeadStore.Tests.HeadSetTagFaults;

public sealed class HeadSetTagAuditResourceTests {
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void CancellationDuringTagMemberOrPeerReadCleansTemporaryFilesAndPreservesOwner(uint cancelId) {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        bool tagPhase = false;
        bool cancelledAtContent = false;
        f.Files.ConfigureOpen = file => {
            if (file.IsTag) { tagPhase = true; }
            if (!tagPhase || file.IsTag || file.Id != cancelId) { return; }
            SizedPtr target = f.Published[file.Id].Ticket;
            file.BeforeRead = (offset, length, raw) => {
                if (!raw && offset == target.Offset && length == target.Length) {
                    cancelledAtContent = true;
                    cancellation.Cancel();
                }
            };
        };

        var error = Assert.Throws<OperationCanceledException>(() => f.Store.Audit(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(cancelledAtContent);
        Assert.Equal(1, f.Files.ActiveFiles); // Only the deliberately pre-cached Head remains.
        Assert.All(f.Files.Opened.Where(file => !ReferenceEquals(file, f.Cached)), file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(0, f.Cached.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(new uint[] { 3 }, ConditionalUpdateFaultFixture.OwnedHeadIds(f.Store));
        f.Files.ConfigureOpen = null;
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Fact]
    public void CancellationStaysPrimaryWhenNestedPeerMemberBucketCursorCachedFileAndRootLockClosesFail() {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var peerClose = new IOException("tag member CU peer close");
        var memberClose = new IOException("tag member close");
        var bucketClose = new IOException("tag bucket close");
        var cursorClose = new IOException("tag directory cursor close");
        var cachedClose = new IOException("cached Head close");
        var lockClose = new IOException("root lock close");
        bool tagPhase = false;
        bool cancelledAtPeerContent = false;
        f.Cached.DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Operations.ConfigureCursor = (isTag, cursor) => {
            if (isTag) { cursor.DisposeFailure = cursorClose; }
        };
        f.Files.ConfigureOpen = file => {
            if (file.IsTag) { tagPhase = true; file.DisposeFailure = bucketClose; return; }
            if (!tagPhase) { return; }
            if (file.Id == 1) { file.DisposeFailure = memberClose; }
            if (file.Id != 2) { return; }
            file.DisposeFailure = peerClose;
            file.BeforeRead = (offset, length, raw) => {
                SizedPtr target = f.Published[2].Ticket;
                if (!raw && offset == target.Offset && length == target.Length) {
                    cancelledAtPeerContent = true;
                    cancellation.Cancel();
                }
            };
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.Audit(cancellation.Token));

        Exception[] errors = AuditFaultFixture.Flatten(observed);
        Assert.True(cancelledAtPeerContent);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(errors[0]).CancellationToken);
        Assert.Contains(peerClose, errors);
        Assert.Contains(memberClose, errors);
        Assert.Contains(bucketClose, errors);
        Assert.Contains(cursorClose, errors);
        Assert.Contains(cachedClose, errors);
        Assert.Same(lockClose, errors[^1]);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Events[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.Audit());
        f.AssertNoWrites();
        f.Data.EnsureUsable();
    }

    [Fact]
    public void CompleteAuditChecksTagCuWithTemporarySlotsWithoutCountingMembersTwiceOrGrowingCache() {
        using var f = new Fixture();

        for (int iteration = 0; iteration < 2; iteration++) {
            var result = f.Store.Audit().Unwrap();
            Assert.Equal(3, result.HeadCount);
            Assert.Equal(5, result.EntryCount);
            Assert.Equal(1, result.TagCount);
            Assert.Equal(1, result.TagBucketCount);
            Assert.Equal(new uint[] { 3 }, ConditionalUpdateFaultFixture.OwnedHeadIds(f.Store));
            Assert.Equal(1, f.Files.ActiveFiles);
            Assert.Equal(0, f.Cached.DisposeCalls);
        }

        Assert.InRange(f.Files.MaximumActiveFiles, 3, 4);
        Assert.All(f.Files.Opened.Where(file => !ReferenceEquals(file, f.Cached)), file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        f.AssertNoWrites();
    }

    /// <summary>生产 public API 准备 CU/tag；只读审计复用既有真实 reader 观察代理。</summary>
    private sealed class Fixture : IDisposable {
        private readonly PublicHeadStoreFixture _public = new();
        internal readonly List<string> Events = [];
        internal readonly Dictionary<uint, HeadEntryId> Published = [];
        internal readonly AuditOperations Operations;
        internal readonly AuditFiles Files;
        internal readonly ForkQueryLock Lock;
        internal readonly HeadStore Store;
        internal readonly AuditFile Cached;
        internal Atelia.FrameStore.FrameStore Data => _public.Data;

        internal Fixture() {
            StoreIdentity identity;
            using (var writer = HeadStore.Create(_public.Root, Data, 256)) {
                StoreIdentity.TryRead(writer.HeadStoreId, out identity);
                HeadEntryId a = writer.CreateHead(1, [0x11], out _).Unwrap();
                HeadEntryId b = writer.CreateHead(2, [0x22], out _).Unwrap();
                writer.CreateHead(3, [0x33], out _).Unwrap();
                var changes = new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                    [a.HeadId] = new byte[] { 0xa1 }, [b.HeadId] = new byte[] { 0xb1 }
                };
                foreach (var pair in writer.PublishHeads(changes, out _).Unwrap()) { Published.Add(pair.Key.GetLocalId(identity), pair.Value); }
                writer.CreateTag("audited-cu", [a.HeadId], out _).Unwrap();
            }
            Assert.True(StoreIdentity.TryRead(Data.StoreId, out var dataIdentity));
            Operations = new AuditOperations(Events);
            Files = new AuditFiles(new DirectoryHeadStoreFiles(_public.Root, Operations), _public.Sandbox, Events);
            Lock = new ForkQueryLock(HeadStorePlatform.AcquireOwnerLock(_public.Root, create: false, readOnly: true), Events);
            Store = new HeadStore(Data, identity, dataIdentity, 256, true, Files, Lock, tagFiles: Files);
            Store.ReadHead(3).Unwrap();
            Cached = Assert.Single(Files.Opened);
        }

        internal void AssertNoWrites() {
            Assert.Equal(0, Files.MutationCalls);
            Assert.Equal(0, Operations.WritableTagOpens);
            Assert.All(Files.Opened, file => {
                Assert.True(file.ReadOnly);
                Assert.Equal(0, file.MutationCalls);
            });
        }

        public void Dispose() {
            try { Store.Dispose(); }
            finally { _public.Dispose(); }
        }
    }
}
