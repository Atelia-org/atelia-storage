using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.RbfSegmentStore.Tests;

public sealed class RbfSegmentStoreTests : IDisposable {
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
    public void SegmentPath_UsesLayoutSpecificDirectories() {
        Assert.Equal(Path.Combine("root", "buckets", "000000", "00000001.rbf"), RbfSegmentPath.GetSegmentPath("root", RbfSegmentStoreLayout.Bucketed, 1));
        Assert.Equal(Path.Combine("root", "buckets", "000000", "000003ff.rbf"), RbfSegmentPath.GetSegmentPath("root", RbfSegmentStoreLayout.Bucketed, 0x3ff));
        Assert.Equal(Path.Combine("root", "buckets", "000001", "00000400.rbf"), RbfSegmentPath.GetSegmentPath("root", RbfSegmentStoreLayout.Bucketed, 0x400));
        Assert.Equal(Path.Combine("root", "segments", "00000001.rbf"), RbfSegmentPath.GetSegmentPath("root", RbfSegmentStoreLayout.Flat, 1));
    }

    [Fact]
    public void Options_RejectInvalidValues() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RbfSegmentStoreOptions { NewStoreLayout = (RbfSegmentStoreLayout)999 }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 0 }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 7 }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RbfSegmentStoreOptions { HistoricalReaderPoolCapacity = -1 }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RbfSegmentStoreOptions { CacheMode = (RbfCacheMode)999 }.Validated());
    }

    [Fact]
    public void CreateNew_CreatesSegmentOne() {
        string storePath = NewStorePath();

        using var store = RbfSegmentStore.CreateNew(storePath);

        Assert.Equal<uint>(1, store.ActiveSegmentNumber);
        Assert.Equal(RbfSegmentStoreLayout.Bucketed, store.Layout);
        Assert.True(File.Exists(SegmentPath(storePath, RbfSegmentStoreLayout.Bucketed, 1)));
    }

    [Fact]
    public void CreateNew_CreatesFlatSegmentOneWhenRequested() {
        string storePath = NewStorePath();

        using var store = RbfSegmentStore.CreateNew(storePath, new RbfSegmentStoreOptions { NewStoreLayout = RbfSegmentStoreLayout.Flat });

        Assert.Equal<uint>(1, store.ActiveSegmentNumber);
        Assert.Equal(RbfSegmentStoreLayout.Flat, store.Layout);
        Assert.True(File.Exists(SegmentPath(storePath, RbfSegmentStoreLayout.Flat, 1)));
    }

    [Fact]
    public void CreateNew_RejectsExistingPath() {
        string storePath = NewStorePath();
        Directory.CreateDirectory(storePath);

        Assert.Throws<IOException>(() => RbfSegmentStore.CreateNew(storePath));
    }

    [Fact]
    public void OpenExisting_RejectsMissingOrEmptyStore() {
        string missingPath = NewStorePath();
        Assert.Throws<DirectoryNotFoundException>(() => RbfSegmentStore.OpenExisting(missingPath));

        string emptyPath = NewStorePath();
        Directory.CreateDirectory(RbfSegmentPath.BucketedDirectory(emptyPath));
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(emptyPath));
    }

    [Fact]
    public void OpenOrCreate_CreatesMissingAndEmptyStore() {
        string missingPath = NewStorePath();
        using (var store = RbfSegmentStore.OpenOrCreate(missingPath)) {
            Assert.Equal<uint>(1, store.ActiveSegmentNumber);
        }

        string emptyPath = NewStorePath();
        Directory.CreateDirectory(RbfSegmentPath.FlatDirectory(emptyPath));
        using var reopened = RbfSegmentStore.OpenOrCreate(emptyPath);
        Assert.Equal<uint>(1, reopened.ActiveSegmentNumber);
        Assert.Equal(RbfSegmentStoreLayout.Flat, reopened.Layout);
    }

    [Fact]
    public void OpenExisting_RejectsInvalidBucketedDirectoryInventory() {
        string wrongBucket = NewStorePath();
        CreateSegmentAt(wrongBucket, "000001", "00000001.rbf");
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(wrongBucket));

        string badName = NewStorePath();
        Directory.CreateDirectory(Path.Combine(RbfSegmentPath.BucketedDirectory(badName), "000000"));
        File.WriteAllBytes(Path.Combine(RbfSegmentPath.BucketedDirectory(badName), "000000", "bad.rbf"), Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(badName));

        string zero = NewStorePath();
        CreateSegmentAt(zero, "000000", "00000000.rbf");
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(zero));

        string gap = NewStorePath();
        CreateSegmentFile(gap, 1);
        CreateSegmentFile(gap, 3);
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(gap));
    }

    [Fact]
    public void OpenExisting_RejectsInvalidFlatDirectoryInventory() {
        var options = new RbfSegmentStoreOptions { NewStoreLayout = RbfSegmentStoreLayout.Flat };

        string nested = NewStorePath();
        Directory.CreateDirectory(Path.Combine(RbfSegmentPath.FlatDirectory(nested), "000000"));
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(nested, options));

        string badName = NewStorePath();
        Directory.CreateDirectory(RbfSegmentPath.FlatDirectory(badName));
        File.WriteAllBytes(Path.Combine(RbfSegmentPath.FlatDirectory(badName), "bad.rbf"), Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(badName, options));

        string zero = NewStorePath();
        CreateSegmentFile(zero, RbfSegmentStoreLayout.Flat, 0);
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(zero, options));

        string gap = NewStorePath();
        CreateSegmentFile(gap, RbfSegmentStoreLayout.Flat, 1);
        CreateSegmentFile(gap, RbfSegmentStoreLayout.Flat, 3);
        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(gap, options));
    }

    [Fact]
    public void OpenExisting_RejectsAmbiguousLayoutDirectories() {
        string storePath = NewStorePath();
        Directory.CreateDirectory(RbfSegmentPath.BucketedDirectory(storePath));
        Directory.CreateDirectory(RbfSegmentPath.FlatDirectory(storePath));

        Assert.Throws<InvalidDataException>(() => RbfSegmentStore.OpenExisting(storePath));
    }

    [Fact]
    public void OpenActiveWriter_RotatesWhenThresholdReached() {
        string storePath = NewStorePath();
        using var store = RbfSegmentStore.CreateNew(storePath, new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8 });

        using (var lease = store.OpenActiveWriter()) {
            Assert.Equal<uint>(1, lease.SegmentNumber);
            _ = lease.File.Append(1, new byte[] { 1, 2, 3, 4 }).Unwrap();
        }

        using (var lease = store.OpenActiveWriter()) {
            Assert.Equal<uint>(2, lease.SegmentNumber);
        }

        Assert.Equal<uint>(2, store.ActiveSegmentNumber);
        Assert.True(File.Exists(SegmentPath(storePath, RbfSegmentStoreLayout.Bucketed, 2)));
    }

    [Fact]
    public void OpenActiveWriter_RotatesFlatLayoutWhenThresholdReached() {
        string storePath = NewStorePath();
        var options = new RbfSegmentStoreOptions {
            NewStoreLayout = RbfSegmentStoreLayout.Flat,
            SegmentSizeThresholdBytes = 8
        };
        using var store = RbfSegmentStore.CreateNew(storePath, options);

        using (var lease = store.OpenActiveWriter()) {
            Assert.Equal<uint>(1, lease.SegmentNumber);
            _ = lease.File.Append(1, new byte[] { 1, 2, 3, 4 }).Unwrap();
        }

        using (var lease = store.OpenActiveWriter()) {
            Assert.Equal<uint>(2, lease.SegmentNumber);
        }

        Assert.Equal<uint>(2, store.ActiveSegmentNumber);
        Assert.Equal(RbfSegmentStoreLayout.Flat, store.Layout);
        Assert.True(File.Exists(SegmentPath(storePath, RbfSegmentStoreLayout.Flat, 2)));
    }

    [Fact]
    public void OpenReader_ReadsActiveSegment() {
        string storePath = NewStorePath();
        using var store = RbfSegmentStore.CreateNew(storePath);
        SizedPtr ticket;

        using (var writer = store.OpenActiveWriter()) {
            ticket = writer.File.Append(7, new byte[] { 10, 20, 30, 40 }).Unwrap();
        }

        using var reader = store.OpenReader(store.ActiveSegmentNumber);
        using var frame = reader.File.ReadPooledFrame(ticket).Unwrap();

        Assert.Equal<uint>(7, frame.Tag);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void OpenReader_KeepsLiveHistoricalLeaseDuringEviction() {
        string storePath = NewStorePath();
        var options = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8, HistoricalReaderPoolCapacity = 1 };
        using var store = RbfSegmentStore.CreateNew(storePath, options);
        SizedPtr ticket1;
        SizedPtr ticket2;

        using (var writer = store.OpenActiveWriter()) {
            ticket1 = writer.File.Append(1, new byte[] { 1, 1, 1, 1 }).Unwrap();
        }

        using (var writer = store.OpenActiveWriter()) {
            Assert.Equal<uint>(2, writer.SegmentNumber);
            ticket2 = writer.File.Append(2, new byte[] { 2, 2, 2, 2 }).Unwrap();
        }

        using (var writer = store.OpenActiveWriter()) {
            Assert.Equal<uint>(3, writer.SegmentNumber);
        }

        using var reader1 = store.OpenReader(1);
        using var reader2 = store.OpenReader(2);
        using var frame1 = reader1.File.ReadPooledFrame(ticket1).Unwrap();
        using var frame2 = reader2.File.ReadPooledFrame(ticket2).Unwrap();

        Assert.Equal<uint>(1, frame1.Tag);
        Assert.Equal<uint>(2, frame2.Tag);
    }

    [Fact]
    public void OpenExisting_RecoversTornActiveTail() {
        string storePath = NewStorePath();
        SizedPtr ticket;

        using (var store = RbfSegmentStore.CreateNew(storePath))
        using (var writer = store.OpenActiveWriter()) {
            ticket = writer.File.Append(9, new byte[] { 9, 9, 9, 9 }).Unwrap();
        }

        string segmentPath = SegmentPath(storePath, RbfSegmentStoreLayout.Bucketed, 1);
        long cleanLength = new FileInfo(segmentPath).Length;
        File.AppendAllBytes(segmentPath, new byte[] { 0, 0, 0, 0 });

        using var reopened = RbfSegmentStore.OpenExisting(storePath);

        Assert.Equal(cleanLength, new FileInfo(segmentPath).Length);
        using var reader = reopened.OpenReader(1);
        using var frame = reader.File.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal<uint>(9, frame.Tag);
    }

    [Fact]
    public void OpenExisting_AcceptsHeaderOnlyActiveSegment() {
        string storePath = NewStorePath();
        using (RbfSegmentStore.CreateNew(storePath)) { }

        using var reopened = RbfSegmentStore.OpenExisting(storePath);

        Assert.Equal<uint>(1, reopened.ActiveSegmentNumber);
    }

    private string NewStorePath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-rbf-segment-store-" + Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private static void CreateSegmentFile(string storePath, uint segmentNumber) {
        CreateSegmentFile(storePath, RbfSegmentStoreLayout.Bucketed, segmentNumber);
    }

    private static void CreateSegmentFile(string storePath, RbfSegmentStoreLayout layout, uint segmentNumber) {
        string path = SegmentPath(storePath, layout, segmentNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = RbfFile.CreateNew(path);
    }

    private static void CreateSegmentAt(string storePath, string bucketName, string fileName) {
        string bucketPath = Path.Combine(RbfSegmentPath.BucketedDirectory(storePath), bucketName);
        Directory.CreateDirectory(bucketPath);
        using var file = RbfFile.CreateNew(Path.Combine(bucketPath, fileName));
    }

    private static string SegmentPath(string storePath, RbfSegmentStoreLayout layout, uint segmentNumber) {
        if (segmentNumber == 0) {
            return layout switch {
                RbfSegmentStoreLayout.Bucketed => Path.Combine(RbfSegmentPath.BucketedDirectory(storePath), "000000", "00000000.rbf"),
                RbfSegmentStoreLayout.Flat => Path.Combine(RbfSegmentPath.FlatDirectory(storePath), "00000000.rbf"),
                _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown RBF segment store layout.")
            };
        }

        return RbfSegmentPath.GetSegmentPath(storePath, layout, segmentNumber);
    }
}
