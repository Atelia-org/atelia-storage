using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;

namespace Atelia.Rbf.Internal;

internal enum RbfPrefixStatus {
    Complete, IncompleteHeader, IncompleteSuffix, Invalid, Unresolved,
    BudgetExceeded, Cancelled, IoFailure
}

internal sealed record RbfPrefixInspection(
    RbfPrefixStatus Status, long OriginalLength, long LastCompleteBoundary,
    long IssueOffset, long ReadRequests, long RequestedBytes, long ReturnedBytes, string Detail);

/// <summary>Physical evidence only: no repair authority. Caller must hold stable, exclusive input ownership.</summary>
/// <remarks>Scans forward from the header, without cache/prefetch or payload interpretation.
/// Unsupported header bytes are Invalid. Unresolved is unfinished qualification, not a discard candidate.</remarks>
internal static class RbfPrefixInspector {
    internal const int ChunkSize = 64 * 1024;

    internal static RbfPrefixInspection Inspect(string path, long readBudget = long.MaxValue, CancellationToken cancellationToken = default) {
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        try {
            // Buffer size 1 disables FileStream buffering; no reads beyond the explicitly requested range.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            return Inspect(source, readBudget, cancellationToken);
        }
        catch (OperationCanceledException ex) {
            return new(RbfPrefixStatus.Cancelled, -1, 0, 0, 0, 0, 0, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new(RbfPrefixStatus.IoFailure, -1, 0, 0, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>Borrowed seekable stream; leaves it open. Zero before captured EOF is an I/O failure.</summary>
    internal static RbfPrefixInspection Inspect(Stream source, long readBudget = long.MaxValue, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        if (!source.CanRead || !source.CanSeek) { throw new ArgumentException("Readable seekable source required.", nameof(source)); }
        var scan = new Scan(source, readBudget, cancellationToken);
        return scan.Run();
    }

    /// <summary>Validate a suffix whose frame start was established by continuous metadata membership.
    /// Borrows the source; I/O exceptions propagate to the online opener.</summary>
    internal static RbfPrefixInspection InspectSuffix(Stream source, long frameStart) {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) { throw new ArgumentException("Readable seekable source required.", nameof(source)); }
        if (frameStart < RbfLayout.FirstFrameOffset || frameStart > source.Length || (frameStart & RbfLayout.AlignmentMask) != 0) {
            throw new ArgumentOutOfRangeException(nameof(frameStart));
        }
        return new Scan(source, long.MaxValue, default, frameStart, propagateIo: true).Run();
    }

    private sealed class Stop(RbfPrefixStatus status, string detail) : Exception(detail) {
        internal RbfPrefixStatus Status { get; } = status;
    }

    private sealed class Scan(Stream source, long budget, CancellationToken cancellation,
        long? suffixStart = null, bool propagateIo = false) {
        private long _length = -1, _boundary, _position, _requests, _requested, _returned;
        private RbfPrefixInspection Result(RbfPrefixStatus status, long issue, string detail) =>
            new(status, _length, _boundary, issue, _requests, _requested, _returned, detail);

        private void Read(Span<byte> destination) {
            while (!destination.IsEmpty) {
                cancellation.ThrowIfCancellationRequested();
                int count = (int)Math.Min(destination.Length, budget - _requested);
                if (count == 0) { throw new Stop(RbfPrefixStatus.BudgetExceeded, "Read budget exhausted."); }
                _requests++;
                _requested += count; // Budget charges every request, including retry after a short read.
                int actual = source.Read(destination[..count]);
                _returned += actual;
                _position += actual;
                if (actual == 0) {
                    if (propagateIo) { throw new IOException("Unexpected EOF before captured length."); }
                    throw new Stop(RbfPrefixStatus.IoFailure, "Unexpected EOF before captured length.");
                }
                destination = destination[actual..];
            }
        }

        internal RbfPrefixInspection Run() {
            try {
                cancellation.ThrowIfCancellationRequested();
                _length = source.Length;
                source.Position = suffixStart ?? 0;
                _position = source.Position;
                Span<byte> small = stackalloc byte[16];
                if (suffixStart is null) {
                    int headerCount = (int)Math.Min(_length, RbfLayout.HeaderOnlyLength);
                    Read(small[..headerCount]);
                    if (!small[..headerCount].SequenceEqual(RbfLayout.Fence[..headerCount])) {
                        return Result(RbfPrefixStatus.Invalid, 0, "Unsupported or contradictory HeaderFence.");
                    }
                    if (headerCount < RbfLayout.HeaderOnlyLength) {
                        return Result(RbfPrefixStatus.IncompleteHeader, 0, "HeaderFence prefix only.");
                    }
                }
                _boundary = suffixStart ?? RbfLayout.HeaderOnlyLength;
                byte[] chunk = new byte[ChunkSize];
                Span<byte> last = stackalloc byte[3];
                Span<byte> zeros = stackalloc byte[3];
                zeros.Clear();
                Span<byte> expectedCrc = stackalloc byte[4];
                Span<byte> expectedTrailer = stackalloc byte[TrailerCodewordHelper.Size];
                while (_position < _length) {
                    cancellation.ThrowIfCancellationRequested();
                    long start = _position;
                    if (start > SizedPtr.MaxOffset) {
                        return Result(RbfPrefixStatus.Invalid, start, "Frame address outside supported range.");
                    }
                    int headCount = (int)Math.Min(4, _length - start);
                    small.Clear();
                    Read(small[..headCount]);
                    uint head = BinaryPrimitives.ReadUInt32LittleEndian(small);
                    if (headCount < 4) {
                        ulong step = 1UL << (headCount * 8);
                        ulong possible = head;
                        if (possible < RbfLayout.MinFrameLength) {
                            possible += ((ulong)RbfLayout.MinFrameLength - possible + step - 1) / step * step;
                        }
                        if ((head & RbfLayout.AlignmentMask) != 0 || possible > SizedPtr.MaxLength) {
                            return Result(RbfPrefixStatus.Invalid, start, "Partial HeadLen has no legal completion.");
                        }
                        return Result(RbfPrefixStatus.IncompleteSuffix, start, "Legal partial HeadLen.");
                    }
                    if (head < RbfLayout.MinFrameLength || head > SizedPtr.MaxLength || (head & RbfLayout.AlignmentMask) != 0) {
                        return Result(RbfPrefixStatus.Invalid, start, "HeadLen or frame address outside supported range.");
                    }
                    int coverage = (int)head - FrameLayout.FixedOverhead;
                    long coverageEnd = start + 4 + coverage;
                    uint raw = RollingCrc.DefaultInitValue;
                    last.Clear();
                    while (_position < Math.Min(_length, coverageEnd)) {
                        int count = (int)Math.Min(chunk.Length, Math.Min(_length, coverageEnd) - _position);
                        Read(chunk.AsSpan(0, count));
                        raw = RollingCrc.CrcForward(raw, chunk.AsSpan(0, count));
                        // Only the final three coverage bytes can be padding.
                        if (count >= 3) { chunk.AsSpan(count - 3, 3).CopyTo(last); }
                        else {
                            last[count..].CopyTo(last);
                            chunk.AsSpan(0, count).CopyTo(last[(3 - count)..]);
                        }
                    }
                    if (_position < coverageEnd) { return Result(RbfPrefixStatus.IncompleteSuffix, start, "Payload/meta coverage prefix."); }
                    BinaryPrimitives.WriteUInt32LittleEndian(expectedCrc, raw ^ RollingCrc.DefaultFinalXor);
                    int crcCount = (int)Math.Min(4, _length - _position);
                    Read(small[..crcCount]);
                    if (!small[..crcCount].SequenceEqual(expectedCrc[..crcCount])) {
                        return Result(RbfPrefixStatus.Invalid, coverageEnd, "Existing PayloadCRC bytes contradict coverage.");
                    }
                    if (crcCount < 4) { return Result(RbfPrefixStatus.IncompleteSuffix, start, "PayloadCRC prefix validated."); }
                    long trailerStart = _position;
                    int trailerCount = (int)Math.Min(TrailerCodewordHelper.Size, _length - _position);
                    small.Clear();
                    Read(small[..trailerCount]);
                    // Validate descriptor bytes as soon as they exist, even before full TrailerCRC coverage.
                    int descriptorCount = Math.Clamp(trailerCount - 4, 0, 4);
                    uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(small[4..]);
                    int metaMinimum = (int)(descriptor & 0xFFFF);
                    int pad = descriptorCount == 4 ? (int)((descriptor >> 29) & 3) : 0;
                    if (!TrailerCodewordHelper.ValidateReservedBits(descriptor) || metaMinimum + pad > coverage) {
                        return Result(RbfPrefixStatus.Invalid, trailerStart + 4, "Existing descriptor has no legal completion.");
                    }
                    if (descriptorCount == 4 && !last[(3 - pad)..].SequenceEqual(zeros[..pad])) {
                        return Result(RbfPrefixStatus.Invalid, coverageEnd - pad, "Nonzero padding.");
                    }
                    if (trailerCount >= 12) {
                        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(small[8..]);
                        TrailerCodewordHelper.Serialize(expectedTrailer, descriptor, tag, head);
                        if (!small[..trailerCount].SequenceEqual(expectedTrailer[..trailerCount])) {
                            return Result(RbfPrefixStatus.Invalid, trailerStart, "Existing TrailerCRC/TailLen contradict known fields.");
                        }
                        var parsed = TrailerCodewordHelper.ParseAndValidate(expectedTrailer);
                        if (!parsed.IsSuccess || !TrailerCodewordHelper.ComputePayloadLength(head, metaMinimum, pad).IsSuccess) {
                            return Result(RbfPrefixStatus.Invalid, trailerStart, "Invalid trailer layout.");
                        }
                    }
                    if (trailerCount < TrailerCodewordHelper.Size) {
                        return Result(trailerCount is > 0 and < 12 ? RbfPrefixStatus.Unresolved : RbfPrefixStatus.IncompleteSuffix,
                            start, "Trailer prefix; unknown descriptor/tag may constrain existing TrailerCRC.");
                    }
                    long fenceStart = _position;
                    int fenceCount = (int)Math.Min(RbfLayout.FenceSize, _length - _position);
                    Read(small[..fenceCount]);
                    if (!small[..fenceCount].SequenceEqual(RbfLayout.Fence[..fenceCount])) {
                        return Result(RbfPrefixStatus.Invalid, fenceStart, "Contradictory trailing Fence.");
                    }
                    if (fenceCount < RbfLayout.FenceSize) { return Result(RbfPrefixStatus.IncompleteSuffix, start, "Frame validated; trailing Fence incomplete."); }
                    _boundary = _position;
                }
                cancellation.ThrowIfCancellationRequested();
                return Result(RbfPrefixStatus.Complete, _length, "Complete continuous physical prefix.");
            }
            catch (Stop ex) { return Result(ex.Status, _position, ex.Message); }
            catch (OperationCanceledException ex) { return Result(RbfPrefixStatus.Cancelled, _position, ex.Message); }
            catch (Exception ex) when (!propagateIo && (ex is IOException or UnauthorizedAccessException)) { return Result(RbfPrefixStatus.IoFailure, _position, ex.Message); }
        }
    }
}
