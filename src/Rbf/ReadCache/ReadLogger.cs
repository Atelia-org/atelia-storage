using System.Diagnostics;

namespace Atelia.Rbf.ReadCache;

internal sealed class ReadLogger : IDisposable {

    /// <summary>日志参数。<c>LogPath</c> 为 null 表示禁用日志。</summary>
    internal sealed record Params(
        string? LogPath,
        bool Append = true,
        int FlushEvery = 0,
        string? Metadata = null
    );

    private StreamWriter? _writer;
    private string? _logPath;
    private string? _metadata;
    private int _flushEvery;
    private long _sequence;
    private int _pendingSinceFlush;

    private Params? _pendingSetup;

    private readonly Stopwatch _stopwatch = new();

    // Per-Read accumulators — reset in OnReadBegin, written in OnReadFinish
    private long _currentSeq;
    private long _currentOffset;
    private int _currentRequested;
    private int _rawCount;
    private long _rawBytesTotal;
    private long _ioCostTicksTotal;
    private CacheHitMap _cachedHitMap; // 在 OnReadBegin 时计算并缓存

    /// <summary>日志是否正在写入。用于调用方跳过昂贵的诊断数据收集。</summary>
    public bool NeedCacheSegments => (_pendingSetup is not null)
        ? !string.IsNullOrWhiteSpace(_pendingSetup.LogPath)
        : _writer is not null;

    /// <summary>
    /// 缓存日志参数，延迟到下一次 <see cref="OnReadBegin"/> 时生效，
    /// 保证 writer 切换只发生在读取周期之间。
    /// </summary>
    public void Setup(Params loggerParams) {
        _pendingSetup = loggerParams;
    }

    private void ApplyPendingSetup() {
        var p = _pendingSetup;
        if (p is null) { return; }
        _pendingSetup = null;

        _flushEvery = Math.Max(0, p.FlushEvery);

        string? logPath = string.IsNullOrWhiteSpace(p.LogPath) ? null : p.LogPath;
        bool isLogPathChanged = _logPath != logPath;

        string? metadata = string.IsNullOrWhiteSpace(p.Metadata) ? null : p.Metadata;
        bool shouldWriteHeader = isLogPathChanged || _metadata != metadata;

        if (isLogPathChanged) {
            if (_writer is not null) { DisposeWriter(); }
            if (logPath is not null) {
                CreateWriter(logPath, p.Append);
            }
            _pendingSinceFlush = 0;
            _logPath = logPath;
        }

        if (shouldWriteHeader && _writer is not null) {
            WriteHeader(metadata);
        }
        _metadata = metadata;
    }

    private void DisposeWriter() {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    private void CreateWriter(string logPath, bool append) {
        FileMode mode = append ? FileMode.Append : FileMode.Create;
        var stream = new FileStream(logPath, mode, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream);
    }

    /// <summary>输出 CSV 段头（版本+metadata 行 + 列名行）。logPath 或 metadata 变化时调用。</summary>
    private void WriteHeader(string? metadata) {
        Debug.Assert(_writer is not null);
        _writer.Write("#v1 tickFreq=");
        _writer.Write(Stopwatch.Frequency);
        if (!string.IsNullOrEmpty(metadata)) {
            _writer.Write(' ');
            _writer.Write(metadata);
        }
        _writer.WriteLine();
        _writer.WriteLine("seq,offset,requested,bytesRead,rawCount,rawBytes,ioTicks,cacheTicks,hitmap");
    }

    private void CheckFlush() {
        if (_writer is not null && _flushEvery > 0 && ++_pendingSinceFlush >= _flushEvery) {
            _writer.Flush();
            _pendingSinceFlush = 0;
        }
    }

    public void OnReadBegin(long offset, int requested, List<OffsetLength>? cacheSegments = null) {
        ApplyPendingSetup();
        _currentSeq = _sequence++; // 不论_writer是否存在，不影响内部的读取计数器。
        _currentOffset = offset;
        _currentRequested = requested;
        _rawCount = 0;
        _rawBytesTotal = 0;
        _ioCostTicksTotal = 0;

        // 立即计算并缓存 HitMap，反映读取开始时的缓存状态
        _cachedHitMap = (cacheSegments is not null)
            ? CacheHitMap.Render(offset, requested, cacheSegments)
            : default;

        _stopwatch.Restart();
    }

    public void OnRawRead(long offset, int requested, int bytesRead, long costTick) {
        _stopwatch.Stop();
        _rawCount++;
        _rawBytesTotal += bytesRead;
        _ioCostTicksTotal += costTick;
        _stopwatch.Start();
    }

    public void OnReadFinish(int bytesRead) {
        _stopwatch.Stop();
        long cacheTicks = _stopwatch.ElapsedTicks;
        if (_writer is not null) {
            _writer.Write(_currentSeq);
            _writer.Write(',');
            _writer.Write(_currentOffset);
            _writer.Write(',');
            _writer.Write(_currentRequested);
            _writer.Write(',');
            _writer.Write(bytesRead);
            _writer.Write(',');
            _writer.Write(_rawCount);
            _writer.Write(',');
            _writer.Write(_rawBytesTotal);
            _writer.Write(',');
            _writer.Write(_ioCostTicksTotal);
            _writer.Write(',');
            _writer.Write(cacheTicks);
            _writer.Write(',');
            _cachedHitMap.WriteTo(_writer);
            _writer.WriteLine();
            CheckFlush();
        }
    }

    public void Dispose() {
        DisposeWriter();
    }
}
