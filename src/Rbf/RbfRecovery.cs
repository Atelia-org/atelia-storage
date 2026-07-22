using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf.Internal;
using Atelia.Rbf.ReadCache;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>RBF 离线救援/分析入口。</summary>
public static class RbfRecovery {
    /// <summary>以只读方式打开 Recovery scanner。</summary>
    public static RbfRecoveryScanner OpenReadOnly(string path, RbfCacheMode cacheMode = RbfCacheMode.Slots16) {
        return RbfRecoveryScanner.OpenReadOnly(path, cacheMode);
    }

    /// <summary>将文件截断到 recovery hit 建议的逻辑尾。</summary>
    public static void TruncateToSuggestedTail(string path, RbfRecoveryHit hit) {
        if (!hit.HasTailFence) { throw new InvalidOperationException("Cannot truncate to a recovery hit without a verified TailFence."); }

        long truncateOffset = hit.SuggestedTruncateOffset;
        if (truncateOffset < RbfLayout.HeaderOnlyLength || (truncateOffset & RbfLayout.AlignmentMask) != 0) { throw new ArgumentOutOfRangeException(nameof(hit), truncateOffset, "Suggested truncate offset must be at or after HeaderOnlyLength and 4-byte aligned."); }

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long fileLength = RandomAccess.GetLength(handle);
        if (truncateOffset > fileLength) { throw new InvalidOperationException($"Suggested truncate offset ({truncateOffset}) exceeds file length ({fileLength})."); }

        Span<byte> header = stackalloc byte[RbfLayout.FenceSize];
        int bytesRead = RandomAccess.Read(handle, header, RbfLayout.HeaderFenceOffset);
        if (bytesRead < RbfLayout.FenceSize || !header.SequenceEqual(RbfLayout.Fence)) { throw new InvalidDataException("Invalid RBF file: HeaderFence mismatch."); }

        Span<byte> tailFence = stackalloc byte[RbfLayout.FenceSize];
        bytesRead = RandomAccess.Read(handle, tailFence, truncateOffset - RbfLayout.FenceSize);
        if (bytesRead < RbfLayout.FenceSize || !tailFence.SequenceEqual(RbfLayout.Fence)) { throw new InvalidDataException("Invalid RBF file: suggested TailFence mismatch."); }

        RandomAccess.SetLength(handle, truncateOffset);
    }
}

/// <summary>Recovery scanner 要求的候选校验等级。</summary>
public enum RbfRecoveryValidationLevel {
    /// <summary>要求 TrailerOnly + 前置 Fence + HeadLen == TailLen。默认等级。</summary>
    FrameBoundary = 0,

    /// <summary>只要求尾部 Fence 与 TrailerCodeword 校验通过。</summary>
    TrailerOnly = 1,

    /// <summary>要求 FrameBoundary，且完整 PayloadCrc32C 校验通过。</summary>
    FullFrame = 2
}

/// <summary>Recovery scanner 产出 hit 的置信等级。</summary>
public enum RbfRecoveryConfidence {
    /// <summary>尾部 Fence 与 TrailerCodeword 校验通过。</summary>
    TrailerOnly = 1,

    /// <summary>额外确认前置 Fence 与 HeadLen。</summary>
    FrameBoundary = 2,

    /// <summary>额外确认完整 PayloadCrc32C。</summary>
    FullFrame = 3
}

/// <summary>Recovery scanner 寻找候选 frame 边界的策略。</summary>
public enum RbfRecoveryBoundarySearchStrategy {
    /// <summary>按 4B 步进搜索尾部 Fence，再验证 Fence 前的 TrailerCodeword。默认策略。</summary>
    Fence = 0,

    /// <summary>使用 Rolling CRC32C 直接搜索 TrailerCodeword，可在尾部 Fence 损坏或缺失时救回完整 frame。</summary>
    RollingCrc = 1
}

