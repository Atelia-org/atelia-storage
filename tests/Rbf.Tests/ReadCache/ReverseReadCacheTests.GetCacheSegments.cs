using System.Reflection;
using Xunit;

namespace Atelia.Rbf.ReadCache.Tests;

partial class ReverseReadCacheTests {
    // ── GetCacheSegments ────────────────────────────────────────────

    private static List<OffsetLength>? InvokeGetCacheSegments(ReverseReadCache cache) {
        var method = typeof(ReverseReadCache).GetMethod(
            "GetCacheSegments",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);
        return (List<OffsetLength>?)method!.Invoke(cache, null);
    }

    [Fact]
    public void GetCacheSegments_Empty_ReturnsEmptyList() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        var segs = InvokeGetCacheSegments(cache);
        Assert.NotNull(segs);
        Assert.Empty(segs!);
    }

    [Fact]
    public void GetCacheSegments_AfterOneRead_ReturnsOnePage() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        Span<byte> buf = stackalloc byte[20];
        cache.Read(buf, 100);

        var segs = InvokeGetCacheSegments(cache)!;
        Assert.Single(segs);
        Assert.Equal(0, segs[0].Offset); // page 0 starts at file offset 0
        Assert.Equal(PageSize, segs[0].Length); // full page cached
    }

    [Fact]
    public void GetCacheSegments_TwoDistinctPages_ReturnsTwoSegments() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 3);
        Span<byte> buf = stackalloc byte[20];
        cache.Read(buf, 10); // caches page 0
        cache.Read(buf, PageSize * 5 + 10); // caches page 5

        var segs = InvokeGetCacheSegments(cache)!;
        Assert.Equal(2, segs.Count);

        // Segments may not be sorted; verify both pages are present.
        var offsets = segs.Select(s => s.Offset).OrderBy(o => o).ToList();
        Assert.Equal(0L, offsets[0]);
        Assert.Equal((long)PageSize * 5, offsets[1]);
    }

    [Fact]
    public void GetCacheSegments_ShortReadPage_ReportsActualValidLength() {
        // The fixture file is page-aligned (FileSize = PageSize*20), so we need
        // a non-aligned file to produce a genuine short-read page.
        var path = Path.Combine(Path.GetTempPath(), $"seg-short-{Guid.NewGuid()}.bin");
        int shortFileSize = PageSize * 3 + 100; // 100 bytes into the 4th page
        try {
            var data = new byte[shortFileSize];
            for (int i = 0; i < data.Length; i++) { data[i] = (byte)(i % 251); }
            File.WriteAllBytes(path, data);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var cache = new ReverseReadCache(handle, slotCountShift: 2);

            // Read near EOF — will cache page 3 as a short page (100 valid bytes)
            var buf = new byte[200];
            cache.Read(buf, PageSize * 3); // only 100 bytes available

            var segs = InvokeGetCacheSegments(cache)!;
            var lastPageSeg = segs.FirstOrDefault(s => s.Offset == (long)PageSize * 3);
            Assert.Equal(100, lastPageSeg.Length);
        }
        finally {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void GetCacheSegments_AfterInvalidateFrom_ReflectsEviction() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 3);
        Span<byte> buf = stackalloc byte[20];
        cache.Read(buf, 10); // page 0
        cache.Read(buf, PageSize * 3 + 10); // page 3
        cache.Read(buf, PageSize * 7 + 10); // page 7

        var before = InvokeGetCacheSegments(cache)!;
        Assert.Equal(3, before.Count);

        cache.InvalidateFrom(PageSize * 3);

        var after = InvokeGetCacheSegments(cache)!;
        // Only page 0 should survive
        Assert.Single(after);
        Assert.Equal(0L, after[0].Offset);
    }

    [Fact]
    public void GetCacheSegments_AfterEviction_SlotsRecycled() {
        // 2 slots only → forces eviction by the 3rd read
        using var cache = new ReverseReadCache(_handle, slotCountShift: 1);
        Span<byte> buf = stackalloc byte[20];

        cache.Read(buf, 10); // page 0 → slot 0
        cache.Read(buf, PageSize + 10); // page 1 → slot 1
        cache.Read(buf, PageSize * 2 + 10); // page 2 → evicts page 0

        var segs = InvokeGetCacheSegments(cache)!;
        var offsets = segs.Select(s => s.Offset).OrderBy(o => o).ToList();
        // page 0 evicted, so only page 1 and page 2 should be present
        Assert.Equal(2, offsets.Count);
        Assert.Equal((long)PageSize, offsets[0]);
        Assert.Equal((long)PageSize * 2, offsets[1]);
    }

    [Fact]
    public void GetCacheSegments_Logger_EmitsHitMapInCsv() {
        var logPath = Path.Combine(Path.GetTempPath(), $"hmc-{Guid.NewGuid()}.csv");
        try {
            using var cache = new ReverseReadCache(_handle, slotCountShift: 3);
            cache.SetupLogger(new ReadLogger.Params(logPath, Append: false, FlushEvery: 1));

            Span<byte> buf = stackalloc byte[20];
            cache.Read(buf, 100); // cold miss → page 0 cached
            cache.Read(buf, 100); // warm hit  → same page

            cache.Dispose(); // flush logger

            var lines = File.ReadAllLines(logPath);
            // Line 0: header (#v1 ...)
            // Line 1: column names
            // Line 2: first read (cold miss)
            // Line 3: second read (warm hit)
            Assert.True(lines.Length >= 4, $"Expected ≥4 log lines, got {lines.Length}");

            // The hitmap column (last field) should be non-empty on the warm-hit line
            var warmHitLine = lines[3];
            var fields = warmHitLine.Split(',');
            string hitmap = fields[^1]; // last column
            Assert.Contains("H", hitmap); // warm hit → must contain 'H'
        }
        finally {
            try { File.Delete(logPath); } catch { }
        }
    }
}
