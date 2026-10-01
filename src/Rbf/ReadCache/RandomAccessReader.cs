using System.Diagnostics;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.ReadCache;
/// <summary>
/// 文件读取缓存基类。核心扩展点是override <see cref="ReadWithCache"/>。派生类通过调用<see cref="RawRead"/>实际读取文件内容。
/// 为各派生实现提供统一的Log文件功能，通过调用<see cref="SetupLogger"/>启用。通过override <see cref="GetCacheSegments"/>可以Log更详细的缓存MHC(Miss/Hit/Cache)图。
/// </summary>
/// <remarks>Threading: not thread-safe.</remarks>
internal class RandomAccessReader : IDisposable {
    // Not owned: caller manages handle lifetime.
    private readonly SafeFileHandle _file;
    private readonly ReadLogger _logger;
    private bool _disposed;
    // The single irreversible write-fault fact shared by the facade, old frame handles and scans.
    private bool _writeFaulted;
    private RbfReadMetrics? _activeReadMetrics;
    private long _logicalReadOffset;
    private int _logicalReadRequested;
    private readonly long? _fixedEof;
    private readonly Action<int>? _beforeRead;
    private readonly CancellationToken _cancellationToken;

    internal RandomAccessReader(SafeFileHandle file, long? fixedEof = null, Action<int>? beforeRead = null, CancellationToken cancellationToken = default) {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        if (fixedEof < 0) { throw new ArgumentOutOfRangeException(nameof(fixedEof)); }
        _fixedEof = fixedEof;
        _beforeRead = beforeRead;
        _cancellationToken = cancellationToken;
        _logger = new ReadLogger();
    }

    // A range/Fence check only. Continuous physical membership belongs to the caller's prefix proof.
    internal AteliaError? ValidateTicket(SizedPtr ticket) {
        EnsureUsable();
        if (_fixedEof is not long eof) { return null; }
        _cancellationToken.ThrowIfCancellationRequested();
        if (ticket.Offset < RbfLayout.FirstFrameOffset || ticket.Length < RbfLayout.MinFrameLength ||
            ticket.Offset > eof || (long)ticket.Length + RbfLayout.FenceSize > eof - ticket.Offset) {
            return new RbfArgumentError("Ticket and its complete Fence must lie within the fixed candidate EOF.");
        }
        Span<byte> fence = stackalloc byte[RbfLayout.FenceSize];
        if (Read(fence, ticket.EndOffsetExclusive) != fence.Length || !fence.SequenceEqual(RbfLayout.Fence)) {
            return new RbfFramingError("Candidate ticket Fence is missing or corrupted.");
        }
        return null;
    }

    private int BoundedLength(long offset, int length) {
        if (_fixedEof is not long eof) { return length; }
        return offset >= eof ? 0 : (int)Math.Min(length, eof - offset);
    }

    // Test/diagnostic observation at the actual reader boundary; raw=true includes cache prefetch.
    internal Action<long, int, bool>? ReadObserver { get; set; }
    internal Action<int>? BufferRentObserver { get; set; }

    public SafeFileHandle File => _file;
    public bool IsDisposed => _disposed;

    public void SetupLogger(ReadLogger.Params loggerParams) {
        ThrowIfDisposed();
        _logger.Setup(loggerParams);
    }

