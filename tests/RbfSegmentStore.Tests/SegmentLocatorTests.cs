using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Xunit;

namespace Atelia.RbfSegmentStore.Tests;

public sealed class SegmentLocatorTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "t02-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    [Theory]
    [InlineData(RbfSegmentStoreLayout.Bucketed, "52425341010014000000000001000000cf6cdc28")]
    [InlineData(RbfSegmentStoreLayout.Flat, "52425341010014000100000001000000e811e061")]
    public void CodecAndDurableCreateMatchFrozenVector(RbfSegmentStoreLayout layout, string hex) {
        using (RbfSegmentStore.CreateNew(_root, new() { NewStoreLayout = layout })) { }
        Assert.Equal(hex, Convert.ToHexStringLower(File.ReadAllBytes(Path.Combine(_root, "active.segment"))));
        using var opened = RbfSegmentStore.OpenExisting(_root);
        Assert.Equal(layout, opened.Layout);
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(6)] [InlineData(8)] [InlineData(9)] [InlineData(12)] [InlineData(16)]
    public void LocatorCorruptOrUnsupportedIsRejectedBeforeWriting(int offset) {
        using (RbfSegmentStore.CreateNew(_root)) { }
        string path = Path.Combine(_root, "active.segment");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[offset] ^= 3;
        if (offset == 12) { bytes.AsSpan(12, 4).Clear(); }
        if (offset != 16) { RollingCrc.SealCodewordForward(bytes); }
        File.WriteAllBytes(path, bytes);
        Assert.Throws<StorageOpenException>(() => RbfSegmentStore.OpenExisting(_root));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(5)] [InlineData(8)]
    public void NonemptyShortTailIsRejectedUnchanged(int length) {
        using (RbfSegmentStore.CreateNew(_root)) { }
        string path = RbfSegmentPath.GetSegmentPath(_root, RbfSegmentStoreLayout.Bucketed, 1);
        byte[] bytes = new byte[length];
        File.ReadAllBytes(path).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        Assert.Equal("InvalidTail", Assert.Throws<StorageOpenException>(() => RbfSegmentStore.OpenExisting(_root)).ReasonCode);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void DailyOpenIgnoresUnrelatedInventoryAndReadsMissingSegmentOnlyOnAccess() {
        using (RbfSegmentStore.CreateNew(_root)) { }
        // Explicit T06 audit cases: malformed filename, misplaced segment, zero, and missing historical segment.
        File.WriteAllBytes(Path.Combine(_root, "buckets", "000000", "bad.rbf"), []);
        Directory.CreateDirectory(Path.Combine(_root, "buckets", "ffffff"));
        File.WriteAllBytes(Path.Combine(_root, "buckets", "ffffff", "00000000.rbf"), []);
        string active = RbfSegmentPath.GetSegmentPath(_root, RbfSegmentStoreLayout.Bucketed, 3);
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        using (var file = RbfFile.CreateNew(active)) { file.Append(7, "last"u8).Unwrap(); file.DurableFlush(); }
        SegmentLocator.Publish(_root, RbfSegmentStoreLayout.Bucketed, 3);
        using var store = RbfSegmentStore.OpenExisting(_root);
        Assert.Throws<FileNotFoundException>(() => store.OpenReader(2));
        using var lease = store.OpenReader(3);
        Assert.Equal(3u, lease.SegmentNumber);
    }

    [Theory]
    [InlineData("OldFlush", false, false)]
    [InlineData("NextCreate", false, false)]
    [InlineData("NextFlush", true, false)]
    [InlineData("LocatorCreate", true, false)]
    [InlineData("LocatorFlush", true, false)]
    [InlineData("LocatorReplace", true, false)]
    [InlineData("LocatorPublished", true, true)]
    [InlineData("OldDispose", true, true)]
    public void RotationFailureFaultsReleasesAndHasExactReopenState(string stage, bool nextExists, bool published) {
        var store = RbfSegmentStore.CreateNew(_root, new() { SegmentSizeThresholdBytes = 8 });
        using (var writer = store.OpenActiveWriter()) { writer.File.Append(7, "data"u8).Unwrap(); }
        store.OperationProbe = point => { if (point == stage) { throw new IOException("injected"); } };
        Assert.Throws<IOException>(() => store.OpenActiveWriter());
        Assert.Throws<InvalidOperationException>(() => store.OpenActiveWriter());
        Assert.Throws<InvalidOperationException>(() => store.OpenReader(1));
        Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable(1));
        store.Dispose();
        Assert.Equal(nextExists, File.Exists(RbfSegmentPath.GetSegmentPath(_root, RbfSegmentStoreLayout.Bucketed, 2)));
        Assert.Equal(published ? 2u : 1u, SegmentLocator.Read(_root).Active);
        if (nextExists && !published) {
            Assert.Equal("NextSegmentPresent", Assert.Throws<StorageOpenException>(() => RbfSegmentStore.OpenExisting(_root)).ReasonCode);
        }
        else {
            using var reopened = RbfSegmentStore.OpenExisting(_root);
            Assert.Equal(published ? 2u : 1u, reopened.ActiveSegmentNumber);
        }
        // All owned handles were closed; exclusive filesystem access succeeds after failure.
        using var exclusive = new FileStream(RbfSegmentPath.GetSegmentPath(_root, RbfSegmentStoreLayout.Bucketed, 1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RotationExistingNextFaultsWithoutOverwritingIt() {
        using var store = RbfSegmentStore.CreateNew(_root, new() { SegmentSizeThresholdBytes = 8 });
        using (var writer = store.OpenActiveWriter()) { writer.File.Append(7, "data"u8).Unwrap(); }
        string next = RbfSegmentPath.GetSegmentPath(_root, RbfSegmentStoreLayout.Bucketed, 2);
        File.WriteAllBytes(next, "existing"u8.ToArray());
        Assert.Equal("NextSegmentPresent", Assert.Throws<StorageOpenException>(() => store.OpenActiveWriter()).ReasonCode);
        Assert.Throws<InvalidOperationException>(() => store.OpenReader(1));
        Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable(1));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(next));
    }

    [Fact]
    public void ConfirmDurableClosedOwnedHandleFaultsInstance() {
        using var store = RbfSegmentStore.CreateNew(_root);
        IRbfFile file;
        using (var lease = store.OpenActiveWriter()) { file = lease.File; }
        // Real owned-handle failure, distinct from the staged exception probes.
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.ConfirmDurable(1));
        Assert.Throws<InvalidOperationException>(() => store.OpenReader(1));
    }

    [Fact]
    public void EmptyActiveAfterEmptyHistoricalIsRejected() {
        using (RbfSegmentStore.CreateNew(_root)) { }
        string next = RbfSegmentPath.GetSegmentPath(_root, RbfSegmentStoreLayout.Bucketed, 2);
        using (var file = RbfFile.CreateNew(next)) { file.DurableFlush(); }
        SegmentLocator.Publish(_root, RbfSegmentStoreLayout.Bucketed, 2);
        Assert.Equal("InvalidTail", Assert.Throws<StorageOpenException>(() => RbfSegmentStore.OpenExisting(_root)).ReasonCode);
    }

    [Fact]
    public void ConfirmDurableFailureFaultsButLeaseAndParameterGuardsDoNot() {
        using var store = RbfSegmentStore.CreateNew(_root);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ConfirmDurable(0));
        using (var lease = store.OpenActiveWriter()) { Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable(1)); }
        store.OperationProbe = _ => throw new IOException("injected");
        Assert.Throws<IOException>(() => store.ConfirmDurable(1));
        Assert.Throws<InvalidOperationException>(() => store.OpenReader(1));
    }
}
