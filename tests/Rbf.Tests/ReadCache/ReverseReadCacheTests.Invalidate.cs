using Xunit;

namespace Atelia.Rbf.ReadCache.Tests;

partial class ReverseReadCacheTests {
    // ── InvalidateFrom ──────────────────────────────────────────────

    [Fact]
    public void InvalidateFrom_AfterAppend_ReturnsNewData() {
        var path = Path.Combine(Path.GetTempPath(), $"inv-{Guid.NewGuid()}.bin");
        try {
            int initialSize = PageSize * 2 + 100;
            var data = new byte[initialSize];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 251); }
            File.WriteAllBytes(path, data);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cache = new ReverseReadCache(handle, slotCountShift: 2);

            // Read the partial page → cached as short read (100 bytes)
            var buf = new byte[200];
            int n = cache.Read(PageSize * 2, buf);
            Assert.Equal(100, n);

            // Append 200 bytes
            var append = new byte[200];
            for (int i = 0; i < append.Length; i++) { append[i] = (byte)((initialSize + i) % 251); }
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) {
                fs.Write(append);
            }

            // Invalidate from old EOF
            cache.InvalidateFrom(initialSize);

            // Re-read: should now get 200 bytes of correct data
            buf = new byte[200];
            n = cache.Read(PageSize * 2, buf);
            Assert.Equal(200, n);
            AssertPattern(buf, PageSize * 2);
        }
        finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void InvalidateFrom_Zero_StillReturnsCorrectData() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf = stackalloc byte[20];
        cache.Read(PageSize, buf);
        cache.Read(PageSize * 3, buf);

        // Invalidate everything
        cache.InvalidateFrom(0);

        // Should still return correct data after re-read from disk
        int n = cache.Read(PageSize, buf);
        Assert.Equal(20, n);
        AssertPattern(buf, PageSize);
    }

    [Fact]
    public void InvalidateFrom_MidFile_PreservesEarlierPages() {
        var path = Path.Combine(Path.GetTempPath(), $"inv-mid-{Guid.NewGuid()}.bin");
        try {
            int fileSize = PageSize * 4;
            var data = new byte[fileSize];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 251); }
            File.WriteAllBytes(path, data);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cache = new ReverseReadCache(handle, slotCountShift: 3);

            // Cache page 0 and page 3
            Span<byte> buf = stackalloc byte[20];
            cache.Read(10, buf);
            cache.Read(PageSize * 3 + 10, buf);

            // Invalidate from page 2 onward — page 0 should remain cached
            cache.InvalidateFrom(PageSize * 2);

            // Overwrite page 3 on disk with zeros
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
                fs.Seek(PageSize * 3, SeekOrigin.Begin);
                fs.Write(new byte[PageSize]);
            }

            // Page 0 (not invalidated) still returns correct pattern
            int n = cache.Read(10, buf);
            Assert.Equal(20, n);
            AssertPattern(buf, 10);

            // Page 3 (invalidated) should re-read → now zeros
            buf = stackalloc byte[20];
            n = cache.Read(PageSize * 3 + 10, buf);
            Assert.Equal(20, n);
            for (int i = 0; i < 20; i++) { Assert.Equal(0, buf[i]); }
        }
        finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void InvalidateFrom_Negative_Throws() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.InvalidateFrom(-1));
    }

    [Fact]
    public void InvalidateFrom_AfterDispose_Throws() {
        var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.InvalidateFrom(0));
    }

    // ── NotifyFileLengthChanged ─────────────────────────────────────

    [Fact]
    public void NotifyFileLengthChanged_AfterAppend_ReturnsNewData() {
        var path = Path.Combine(Path.GetTempPath(), $"nlc-{Guid.NewGuid()}.bin");
        try {
            int initialSize = PageSize * 2 + 100;
            var data = new byte[initialSize];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 251); }
            File.WriteAllBytes(path, data);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cache = new ReverseReadCache(handle, slotCountShift: 2);

            // Cache the partial page
            var buf = new byte[200];
            int n = cache.Read(PageSize * 2, buf);
            Assert.Equal(100, n);

            // Append 200 bytes
            int newSize = initialSize + 200;
            var append = new byte[200];
            for (int i = 0; i < append.Length; i++) { append[i] = (byte)((initialSize + i) % 251); }
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) {
                fs.Write(append);
            }

            // Notify new length
            cache.NotifyFileLengthChanged(newSize);

            // Re-read: should now get 200 bytes
            buf = new byte[200];
            n = cache.Read(PageSize * 2, buf);
            Assert.Equal(200, n);
            AssertPattern(buf, PageSize * 2);
        }
        finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void NotifyFileLengthChanged_PreservesFullPagesBeforeBoundary() {
        var path = Path.Combine(Path.GetTempPath(), $"nlc-preserve-{Guid.NewGuid()}.bin");
        try {
            int fileSize = PageSize * 4;
            var data = new byte[fileSize];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 251); }
            File.WriteAllBytes(path, data);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cache = new ReverseReadCache(handle, slotCountShift: 3);

            // Cache page 0 (full) and page 3 (full, last page of file)
            Span<byte> buf = stackalloc byte[20];
            cache.Read(10, buf);
            cache.Read(PageSize * 3 + 10, buf);

            // Notify that file grew to 5 pages — page 0 should survive (full, before boundary)
            cache.NotifyFileLengthChanged(PageSize * 5);

            // Overwrite page 0 on disk with zeros
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(new byte[PageSize]);
            }

            // Page 0 is still cached → should still return the original pattern
            int n = cache.Read(10, buf);
            Assert.Equal(20, n);
            AssertPattern(buf, 10);
        }
        finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void NotifyFileLengthChanged_Truncate_InvalidatesAffectedPages() {
        var path = Path.Combine(Path.GetTempPath(), $"nlc-trunc-{Guid.NewGuid()}.bin");
        try {
            int fileSize = PageSize * 4;
            var data = new byte[fileSize];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 251); }
            File.WriteAllBytes(path, data);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cache = new ReverseReadCache(handle, slotCountShift: 3);

            // Cache pages 0, 2, 3
            Span<byte> buf = stackalloc byte[20];
            cache.Read(10, buf);
            cache.Read(PageSize * 2 + 10, buf);
            cache.Read(PageSize * 3 + 10, buf);

            // Truncate file to 2.5 pages
            int newSize = PageSize * 2 + PageSize / 2;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
                fs.SetLength(newSize);
            }

            cache.NotifyFileLengthChanged(newSize);

            // Page 3 is beyond new EOF → read returns 0
            buf = stackalloc byte[20];
            int n = cache.Read(PageSize * 3 + 10, buf);
            Assert.Equal(0, n);

            // Page 2 (boundary page, truncated) → should get only the available portion
            buf = stackalloc byte[PageSize];
            n = cache.Read(PageSize * 2, buf);
            Assert.Equal(PageSize / 2, n);
            AssertPattern(buf[..(PageSize / 2)], PageSize * 2);
        }
        finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void NotifyFileLengthChanged_Negative_Throws() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.NotifyFileLengthChanged(-1));
    }

    [Fact]
    public void NotifyFileLengthChanged_AfterDispose_Throws() {
        var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.NotifyFileLengthChanged(0));
    }
}
