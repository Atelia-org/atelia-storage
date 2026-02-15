using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.ReadCache;

// ai:doc "docs/Rbf/rbf-read-cache.md"
// ai:test "tests/Rbf.Tests/ReadCache/ReverseReadCacheTests.cs"
// ai:test "tests/Rbf.Tests/ReadCache/ReverseReadCacheTests.GetCacheSegments.cs"
// ai:test "tests/Rbf.Tests/ReadCache/ReverseReadCacheTests.Invalidate.cs"
/// <remarks>
/// Memory layout: A single flat <c>byte[]</c> allocation (<c>slotCount × pageSize</c>),
/// plus a fixed 2-page scratch buffer for cross-page reads.
/// Page metadata lives in parallel <c>long[]</c> and <c>int[]</c> arrays.
/// Eviction policy: Clock (second-chance) — a <c>ulong</c> bitmap tracks per-slot reference bits.
/// </remarks>
internal sealed class ReverseReadCache : RandomAccessReader {
    public const int PageShift = 12, PageSize = 1 << PageShift, PageMask = PageSize - 1;
    public const int MinSlotCountShift = 1; // 2^1 = 2 slots minimum
    public const int MaxSlotCountShift = 6; // 2^6 = 64 slots maximum (fits ulong bitmap)
    private const long NoPage = -1;
    private readonly int _slotCount;
    private readonly int _slotMask; // _slotCount - 1, for & instead of %

    #region Page table — slot-indexed parallel arrays
    private readonly byte[] _pageData; // flat storage: slotCount × pageSize
    private readonly long[] _slotPageIndex; // file page index per slot, or NoPage
    private readonly int[] _slotValidBytes; // valid byte count per slot (≤ pageSize)
    private readonly byte[] _crossPageScratch; // 2-page scratch buffer for cross-page reads
    #endregion

    #region Clock state
    private int _hand; // Clock-hand eviction cursor (slot index, wraps with _slotMask)
    private ulong _refBits; // per-slot "referenced" bit for Clock second-chance
    #endregion

    public ReverseReadCache(SafeFileHandle file, int slotCountShift = 4) : base(file) {
        Debug.Assert(MinSlotCountShift <= slotCountShift && slotCountShift <= MaxSlotCountShift);
        slotCountShift = Math.Clamp(slotCountShift, MinSlotCountShift, MaxSlotCountShift);

        _slotCount = 1 << slotCountShift;
        _slotMask = _slotCount - 1;

        _pageData = new byte[_slotCount * PageSize];
        _slotPageIndex = new long[_slotCount];
        _slotValidBytes = new int[_slotCount];
        _crossPageScratch = new byte[2 * PageSize];
        Array.Fill(_slotPageIndex, NoPage);
    }

    protected override int ReadWithCache(long offset, Span<byte> buffer) {
        int tailFilled = FillTrailerWithCache(offset, buffer);
        buffer = buffer[..^tailFilled];

        int headFilled = FillHeaderWithCache(offset, buffer);
        offset += headFilled;
        buffer = buffer[headFilled..];

        if (buffer.Length == 0) { return headFilled + tailFilled; }

        long stPageIdx = offset >> PageShift;
        long edPageIdx = (offset + buffer.Length - 1) >> PageShift;

        // 超过2页则走passthrough
        if (edPageIdx - stPageIdx > 1) {
            int passthroughReaded = RawRead(offset, buffer);
            return headFilled + passthroughReaded + tailFilled;
        }

        Span<byte> midCached;
        int midReaded = (edPageIdx == stPageIdx)
            ? ReadOnePageIntoCache(stPageIdx, out midCached)
            : ReadCrossPageWithScratch(stPageIdx, offset, buffer.Length, out midCached);

        int offsetInStPage = (int)(offset & PageMask);
        int midAvailable = Math.Max(0, midReaded - offsetInStPage);
        int midCopied = Math.Min(midAvailable, buffer.Length);
        midCached.Slice(offsetInStPage, midCopied).CopyTo(buffer);
        return headFilled + midCopied + tailFilled;
    }

    /// <summary>向<c>outBuffer</c>尾部，尽量多的连续填充已缓存数据。</summary>
    /// <returns>实际填充的字节数。</returns>
    private int FillTrailerWithCache(long offset, Span<byte> outBuffer) {
        if (outBuffer.Length == 0) { return 0; }

        long rangeEnd = offset + outBuffer.Length; // exclusive end in file
        int filled = 0;

        while (filled < outBuffer.Length) {
            long tailFileOff = rangeEnd - filled - 1; // last unfilled byte
            long pageIdx = tailFileOff >> PageShift;
            int slot = FindCachedSlot(pageIdx);
            if (slot < 0) { break; }

            long pageStart = pageIdx << PageShift;
            long pageValidEnd = pageStart + _slotValidBytes[slot]; // exclusive
            if (tailFileOff >= pageValidEnd) { break; /* cache doesn't cover this byte */ }

            // Copy region: [regionStart, tailFileOff + 1)  —— 本页与未填充区域的交集
            long regionStart = Math.Max(offset, pageStart);
            int copyLen = (int)(tailFileOff + 1 - regionStart);
            int srcInPage = (int)(regionStart - pageStart);
            int dstInBuf = (int)(regionStart - offset);

            _pageData.AsSpan(slot * PageSize + srcInPage, copyLen).CopyTo(outBuffer.Slice(dstInBuf));
            filled += copyLen;

            // regionStart > pageStart 意味着 buffer 起始在本页内部，已触及 outBuffer 头部
            if (regionStart > pageStart) { break; }
        }

        return filled;
    }