    public int Read(Span<byte> buffer, long offset) {
        ThrowIfDisposed();
        if (offset < 0) { throw new ArgumentOutOfRangeException(nameof(offset)); }
        _cancellationToken.ThrowIfCancellationRequested();
        buffer = buffer[..BoundedLength(offset, buffer.Length)];
        if (buffer.Length == 0) { return 0; }
        Debug.Assert(offset <= long.MaxValue - buffer.Length);

        ReadObserver?.Invoke(offset, buffer.Length, false);
        var cacheSegments = _logger.NeedCacheSegments ? GetCacheSegments() : null;
        _logger.OnReadBegin(offset, buffer.Length, cacheSegments);
        var previousMetrics = _activeReadMetrics;
        long previousOffset = _logicalReadOffset;
        int previousRequested = _logicalReadRequested;
        var metrics = RbfReadMetrics.Current;
        _activeReadMetrics = metrics;
        _logicalReadOffset = offset;
        _logicalReadRequested = buffer.Length;
        metrics?.ReadRequested(buffer.Length);
        try {
            int bytesRead = ReadWithCache(offset, buffer);
            metrics?.ReadReturned(bytesRead);
            _logger.OnReadFinish(bytesRead);
            return bytesRead;
        }
        finally {
            _activeReadMetrics = previousMetrics;
            _logicalReadOffset = previousOffset;
            _logicalReadRequested = previousRequested;
        }
    }

    protected int RawRead(long offset, Span<byte> buffer) {
        _cancellationToken.ThrowIfCancellationRequested();
        buffer = buffer[..BoundedLength(offset, buffer.Length)];
        if (buffer.IsEmpty) { return 0; }
        _beforeRead?.Invoke(buffer.Length);
        ReadObserver?.Invoke(offset, buffer.Length, true);
        var metrics = _activeReadMetrics;
        metrics?.RawRequested(buffer.Length);
        var startTick = Stopwatch.GetTimestamp();
        int bytesRead = RandomAccess.Read(_file, buffer, offset);
        long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
        metrics?.RawReturned(offset, bytesRead, _logicalReadOffset, _logicalReadRequested);
        _logger.OnRawRead(offset, buffer.Length, bytesRead, elapsedTicks);
        return bytesRead;
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        try { DisposeCache(); }
        finally { _logger.Dispose(); }
    }

    // ── Cache invalidation ──────────────────────────────────────────

    /// <summary>
    /// 通知缓存：文件从 <paramref name="fileOffset"/> 开始的内容可能已变化，
    /// 与 [fileOffset, ∞) 重叠的缓存条目应当失效。
    /// </summary>
    public void InvalidateFrom(long fileOffset) {
        ThrowIfDisposed();
        if (fileOffset < 0) { throw new ArgumentOutOfRangeException(nameof(fileOffset)); }
        OnInvalidateFrom(fileOffset);
    }

    /// <summary>
    /// 通知缓存：文件长度已变化为 <paramref name="newLength"/>。
    /// 短读页和超出新长度的缓存条目应当失效。
    /// </summary>
    public void NotifyFileLengthChanged(long newLength) {
        ThrowIfDisposed();
        if (newLength < 0) { throw new ArgumentOutOfRangeException(nameof(newLength)); }
        OnFileLengthChanged(newLength);
    }

    /// <summary>派生类重写以失效 [fileOffset, ∞) 范围内的缓存条目。</summary>
    protected virtual void OnInvalidateFrom(long fileOffset) { }

    /// <summary>派生类重写以失效因文件长度变化而过期的缓存条目。</summary>
    protected virtual void OnFileLengthChanged(long newLength) { }

    protected void ThrowIfDisposed() {
        EnsureUsable();
    }

    internal void MarkWriteFaulted() => _writeFaulted = true;

    internal void EnsureUsable() {
        if (_disposed) { throw new ObjectDisposedException(GetType().Name); }
        if (_writeFaulted) { throw new InvalidOperationException("The RBF writer has failed. Dispose the file and reopen it before further operations."); }
    }

    protected virtual void DisposeCache() { }

    /// <summary>返回当前缓存中的数据段分布，用于 Logger 生成 HMC 字符画。</summary>
    /// <returns>缓存段列表，或 null 表示不参与 HMC 可视化。</returns>
    protected virtual List<OffsetLength>? GetCacheSegments() => null;

    /// <summary>核心扩展点，派生类通过重写此方法实现各自的缓存/预读逻辑</summary>
    /// <see cref="RawRead"/>
    /// <returns>实际读取到的字节数，用于外部短读判断</returns>
    protected virtual int ReadWithCache(long offset, Span<byte> buffer) => RawRead(offset, buffer);
}
