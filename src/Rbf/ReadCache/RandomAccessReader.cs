using System.Diagnostics;
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

    internal RandomAccessReader(SafeFileHandle file) {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _logger = new ReadLogger();
    }

    public SafeFileHandle File => _file;
    public bool IsDisposed => _disposed;

    public void SetupLogger(ReadLogger.Params loggerParams) {
        ThrowIfDisposed();
        _logger.Setup(loggerParams);
    }

    public int Read(long offset, Span<byte> buffer) {
        ThrowIfDisposed();
        if (offset < 0) { throw new ArgumentOutOfRangeException(nameof(offset)); }
        if (buffer.Length == 0) { return 0; }
        Debug.Assert(offset <= long.MaxValue - buffer.Length);

        var cacheSegments = _logger.NeedCacheSegments ? GetCacheSegments() : null;
        _logger.OnReadBegin(offset, buffer.Length, cacheSegments);
        int bytesRead = ReadWithCache(offset, buffer);
        _logger.OnReadFinish(bytesRead);
        return bytesRead;
    }

    protected int RawRead(long offset, Span<byte> buffer) {
        var startTick = Stopwatch.GetTimestamp();
        int bytesRead = RandomAccess.Read(_file, buffer, offset);
        _logger.OnRawRead(offset, buffer.Length, bytesRead, Stopwatch.GetTimestamp() - startTick);
        return bytesRead;
    }

    public void Dispose() {
        if (_disposed) { return; }
        DisposeCache();
        _logger.Dispose();
        _disposed = true;
    }

    protected void ThrowIfDisposed() {
        if (_disposed) { throw new ObjectDisposedException(nameof(RandomAccessReader)); }
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