    /// <summary>向<c>outBuffer</c>头部，尽量多的连续填充已缓存数据。</summary>
    /// <returns>实际填充的字节数。</returns>
    private int FillHeaderWithCache(long offset, Span<byte> outBuffer) {
        if (outBuffer.Length == 0) { return 0; }

        int filled = 0;

        while (filled < outBuffer.Length) {
            long headFileOff = offset + filled; // first unfilled byte
            long pageIdx = headFileOff >> PageShift;
            int slot = FindCachedSlot(pageIdx);
            if (slot < 0) { break; }

            int inPageOff = (int)(headFileOff & PageMask);
            int avail = _slotValidBytes[slot] - inPageOff;
            if (avail <= 0) { break; /* EOF within cached page */ }

            int toCopy = Math.Min(avail, outBuffer.Length - filled);
            _pageData.AsSpan(slot * PageSize + inPageOff, toCopy).CopyTo(outBuffer.Slice(filled));
            filled += toCopy;
        }

        return filled;
    }

    // ── Slot lookup ─────────────────────────────────────────────────

    /// <summary>在页表中查找已缓存的页，返回 slot 索引；未命中返回 -1。</summary>
    private int FindCachedSlot(long pageIndex) {
        for (int slot = _slotCount; --slot >= 0;) {
            if (_slotPageIndex[slot] == pageIndex) {
                _refBits |= 1UL << slot; // Clock: mark referenced
                return slot;
            }
        }
        return -1;
    }

    protected override List<OffsetLength>? GetCacheSegments() {
        var segments = new List<OffsetLength>(_slotCount);
        for (int i = _slotCount; --i >= 0;) {
            if (_slotPageIndex[i] != NoPage && _slotValidBytes[i] > 0) {
                segments.Add(new OffsetLength(_slotPageIndex[i] << PageShift, _slotValidBytes[i]));
            }
        }
        return segments;
    }

    private void InvalidateSlot(int slot) {
        _slotPageIndex[slot] = NoPage;
        _slotValidBytes[slot] = 0;
        _refBits &= ~(1UL << slot);
    }

    protected override void OnInvalidateFrom(long fileOffset) {
        long boundaryPage = fileOffset >> PageShift;
        for (int i = _slotCount; --i >= 0;) {
            if (_slotPageIndex[i] != NoPage && _slotPageIndex[i] >= boundaryPage) {
                InvalidateSlot(i);
            }
        }
    }

    protected override void OnFileLengthChanged(long newLength) {
        long boundaryPage = newLength >> PageShift;
        for (int i = _slotCount; --i >= 0;) {
            if (_slotPageIndex[i] == NoPage) { continue; }
            if (_slotPageIndex[i] >= boundaryPage || _slotValidBytes[i] < PageSize) {
                InvalidateSlot(i);
            }
        }
    }

    private int AcquireEvictionSlot() {
        // Clock sweep: skip referenced slots, giving each a second chance
        while ((_refBits & (1UL << _hand)) != 0) {
            _refBits &= ~(1UL << _hand);
            _hand = (_hand + 1) & _slotMask;
        }

        int slot = _hand;
        _hand = (_hand + 1) & _slotMask;
        return slot;
    }

    /// <summary>读取1页数据并放入缓存。Clock 驱逐（跳过被引用的 slot）。</summary>
    /// <param name="pageIndex">页索引号。</param>
    /// <param name="cached">输出：该 slot 整页的 Span（PageSize 长）。</param>
    /// <returns>实际读取到的字节数。有短读可能。</returns>
    private int ReadOnePageIntoCache(long pageIndex, out Span<byte> cached) {
        int slot = AcquireEvictionSlot();

        cached = _pageData.AsSpan(slot * PageSize, PageSize);
        int bytesRead = RawRead(pageIndex << PageShift, cached);
        if (0 < bytesRead) {
            _slotPageIndex[slot] = pageIndex;
            _slotValidBytes[slot] = bytesRead;
            // ref bit already clear — new page not yet re-accessed
        }
        else {
            // 文件意外的被其他进程截断了才会进此分支
            InvalidateSlot(slot);
        }
        return bytesRead;
    }

    /// <summary>
    /// 读取跨页数据到 scratch，并仅将起始页放入缓存（Clock 驱逐 1 个 slot）。
    /// </summary>
    /// <param name="pageIndex">起始页索引号。</param>
    /// <param name="offset">本次读取在文件中的起始偏移。</param>
    /// <param name="length">本次读取的目标长度。</param>
    /// <param name="cached">输出：覆盖两页的连续 Span（2 × PageSize 长）。</param>
    /// <returns>实际读取到的字节数。有短读可能。</returns>
    private int ReadCrossPageWithScratch(long pageIndex, long offset, int length, out Span<byte> cached) {
        long pageStart = pageIndex << PageShift;
        int readLength = (int)Math.Min(2 * PageSize, offset + length - pageStart);

        cached = _crossPageScratch.AsSpan(0, readLength);
        int bytesRead = RawRead(pageStart, cached);

        // 只缓存头一页；若短读为 0 则跳过缓存，避免无谓驱逐
        int validBytes = Math.Min(bytesRead, PageSize);
        if (validBytes > 0) {
            int slot = AcquireEvictionSlot();
            cached[..validBytes].CopyTo(_pageData.AsSpan(slot * PageSize, validBytes));
            _slotPageIndex[slot] = pageIndex;
            _slotValidBytes[slot] = validBytes;
            // ref bit already clear — new page not yet re-accessed
        }
        return bytesRead;
    }
}
