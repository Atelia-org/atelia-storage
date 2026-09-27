using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.RbfSegmentStore.Tests;

public sealed class RbfSegmentStoreConfirmDurableTests : IDisposable {
    private readonly List<string> _tempDirectories = new();

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    [Fact]
    public void ConfirmDurable_ActiveSegmentAtThreshold_DoesNotRotate() {
        string storePath = NewStorePath();
        using var store = RbfSegmentStore.CreateNew(
            storePath,
            new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 32 }
        );

        using (var writer = store.OpenActiveWriter()) {
            writer.File.Append(1, Array.Empty<byte>()).Unwrap();
        }

        store.ConfirmDurable(1);

        Assert.Equal<uint>(1, store.ActiveSegmentNumber);
        Assert.False(File.Exists(RbfSegmentPath.GetSegmentPath(storePath, store.Layout, 2)));
    }

    [Fact]
    public void ConfirmDurable_HistoricalIdlePooledReader_EvictsThenCanReadAgain() {
        string storePath = NewStorePath();
        var options = new RbfSegmentStoreOptions {
            SegmentSizeThresholdBytes = 32,
            HistoricalReaderPoolCapacity = 1
        };
        using var store = RbfSegmentStore.CreateNew(storePath, options);
        SizedPtr ticket;

        using (var writer = store.OpenActiveWriter()) {
            ticket = writer.File.Append(1, Array.Empty<byte>()).Unwrap();
        }
        using (store.OpenActiveWriter()) { }
        using (var reader = store.OpenReader(1)) {
            Assert.Equal<uint>(1, reader.File.ReadFrameInfo(ticket).Unwrap().Tag);
        }

        store.ConfirmDurable(1);

        using var rereader = store.OpenReader(1);
        Assert.Equal<uint>(1, rereader.File.ReadFrameInfo(ticket).Unwrap().Tag);
    }

    [Fact]
    public void ConfirmDurable_ActiveOrHistoricalLiveLease_RejectsAndLeaseRemainsReadable() {
        string storePath = NewStorePath();
        var options = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 32 };
        using var store = RbfSegmentStore.CreateNew(storePath, options);
        SizedPtr ticket1;

        using (var writer = store.OpenActiveWriter()) {
            ticket1 = writer.File.Append(1, Array.Empty<byte>()).Unwrap();
        }

        using (var activeReader = store.OpenReader(1)) {
            Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable(1));
            Assert.Equal<uint>(1, activeReader.File.ReadFrameInfo(ticket1).Unwrap().Tag);
        }

        using (store.OpenActiveWriter()) { }
        using var historicalReader = store.OpenReader(1);
        Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable(1));
        Assert.Equal<uint>(1, historicalReader.File.ReadFrameInfo(ticket1).Unwrap().Tag);
    }

    [Fact]
    public void ConfirmDurable_RejectsReadOnlyDisposedAndInvalidSegments_AndWorksAfterReopen() {
        string storePath = NewStorePath();
        SizedPtr ticket;
        using (var store = RbfSegmentStore.CreateNew(storePath))
        using (var writer = store.OpenActiveWriter()) {
            ticket = writer.File.Append(1, new byte[] { 1, 2, 3, 4 }).Unwrap();
        }

        using (var readOnly = RbfSegmentStore.OpenReadOnlyExisting(storePath)) {
            Assert.Throws<InvalidOperationException>(() => readOnly.ConfirmDurable(1));
        }

        using (var reopened = RbfSegmentStore.OpenExisting(storePath)) {
            Assert.Throws<ArgumentOutOfRangeException>(() => reopened.ConfirmDurable(0));
            Assert.Throws<FileNotFoundException>(() => reopened.ConfirmDurable(2));
            reopened.ConfirmDurable(1);

            using var reader = reopened.OpenReader(1);
            Assert.Equal<uint>(1, reader.File.ReadFrameInfo(ticket).Unwrap().Tag);
        }

        var disposed = RbfSegmentStore.OpenExisting(storePath);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.ConfirmDurable(1));
    }

    private string NewStorePath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-rbf-segment-store-confirm-" + Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }
}
