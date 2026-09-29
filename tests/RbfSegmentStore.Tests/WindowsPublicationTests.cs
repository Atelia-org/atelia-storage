using Atelia.Rbf;
using Xunit;

namespace Atelia.RbfSegmentStore.Tests;

public sealed class WindowsPublicationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "windows-segment-publication-" + Guid.NewGuid().ToString("N"));
    private readonly string _positiveRoot = Path.Combine(Path.GetTempPath(), "windows-segment-control-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RotationDenyDeleteSharingConflictPreservesOldLocatorAndStopsInstance() {
        if (!OperatingSystem.IsWindows()) { return; } // This case requires Windows deny-delete sharing semantics.

        string locatorPath = Path.Combine(_root, SegmentLocator.FileName);
        using (var store = RbfSegmentStore.CreateNew(_root, new() { SegmentSizeThresholdBytes = 8, CacheMode = RbfCacheMode.Off })) {
            using (var writer = store.OpenActiveWriter()) { writer.File.Append(7, "data"u8).Unwrap(); }
            byte[] locatorBefore = File.ReadAllBytes(locatorPath);
            using (var blocker = new FileStream(locatorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                // Reads/writes are allowed; replacing the directory entry requires the missing Delete share.
                var stages = new List<string>();
                store.OperationProbe = stages.Add; // Observe the real failure stage; do not inject an exception.
                Exception error = Assert.ThrowsAny<Exception>(() => store.OpenActiveWriter());
                Assert.True(error.GetType() == typeof(IOException) && error.HResult == unchecked((int)0x80070020)
                    || error.GetType() == typeof(UnauthorizedAccessException) && error.HResult == unchecked((int)0x80070005),
                    $"Unexpected replacement failure: {error.GetType().Name}, HRESULT=0x{error.HResult:x8}.");
                Assert.Contains("LocatorReplace", stages);
                Assert.DoesNotContain("LocatorPublished", stages);
                Assert.Equal(locatorBefore, File.ReadAllBytes(locatorPath));
                Assert.Equal(1u, SegmentLocator.Read(_root).Active);
                string nextPath = RbfSegmentPath.GetSegmentPath(_root, store.Layout, 2);
                Assert.True(File.Exists(nextPath));
                Assert.Equal(4L, new FileInfo(nextPath).Length);
                Assert.Throws<InvalidOperationException>(() => store.OpenActiveWriter());
                Assert.Throws<InvalidOperationException>(() => store.OpenReader(1));
                Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable(1));
            }
        }
        var reopenError = Assert.Throws<StorageOpenException>(() => RbfSegmentStore.OpenReadOnlyExisting(_root));
        Assert.Equal("NextSegmentPresent", reopenError.ReasonCode);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        // Preserve the failed fixture's old+next ambiguity. A fresh sibling uses the same
        // filesystem and public rotation with no blocker, so persistent permission failures cannot pass.
        string positivePath = _positiveRoot;
        using (var positive = RbfSegmentStore.CreateNew(positivePath, new() { SegmentSizeThresholdBytes = 8, CacheMode = RbfCacheMode.Off })) {
            using (var writer = positive.OpenActiveWriter()) { writer.File.Append(7, "data"u8).Unwrap(); }
            using var next = positive.OpenActiveWriter();
            Assert.Equal(2u, next.SegmentNumber);
        }
        using var reopened = RbfSegmentStore.OpenReadOnlyExisting(positivePath);
        Assert.Equal(2u, reopened.ActiveSegmentNumber);
        Assert.Equal(2u, SegmentLocator.Read(positivePath).Active);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
        if (Directory.Exists(_positiveRoot)) { Directory.Delete(_positiveRoot, recursive: true); }
    }
}
