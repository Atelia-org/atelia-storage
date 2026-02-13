using Xunit;

namespace Atelia.Rbf.ReadCache.Tests;

public class CacheHitMapTests {
    private static List<OffsetLength> NoCache => [];
    private static List<OffsetLength> Segs(params (long, long)[] s) {
        var list = new List<OffsetLength>(s.Length);
        foreach (var (off, len) in s) { list.Add(new OffsetLength(off, len)); }
        return list;
    }

    [Fact]
    public void NoCache_RequestOnly_ReturnsM() {
        Assert.Equal("M0", CacheHitMap.Render(100, 200, NoCache).Map);
    }

    [Fact]
    public void EmptyInput_ReturnsNull() {
        Assert.Null(CacheHitMap.Render(0, 0, NoCache).Map);
    }

    [Fact]
    public void CacheOnly_NoRequest_ReturnsC() {
        Assert.Equal("C0", CacheHitMap.Render(0, 0, Segs((100, 50))).Map);
    }

    [Fact]
    public void ExactHit_ReturnsH() {
        // Request exactly matches cache segment.
        Assert.Equal("H0", CacheHitMap.Render(100, 200, Segs((100, 200))).Map);
    }

    [Fact]
    public void RequestBeforeCache_WithGap() {
        // R:[100,200)  C:[300,400)  → M0_0C0 (all equal length)
        Assert.Equal("M0_0C0", CacheHitMap.Render(100, 100, Segs((300, 100))).Map);
    }

    [Fact]
    public void CacheBeforeRequest_WithGap() {
        // C:[50,100)  R:[200,300)  → C(50) _(100) M(100)
        Assert.Equal("C0_zMz", CacheHitMap.Render(200, 100, Segs((50, 50))).Map);
    }

    [Fact]
    public void PartialHit_LowMiss() {
        // R:[100,300)  C:[200,400)  → M(100) H(100) C(100) all equal
        Assert.Equal("M0H0C0", CacheHitMap.Render(100, 200, Segs((200, 200))).Map);
    }

    [Fact]
    public void PartialHit_HighMiss() {
        // R:[200,400)  C:[100,300)  → C(100) H(100) M(100) all equal
        Assert.Equal("C0H0M0", CacheHitMap.Render(200, 200, Segs((100, 200))).Map);
    }

    [Fact]
    public void RequestInsideCache() {
        // C:[50,500)  R:[100,200)  → C(50) H(100) C(300)
        Assert.Equal("C0HeCz", CacheHitMap.Render(100, 100, Segs((50, 450))).Map);
    }

    [Fact]
    public void CacheInsideRequest() {
        // R:[50,500)  C:[100,200)  → M(50) H(100) M(300)
        Assert.Equal("M0HeMz", CacheHitMap.Render(50, 450, Segs((100, 100))).Map);
    }

    [Fact]
    public void TwoCacheSegments_WithGap_NoOverlap() {
        // R:[100,200)  C:[300,400), [600,700)  → M(100) _(100) C(100) _(200) C(100)
        Assert.Equal("M0_0C0_zC0", CacheHitMap.Render(100, 100, Segs((300, 100), (600, 100))).Map);
    }

    [Fact]
    public void RequestSpansTwoCacheChunks_WithGap() {
        // C:[100,200)  R:[150,550)  C:[500,600)
        // Sweep: C(50) H(50) M(300) H(50) C(50)
        Assert.Equal("C0H0MzH0C0",
            CacheHitMap.Render(
                150, 400, Segs((100, 100), (500, 100))
            ).Map
        );
    }

    [Fact]
    public void TouchingCacheSegments_MergeToSingleC() {
        // C:[100,200) + C:[200,300)  R:[400,500)
        // Sweep: C(200) _(100) M(100)
        Assert.Equal("Cz_0M0", CacheHitMap.Render(400, 100, Segs((100, 100), (200, 100))).Map);
    }

    [Fact]
    public void AdjacentRequestAndCache_NoGap() {
        // R:[100,200)  C:[200,300)  → M(100) C(100) equal length
        Assert.Equal("M0C0", CacheHitMap.Render(100, 100, Segs((200, 100))).Map);
    }

    [Fact]
    public void MultipleDisjointCacheSegments_SomeHit() {
        // R:[200,400)  C:[100,250), [350,500)
        // Sweep: C(100) H(50) M(100) H(50) C(100)
        Assert.Equal("CzH0MzH0Cz",
            CacheHitMap.Render(
                200, 200, Segs((100, 150), (350, 150))
            ).Map
        );
    }

    [Fact]
    public void ZeroLengthCacheSegment_Ignored() {
        Assert.Equal("M0", CacheHitMap.Render(100, 200, Segs((50, 0))).Map);
    }

    [Fact]
    public void ZeroLengthRequest_CacheOnly() {
        Assert.Equal("C0", CacheHitMap.Render(100, 0, Segs((200, 50))).Map);
    }

    // ── Log-scale tests ──────────────────────────────────────────────