/// <summary>Recovery scanner 选项。</summary>
public readonly struct RbfRecoveryScanOptions {
    /// <summary>扫描起点（不含）。null 表示从当前文件长度开始。</summary>
    public long? StartOffsetExclusive { get; init; }

    /// <summary>是否跳过 Tombstone 帧。默认 false，即 recovery 路径保留所有物理候选。</summary>
    public bool SkipTombstone { get; init; }

    /// <summary>候选必须达到的校验等级。默认 <see cref="RbfRecoveryValidationLevel.FrameBoundary"/>。</summary>
    public RbfRecoveryValidationLevel ValidationLevel { get; init; }

    /// <summary>寻找候选 frame 边界的策略。默认 <see cref="RbfRecoveryBoundarySearchStrategy.Fence"/>。</summary>
    public RbfRecoveryBoundarySearchStrategy BoundarySearchStrategy { get; init; }

    /// <summary>最多产出多少 hit。0 表示不限制。</summary>
    public int MaxHits { get; init; }
}

/// <summary>Recovery scanner 找到的候选帧。</summary>
public readonly record struct RbfRecoveryHit {
    internal RbfRecoveryHit(RbfFrameInfo info, long? fenceOffset, RbfRecoveryConfidence confidence) {
        Info = info;
        FenceOffset = fenceOffset;
        FenceEndOffset = fenceOffset + RbfLayout.FenceSize;
        FrameEndOffset = info.Ticket.Offset + info.Ticket.Length;
        SuggestedTruncateOffset = FenceEndOffset ?? FrameEndOffset;
        Confidence = confidence;
    }

    /// <summary>候选帧元信息。</summary>
    public RbfFrameInfo Info { get; }

    /// <summary>候选帧尾部 Fence 的起始 offset；当 TailFence 损坏或缺失时为 null。</summary>
    public long? FenceOffset { get; }

    /// <summary>候选帧尾部 Fence 的结束 offset；当 TailFence 损坏或缺失时为 null。</summary>
    public long? FenceEndOffset { get; }

    /// <summary>FrameBytes 结束 offset（不含尾部 Fence）。</summary>
    public long FrameEndOffset { get; }

    /// <summary>建议截断到的位置。若尾部 Fence 存在则为 Fence 后，否则为 FrameBytes 后。</summary>
    public long SuggestedTruncateOffset { get; }

    /// <summary>候选是否带有已验证的尾部 Fence。</summary>
    public bool HasTailFence => FenceOffset.HasValue;

    /// <summary>候选的校验置信等级。</summary>
    public RbfRecoveryConfidence Confidence { get; }
}

/// <summary>RBF 离线 recovery scanner。调用方负责 Dispose。</summary>
public sealed class RbfRecoveryScanner : IDisposable {
    private readonly SafeFileHandle _handle;
    private readonly RandomAccessReader _reader;
    private bool _disposed;

    private RbfRecoveryScanner(SafeFileHandle handle, RandomAccessReader reader, long fileLength) {
        _handle = handle;
        _reader = reader;
        FileLength = fileLength;
    }

    /// <summary>文件打开时的长度。</summary>
    public long FileLength { get; }

    internal RandomAccessReader Reader {
        get {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _reader;
        }
    }

