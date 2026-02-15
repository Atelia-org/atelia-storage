using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Atelia.Rbf.ReadCache.Tests;

/// <summary>
/// Tests for <see cref="ReverseReadCache"/>.
/// Uses a temp file with known content to verify cache hit / miss behaviour.
/// </summary>
public sealed partial class ReverseReadCacheTests : IDisposable {
    private readonly string _path;
    private readonly SafeFileHandle _handle;

    /// <summary>Total test file size: 64 KB of patterned data.</summary>
    private const int FileSize = ReverseReadCache.PageSize * 20;

    /// <summary>Cache page size used by <see cref="ReverseReadCache"/>.</summary>
    private const int PageSize = ReverseReadCache.PageSize;

    public ReverseReadCacheTests() {
        _path = Path.Combine(Path.GetTempPath(), $"rrc-test-{Guid.NewGuid()}.bin");

        // Write 64 KB where each byte == (position % 251).
        // Using a prime modulus so the pattern is recognisable across page boundaries.
        var data = new byte[FileSize];
        for (int i = 0; i < data.Length; i++) {
            data[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(_path, data);
        _handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void Dispose() {
        _handle.Dispose();
        try { File.Delete(_path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Expected byte value at a given file offset.</summary>
    private static byte Expected(long offset) => (byte)(offset % 251);

    /// <summary>Verify that every byte in <paramref name="buf"/> matches the pattern.</summary>
    private static void AssertPattern(ReadOnlySpan<byte> buf, long startOffset) {
        for (int i = 0; i < buf.Length; i++) {
            Assert.Equal(Expected(startOffset + i), buf[i]);
        }
    }

    // ── Basic correctness ────────────────────────────────────────────

    [Fact]
    public void Read_SmallRead_ReturnsCorrectData() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf = stackalloc byte[20];
        int n = cache.Read(100, buf);

        Assert.Equal(20, n);
        AssertPattern(buf, 100);
    }

    [Fact]
    public void Read_AtOffsetZero_Works() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf = stackalloc byte[10];
        int n = cache.Read(0, buf);

        Assert.Equal(10, n);
        AssertPattern(buf, 0);
    }

    [Fact]
    public void Read_EmptyBuffer_ReturnsZero() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        int n = cache.Read(0, Span<byte>.Empty);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Read_PastEof_ReturnsShortRead() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf = stackalloc byte[100];
        int n = cache.Read(FileSize - 30, buf);

        Assert.Equal(30, n);
        AssertPattern(buf[..30], FileSize - 30);
    }

    [Fact]
    public void Read_BeyondEof_ReturnsZero() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf = stackalloc byte[10];
        int n = cache.Read(FileSize + 1000, buf);

        Assert.Equal(0, n);
    }

    // ── Cross-page reads ─────────────────────────────────────────────

    [Fact]
    public void Read_CrossPageBoundary_ReturnsCorrectData() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        // Read 40 bytes spanning a page boundary (page size = 256)
        long offset = PageSize - 10; // starts 10 bytes before page 1
        Span<byte> buf = stackalloc byte[40];
        int n = cache.Read(offset, buf);

        Assert.Equal(40, n);
        AssertPattern(buf, offset);
    }

    [Fact]
    public void Read_CrossPageBoundary_CachesStartPageOnly() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        long offset = PageSize - 8;
        var buf = new byte[20];
        int n = cache.Read(offset, buf);

        Assert.Equal(20, n);
        AssertPattern(buf, offset);

        var segs = InvokeGetCacheSegments(cache)!;
        Assert.Single(segs);
        Assert.Equal(0L, segs[0].Offset);
        Assert.Equal(PageSize, segs[0].Length);
    }

    [Fact]
    public void Read_EntirePageExactly_Works() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        var buf = new byte[PageSize];
        int n = cache.Read(PageSize * 3, buf);

        Assert.Equal(PageSize, n);
        AssertPattern(buf, PageSize * 3);
    }

    [Fact]
    public void Read_SpanningThreePages_Works() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 3);

        // Start mid-page, read enough to span 3 pages
        long offset = PageSize + 100;
        int length = PageSize * 2 + 50;
        var buf = new byte[length];
        int n = cache.Read(offset, buf);

        Assert.Equal(length, n);
        AssertPattern(buf, offset);
    }

    // ── Cache hits (same data read twice) ────────────────────────────

    [Fact]
    public void Read_SameOffsetTwice_ReturnsSameData() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf1 = stackalloc byte[20];
        Span<byte> buf2 = stackalloc byte[20];

        cache.Read(500, buf1);
        cache.Read(500, buf2);

        Assert.True(buf1.SequenceEqual(buf2));
    }

    [Fact]
    public void Read_AdjacentReadsOnSamePage_BothCorrect() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Span<byte> buf1 = stackalloc byte[20];
        Span<byte> buf2 = stackalloc byte[20];

        // Both reads within page 2 (offsets 512..767)
        cache.Read(520, buf1);
        cache.Read(600, buf2);

        AssertPattern(buf1, 520);
        AssertPattern(buf2, 600);
    }

    // ── Reverse-scan simulation ──────────────────────────────────────

    [Fact]
    public void Read_ReverseSequentialPattern_AllDataCorrect() {
        using var cache = new ReverseReadCache(
            _handle,
            slotCountShift: 3
        );

        // Simulate ScanReverse: 20-byte reads walking backward from near EOF
        const int readSize = 20;
        long offset = FileSize - readSize;
        int reads = 0;
        Span<byte> buf = stackalloc byte[readSize];

        while (offset >= 0) {
            int n = cache.Read(offset, buf);

            Assert.Equal(readSize, n);
            AssertPattern(buf, offset);

            offset -= readSize;
            reads++;

            if (reads > 500) { break; }
        }

        Assert.True(reads > 100, "Should have completed many reads");
    }

    // ── Eviction ─────────────────────────────────────────────────────

    [Fact]
    public void Read_MorePagesThanSlots_StillReturnsCorrectData() {
        // Only 2 slots, but touch many pages → forces eviction
        using var cache = new ReverseReadCache(
            _handle,
            slotCountShift: 1
        );

        Span<byte> buf = stackalloc byte[20];

        for (int page = 0; page < 20; page++) {
            long offset = page * PageSize + 10;
            int n = cache.Read(offset, buf);

            Assert.Equal(20, n);
            AssertPattern(buf, offset);
        }
    }

    // ── Constructor validation ───────────────────────────────────────

    [Fact]
    public void Ctor_NullFile_Throws() {
        Assert.Throws<ArgumentNullException>(
            () => new ReverseReadCache(null!)
        );
    }

    // ── Dispose ──────────────────────────────────────────────────────

    [Fact]
    public void Read_AfterDispose_Throws() {
        var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => {
                Span<byte> buf = stackalloc byte[10];
                cache.Read(0, buf);
            }
        );
    }

    [Fact]
    public void Dispose_CalledTwice_NoThrow() {
        var cache = new ReverseReadCache(_handle, slotCountShift: 2);
        cache.Dispose();
        cache.Dispose(); // idempotent
    }

    // ── Edge cases ───────────────────────────────────────────────────

    [Fact]
    public void Read_NegativeOffset_Throws() {
        using var cache = new ReverseReadCache(_handle, slotCountShift: 2);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => {
                Span<byte> buf = stackalloc byte[10];
                cache.Read(-1, buf);
            }
        );
    }
}