    [Fact]
    public void LogScale_SingleSegment_Level0() {
        // Single segment → Lmin==Lmax → level '0'.
        var result = CacheHitMap.Render(100, 8192, NoCache);
        Assert.Equal("M0", result.Map);
        Assert.Equal(8192, result.SegMin);
        Assert.Equal(8192, result.SegMax);
    }

    [Fact]
    public void LogScale_EqualLengths_AllLevel0() {
        // All segments same length → Lmin==Lmax → all level '0'.
        // R:[0,100) C:[200,300) → M(100) _(100) C(100)
        var result = CacheHitMap.Render(0, 100, Segs((200, 100)));
        Assert.Equal("M0_0C0", result.Map);
        Assert.Equal(100, result.SegMin);
        Assert.Equal(100, result.SegMax);
    }

    [Fact]
    public void LogScale_LargeVsSmall_ShowsProportion() {
        // R:[0,100)  C:[0, 8192) → H(100), C(8092)
        // Lmin=100, Lmax=8092 → H='0', C='z'
        var result = CacheHitMap.Render(0, 100, Segs((0, 8192)));
        Assert.Equal("H0Cz", result.Map);
        Assert.Equal(100, result.SegMin);
        Assert.Equal(8092, result.SegMax);
    }

    [Fact]
    public void LogScale_GapParticipatesInScaling() {
        // M(100), _(10000), C(100)
        // Lmin=100, Lmax=10000
        var result = CacheHitMap.Render(0, 100, Segs((10100, 100)));
        Assert.Equal("M0_zC0", result.Map);
        Assert.Equal(100, result.SegMin);
        Assert.Equal(10000, result.SegMax);
    }

    [Fact]
    public void LogScale_ThreeDistinctLengths() {
        // R:[0,50) C:[100,600) C2:[5600,5650)
        // M(50), _(50), C(500), _(5000), C(50)
        // Lmin=50, Lmax=5000 → C(500): level='i' (log10/log100=0.5→18)
        var result = CacheHitMap.Render(0, 50, Segs((100, 500), (5600, 50)));
        Assert.Equal("M0_0Ci_zC0", result.Map);
        Assert.Equal(50, result.SegMin);
        Assert.Equal(5000, result.SegMax);
    }

    [Fact]
    public void LogScale_RequestSpansTwoChunks() {
        // C:[0,8192)  R:[4096,20480)  C:[16384,24576)
        // Sweep: C(4096), H(4096), M(8192), H(4096), C(4096)
        // Lmin=4096, Lmax=8192
        var result = CacheHitMap.Render(4096, 16384, Segs((0, 8192), (16384, 8192)));
        Assert.Equal("C0H0MzH0C0", result.Map);
        Assert.Equal(4096, result.SegMin);
        Assert.Equal(8192, result.SegMax);
    }

    // ── Sweep tests (Phase 1 directly) ──────────────────────────────

    [Fact]
    public void Sweep_MergesAdjacentSameType() {
        // C:[100,200) + C:[200,300) → touching cache → single C(200)
        // R:[400,500)
        var segs = CacheHitMap.Sweep(400, 100, Segs((100, 100), (200, 100)));
        // C(200), _(100), M(100)
        Assert.Equal(3, segs.Count);
        Assert.Equal(new CacheHitMap.Segment('C', 200), segs[0]);
        Assert.Equal(new CacheHitMap.Segment('_', 100), segs[1]);
        Assert.Equal(new CacheHitMap.Segment('M', 100), segs[2]);
    }

    [Fact]
    public void Sweep_GapBetweenSameTypeNotMerged() {
        // C:[100,200) gap C:[400,500) → C, _, C (not merged across gap)
        var segs = CacheHitMap.Sweep(0, 0, Segs((100, 100), (400, 100)));
        Assert.Equal(3, segs.Count);
        Assert.Equal('C', segs[0].Type);
        Assert.Equal('_', segs[1].Type);
        Assert.Equal('C', segs[2].Type);
    }

    // ── HitMapResult ToString / WriteTo tests ────────────────────────

    [Fact]
    public void HitMapResult_ToString_WithLegend() {
        var result = CacheHitMap.Render(0, 100, Segs((0, 8192)));
        Assert.Equal("H0Cz[100;8092]", result.ToString());
    }

    [Fact]
    public void HitMapResult_ToString_AlwaysHasLegend() {
        // Single segment → SegMin==SegMax → legend still present.
        var result = CacheHitMap.Render(100, 200, NoCache);
        Assert.Equal("M0[200;200]", result.ToString());
    }

    [Fact]
    public void HitMapResult_WriteTo_MatchesToString() {
        var result = CacheHitMap.Render(0, 100, Segs((10100, 100)));
        var sw = new StringWriter();
        result.WriteTo(sw);
        Assert.Equal(result.ToString(), sw.ToString());
    }

    [Fact]
    public void HitMapResult_Empty_WritesNothing() {
        var result = CacheHitMap.Render(0, 0, NoCache);
        Assert.Null(result.Map);
        var sw = new StringWriter();
        result.WriteTo(sw);
        Assert.Equal("", sw.ToString());
    }
}