    internal static RbfRecoveryScanner OpenReadOnly(string path, RbfCacheMode cacheMode) {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try {
            long fileLength = RandomAccess.GetLength(handle);
            if (fileLength < RbfLayout.FenceSize) { throw new InvalidDataException("Invalid RBF file: file too short for HeaderFence."); }

            Span<byte> header = stackalloc byte[RbfLayout.FenceSize];
            int bytesRead = RandomAccess.Read(handle, header, RbfLayout.HeaderFenceOffset);
            if (bytesRead < RbfLayout.FenceSize || !header.SequenceEqual(RbfLayout.Fence)) { throw new InvalidDataException("Invalid RBF file: HeaderFence mismatch."); }

            RandomAccessReader reader = cacheMode == RbfCacheMode.Off
                ? new RandomAccessReader(handle)
                : new ReverseReadCache(handle, (int)cacheMode);
            return new RbfRecoveryScanner(handle, reader, fileLength);
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>从尾部向前搜索可验证的候选帧。</summary>
    public RbfRecoverySequence ScanBackward(RbfRecoveryScanOptions options = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateOptions(options, FileLength);
        return new RbfRecoverySequence(_reader, FileLength, options);
    }

    private static void ValidateOptions(RbfRecoveryScanOptions options, long fileLength) {
        if (options.StartOffsetExclusive is { } startOffset && (startOffset < 0 || startOffset > fileLength)) { throw new ArgumentOutOfRangeException(nameof(options), startOffset, "StartOffsetExclusive must be within the file."); }
        if (options.MaxHits < 0) { throw new ArgumentOutOfRangeException(nameof(options), options.MaxHits, "MaxHits must be non-negative."); }
        if (!Enum.IsDefined(options.ValidationLevel)) { throw new ArgumentOutOfRangeException(nameof(options), options.ValidationLevel, "Unknown validation level."); }
        if (!Enum.IsDefined(options.BoundarySearchStrategy)) { throw new ArgumentOutOfRangeException(nameof(options), options.BoundarySearchStrategy, "Unknown boundary search strategy."); }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (_disposed) { return; }
        _reader.Dispose();
        _handle.Dispose();
        _disposed = true;
    }
}

/// <summary>Recovery hit 序列（duck-typed foreach）。</summary>
public ref struct RbfRecoverySequence {
    private readonly RandomAccessReader _reader;
    private readonly long _fileLength;
    private readonly RbfRecoveryScanOptions _options;

    internal RbfRecoverySequence(RandomAccessReader reader, long fileLength, RbfRecoveryScanOptions options) {
        _reader = reader;
        _fileLength = fileLength;
        _options = options;
    }

    /// <summary>获取枚举器。</summary>
    public RbfRecoveryEnumerator GetEnumerator() {
        return new RbfRecoveryEnumerator(_reader, _fileLength, _options);
    }
}

/// <summary>Recovery hit 逆向搜索枚举器。</summary>
public ref struct RbfRecoveryEnumerator {
    private const int RollingCrcChunkSize = 64 * 1024;

    private readonly RandomAccessReader _reader;
    private readonly long _fileLength;
    private readonly RbfRecoveryScanOptions _options;
    private long _nextFenceOffset;
    private long _nextScanEndOffset;
    private int _yielded;
    private RbfRecoveryHit _current;

    internal RbfRecoveryEnumerator(RandomAccessReader reader, long fileLength, RbfRecoveryScanOptions options) {
        _reader = reader;
        _fileLength = fileLength;
        _options = options;
        _nextFenceOffset = GetInitialFenceOffset(fileLength, options.StartOffsetExclusive);
        _nextScanEndOffset = options.StartOffsetExclusive ?? fileLength;
        _yielded = 0;
        _current = default;
    }

    /// <summary>当前 hit。</summary>
    public RbfRecoveryHit Current => _current;

    /// <summary>移动到下一个可验证候选。</summary>
    public bool MoveNext() {
        if (_options.MaxHits > 0 && _yielded >= _options.MaxHits) { return false; }

        return _options.BoundarySearchStrategy switch {
            RbfRecoveryBoundarySearchStrategy.Fence => MoveNextByFence(),
            RbfRecoveryBoundarySearchStrategy.RollingCrc => MoveNextByRollingCrc(),
            _ => false
        };
    }

    private bool MoveNextByFence() {

        while (_nextFenceOffset > RbfLayout.HeaderFenceOffset) {
            long fenceOffset = _nextFenceOffset;
            _nextFenceOffset -= RbfLayout.Alignment;

            if (!TryReadFence(fenceOffset)) { continue; }
            if (!TryCreateHit(fenceOffset, out var hit)) { continue; }

            _current = hit;
            _yielded++;

            long previousFenceOffset = hit.Info.Ticket.Offset - RbfLayout.FenceSize;
            _nextFenceOffset = AlignDown4(previousFenceOffset);
            return true;
        }

        return false;
    }

    private bool MoveNextByRollingCrc() {
        while (TryFindNextByRollingCrc(out var hit)) {
            _current = hit;
            _yielded++;
            _nextScanEndOffset = Math.Max(RbfLayout.HeaderOnlyLength, hit.Info.Ticket.Offset);
            return true;
        }

        return false;
    }

    private static long GetInitialFenceOffset(long fileLength, long? startOffsetExclusive) {
        long start = startOffsetExclusive ?? fileLength;
        long lastPossibleFenceOffset = start - RbfLayout.FenceSize;
        return AlignDown4(lastPossibleFenceOffset);
    }

    private static long AlignDown4(long value) {
        return value < 0 ? -1 : value & ~((long)RbfLayout.AlignmentMask);
    }

    private bool TryReadFence(long fenceOffset) {
        Span<byte> buffer = stackalloc byte[RbfLayout.FenceSize];
        int bytesRead = _reader.Read(buffer, fenceOffset);
        return bytesRead == RbfLayout.FenceSize && buffer.SequenceEqual(RbfLayout.Fence);
    }

    private bool TryFindNextByRollingCrc(out RbfRecoveryHit hit) {
        hit = default;

        long searchStartOffset = Math.Min(_nextScanEndOffset, _fileLength);
        long scanEndOffset = searchStartOffset;
        if (scanEndOffset <= RbfLayout.HeaderOnlyLength) { return false; }

        var scanner = RollingCrc.BackwardScanner(TrailerCodewordHelper.Size);
        byte[] buffer = new byte[Math.Min(RollingCrcChunkSize, (int)Math.Min(int.MaxValue, scanEndOffset - RbfLayout.HeaderOnlyLength))];

        while (scanEndOffset > RbfLayout.HeaderOnlyLength) {
            int requestLength = (int)Math.Min(buffer.Length, scanEndOffset - RbfLayout.HeaderOnlyLength);
            long chunkStartOffset = scanEndOffset - requestLength;
            int bytesRead = _reader.Read(buffer.AsSpan(0, requestLength), chunkStartOffset);
            if (bytesRead <= 0) { return false; }

            ReadOnlySpan<byte> remainChunk = buffer.AsSpan(0, bytesRead);
            while (scanner.TryFindCodeword(remainChunk, out var match)) {
                long trailerOffset = searchStartOffset - match.Processed;
                remainChunk = match.RemainChunk;

                if ((trailerOffset & RbfLayout.AlignmentMask) != 0) { continue; }
                if (!TryCreateHitFromTrailerOffset(trailerOffset, out hit)) { continue; }

                return true;
            }

            scanEndOffset = chunkStartOffset;
        }

        return false;
    }

    private bool TryCreateHit(long fenceOffset, out RbfRecoveryHit hit) {
        hit = default;

        var result = RbfReadImpl.ReadTrailerBefore(_reader, fenceOffset + RbfLayout.FenceSize);
        if (!result.IsSuccess) { return false; }

        RbfFrameInfo info = result.Value;
        if (_options.SkipTombstone && info.IsTombstone) { return false; }

        RbfRecoveryConfidence confidence = RbfRecoveryConfidence.TrailerOnly;
        if (_options.ValidationLevel == RbfRecoveryValidationLevel.TrailerOnly) {
            hit = new RbfRecoveryHit(info, fenceOffset, confidence);
            return true;
        }

        if (!ValidateFrameBoundary(info)) { return false; }
        confidence = RbfRecoveryConfidence.FrameBoundary;

        if (_options.ValidationLevel == RbfRecoveryValidationLevel.FullFrame) {
            var frameResult = info.ReadPooledFrame();
            if (!frameResult.IsSuccess) { return false; }
            frameResult.Value!.Dispose();
            confidence = RbfRecoveryConfidence.FullFrame;
        }

        hit = new RbfRecoveryHit(info, fenceOffset, confidence);
        return true;
    }

    private bool TryCreateHitFromTrailerOffset(long trailerOffset, out RbfRecoveryHit hit) {
        hit = default;

        if (!TryReadFrameInfoFromTrailerOffset(trailerOffset, out var info)) { return false; }
        if (_options.SkipTombstone && info.IsTombstone) { return false; }

        RbfRecoveryConfidence confidence = RbfRecoveryConfidence.TrailerOnly;
        if (_options.ValidationLevel == RbfRecoveryValidationLevel.TrailerOnly) {
            hit = new RbfRecoveryHit(info, TryGetTailFenceOffset(info), confidence);
            return true;
        }

        if (!ValidateFrameBoundary(info)) { return false; }
        confidence = RbfRecoveryConfidence.FrameBoundary;

        if (_options.ValidationLevel == RbfRecoveryValidationLevel.FullFrame) {
            var frameResult = info.ReadPooledFrame();
            if (!frameResult.IsSuccess) { return false; }
            frameResult.Value!.Dispose();
            confidence = RbfRecoveryConfidence.FullFrame;
        }

        hit = new RbfRecoveryHit(info, TryGetTailFenceOffset(info), confidence);
        return true;
    }

    private bool TryReadFrameInfoFromTrailerOffset(long trailerOffset, out RbfFrameInfo info) {
        info = default;

        if (trailerOffset < RbfLayout.HeaderOnlyLength) { return false; }

        Span<byte> trailerBuffer = stackalloc byte[TrailerCodewordHelper.Size];
        int bytesRead = _reader.Read(trailerBuffer, trailerOffset);
        if (bytesRead != TrailerCodewordHelper.Size) { return false; }

        var trailerResult = TrailerCodewordHelper.ParseAndValidate(trailerBuffer);
        if (!trailerResult.IsSuccess) { return false; }

        var trailer = trailerResult.Value;
        if (trailer.TailLen < RbfLayout.MinFrameLength ||
            trailer.TailLen > int.MaxValue ||
            (trailer.TailLen & RbfLayout.AlignmentMask) != 0) { return false; }

        long frameStart = trailerOffset + TrailerCodewordHelper.Size - trailer.TailLen;
        if (frameStart < RbfLayout.HeaderOnlyLength || (frameStart & RbfLayout.AlignmentMask) != 0) { return false; }

        var payloadLenResult = TrailerCodewordHelper.ComputePayloadLength(trailer.TailLen, trailer.TailMetaLen, trailer.PaddingLen);
        if (!payloadLenResult.IsSuccess) { return false; }

        info = new RbfFrameInfo(
            reader: _reader,
            ticket: SizedPtr.Create(frameStart, (int)trailer.TailLen),
            tag: trailer.FrameTag,
            payloadLength: payloadLenResult.Value,
            tailMetaLength: trailer.TailMetaLen,
            isTombstone: trailer.IsTombstone
        );
        return true;
    }

    private long? TryGetTailFenceOffset(RbfFrameInfo info) {
        long tailFenceOffset = info.Ticket.Offset + info.Ticket.Length;
        return TryReadFence(tailFenceOffset) ? tailFenceOffset : null;
    }

    private bool ValidateFrameBoundary(RbfFrameInfo info) {
        long leadingFenceOffset = info.Ticket.Offset - RbfLayout.FenceSize;
        if (leadingFenceOffset < RbfLayout.HeaderFenceOffset || !TryReadFence(leadingFenceOffset)) { return false; }

        Span<byte> headLenBuffer = stackalloc byte[FrameLayout.HeadLenSize];
        int bytesRead = _reader.Read(headLenBuffer, info.Ticket.Offset);
        if (bytesRead != FrameLayout.HeadLenSize) { return false; }

        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(headLenBuffer);
        return headLen == (uint)info.Ticket.Length;
    }
}
