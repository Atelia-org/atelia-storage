using System.Text;

namespace Atelia.Rbf.ReadCache;

// ai:test "tests/Rbf.Tests/CacheHitMapTests.cs"
/// <summary>请求范围与缓存分布的拓扑关系字符画。</summary>
/// <remark>
/// 区段类型：<c>M</c>=miss, <c>H</c>=hit, <c>C</c>=unused cache, <c>_</c>=gap。
/// 相邻同类型子区间合并为一个逻辑段。每段编码为 type+level 两字符：
/// type ∈ {M,H,C,_}，level ∈ [0-9a-z]（36 级对数刻度，最短段=0，最长段=z）。
/// </remark>
/// <param name="Map">由 type+level 字符对组成的字符画（如 <c>H0MzC0</c>）。</param>
/// <param name="SegMin">最短段的字节数。</param>
/// <param name="SegMax">最长段的字节数。</param>
internal readonly record struct CacheHitMap(string? Map, long SegMin, long SegMax) {

    /// <summary>写入紧凑格式：<c>CHMHC[100;8092]</c>；Map 为 null 时不输出。</summary>
    public void WriteTo(TextWriter writer) {
        if (Map is null) { return; }
        writer.Write(Map);
        writer.Write('[');
        writer.Write(SegMin);
        writer.Write(';');
        writer.Write(SegMax);
        writer.Write(']');
    }

    /// <inheritdoc/>
    public override string ToString() {
        if (Map is null) { return ""; }
        return $"{Map}[{SegMin};{SegMax}]";
    }

    /// <summary>一个带类型和长度的合并段。</summary>
    internal readonly record struct Segment(char Type, long Length);

    // ── Phase 1: Sweep ──────────────────────────────────────────────

    /// <summary>将请求区间与缓存段叠加，产出合并后的 <see cref="Segment"/> 序列。</summary>
    /// <remark>
    /// 利用请求为单一连续区间的特性：先将缓存段排序合并为不重叠序列，
    /// 然后线性扫描，将每个缓存段及其前方间隙按请求边界切分为 H/M/C/_ 类型。
    /// 相邻同类型子区间自动合并（长度累加）。
    /// </remark>
    internal static List<Segment> Sweep(
        long reqOffset,
        long reqLength,
        List<OffsetLength> cacheSegments
    ) {
        var merged = MergeCache(cacheSegments);
        var segments = new List<Segment>();
        var req = new StartEnd(reqOffset, reqOffset + reqLength);

        if (reqLength <= 0 && merged.Count == 0) { return segments; }

        // Start from the earliest boundary among request and cache.
        long cursor;
        if (merged.Count > 0) {
            cursor = reqLength > 0 ? Math.Min(req.Start, merged[0].Start) : merged[0].Start;
        }
        else {
            cursor = req.Start;
        }

        for (int i = 0; i < merged.Count; i++) {
            var cache = merged[i];

            // Gap before this cache interval.
            if (cursor < cache.Start) {
                EmitInterval(cursor, cache.Start, req, 'M', '_', segments);
            }

            // The cache interval itself.
            long actualStart = Math.Max(cursor, cache.Start);
            if (actualStart < cache.End) {
                EmitInterval(actualStart, cache.End, req, 'H', 'C', segments);
            }

            cursor = Math.Max(cursor, cache.End);
        }

        // Remaining request tail not covered by any cache.
        if (reqLength > 0 && cursor < req.End) {
            EmitInterval(cursor, req.End, req, 'M', '_', segments);
        }

        return segments;
    }

    // ── Phase 2: Render ─────────────────────────────────────────────

    /// <summary>生成请求范围与缓存段的拓扑关系字符画。</summary>
    /// <param name="reqOffset">请求起始偏移</param>
    /// <param name="reqLength">请求长度</param>
    /// <param name="cacheSegments">缓存中的数据段 (Offset, Length) 列表</param>
    /// <returns>包含字符画及对数刻度参考值的 <see cref="CacheHitMap"/>。</returns>
    public static CacheHitMap Render(
        long reqOffset,
        long reqLength,
        List<OffsetLength> cacheSegments
    ) {
        var segments = Sweep(reqOffset, reqLength, cacheSegments);
        if (segments.Count == 0) { return default; }

        // Find Lmin / Lmax across all segments (including gaps).
        long lMin = long.MaxValue, lMax = 0;
        foreach (var seg in segments) {
            if (seg.Length < lMin) { lMin = seg.Length; }
            if (seg.Length > lMax) { lMax = seg.Length; }
        }

        double logDen = lMax > lMin ? Math.Log((double)lMax / lMin) : 0;

        var sb = new StringBuilder(segments.Count * 2);
        foreach (var seg in segments) {
            sb.Append(seg.Type);
            sb.Append(ScaleToLevel(seg.Length, lMin, logDen));
        }
        return new(sb.ToString(), lMin, lMax);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// 将区间 [from, to) 按请求边界 <paramref name="request"/> 切分，
    /// 落在请求内的部分标 <paramref name="typeInside"/>，
    /// 落在请求外的部分标 <paramref name="typeOutside"/>。
    /// 产出至多 3 片，通过 <see cref="AppendSegment"/> 自动合并相邻同类型。
    /// </summary>
    private static void EmitInterval(
        long from, long to,
        StartEnd request,
        char typeInside, char typeOutside,
        List<Segment> segments
    ) {
        // Before request.
        if (from < request.Start) {
            long end = Math.Min(to, request.Start);
            AppendSegment(typeOutside, end - from, segments);
            from = end;
        }
        // Inside request.
        if (from < to && from < request.End) {
            long end = Math.Min(to, request.End);
            AppendSegment(typeInside, end - from, segments);
            from = end;
        }
        // After request.
        if (from < to) {
            AppendSegment(typeOutside, to - from, segments);
        }
    }

    /// <summary>追加段到列表；若末尾段类型相同则合并（长度累加）。</summary>
    private static void AppendSegment(char type, long length, List<Segment> segments) {
        if (length <= 0) { return; }
        if (segments.Count > 0 && segments[^1].Type == type) {
            var prev = segments[^1];
            segments[^1] = new(type, prev.Length + length);
        }
        else {
            segments.Add(new(type, length));
        }
    }

    /// <summary>将缓存段排序并合并重叠/相邻区间为不重叠有序列表。</summary>
    private static List<StartEnd> MergeCache(List<OffsetLength> cacheSegments) {
        var result = new List<StartEnd>();
        if (cacheSegments.Count == 0) { return result; }

        // Collect valid (non-empty) segments into a sortable copy.
        var valid = new List<OffsetLength>(cacheSegments.Count);
        foreach (var seg in cacheSegments) {
            if (seg.Length > 0) { valid.Add(seg); }
        }
        if (valid.Count == 0) { return result; }

        valid.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        long curStart = valid[0].Offset;
        long curEnd = valid[0].End;

        for (int i = 1; i < valid.Count; i++) {
            if (valid[i].Offset <= curEnd) {
                curEnd = Math.Max(curEnd, valid[i].End);
            }
            else {
                result.Add(new(curStart, curEnd));
                curStart = valid[i].Offset;
                curEnd = valid[i].End;
            }
        }
        result.Add(new(curStart, curEnd));
        return result;
    }

    /// <summary>对数刻度映射：最短段→'0'，最长段→'z'（36 级）。</summary>
    private static char ScaleToLevel(long length, long lMin, double logDen) {
        if (logDen <= 0) { return '0'; }
        double ratio = Math.Log((double)length / lMin) / logDen;
        int level = (int)(ratio * 35 + 0.5);
        level = Math.Clamp(level, 0, 35);
        return level < 10 ? (char)('0' + level) : (char)('a' + level - 10);
    }
}
