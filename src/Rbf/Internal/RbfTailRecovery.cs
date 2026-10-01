using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal;

/// <summary>Single incomplete terminal-frame protocol. All qualification and writes borrow one owned handle.</summary>
internal static class RbfTailRecovery {
    internal static RbfTailRecoveryReport Open(SafeFileHandle handle, bool writable) {
        long originalLength = RandomAccess.GetLength(handle);
        Span<byte> small = stackalloc byte[TrailerCodewordHelper.Size];
        if (originalLength < RbfLayout.HeaderOnlyLength) { throw Invalid(0, "Incomplete HeaderFence."); }
        var metrics = RbfReadMetrics.Current;
        metrics?.HeaderRequested(RbfLayout.FenceSize);
        ReadExactly(handle, small[..RbfLayout.FenceSize], 0, measure: false);
        metrics?.HeaderReturned(RbfLayout.FenceSize);
        if (!small[..RbfLayout.FenceSize].SequenceEqual(RbfLayout.Fence)) { throw Invalid(0, "HeaderFence mismatch."); }

        // Follow HeadLen from the header: a locally valid frame embedded in a partial payload
        // must never become a member of the file's frame sequence.
        long start = RbfLayout.FirstFrameOffset;
        long? previousStart = null;
        int frameLength = 0;
        while (start < originalLength) {
            if (start > SizedPtr.MaxOffset) { throw Invalid(start, "Frame start exceeds supported address range."); }
            if (originalLength - start < sizeof(uint)) { break; }
            ReadExactly(handle, small[..sizeof(uint)], start);
            uint head = BinaryPrimitives.ReadUInt32LittleEndian(small);
            if (head < RbfLayout.MinFrameLength || head > SizedPtr.MaxLength || (head & RbfLayout.AlignmentMask) != 0) {
                throw Invalid(start, "Invalid HeadLen.");
            }
            frameLength = (int)head;
            long frameEnd = start + frameLength;
            if (originalLength - frameEnd < RbfLayout.FenceSize) { break; }
            ReadExactly(handle, small, frameEnd - TrailerCodewordHelper.Size);
            var parsed = TrailerCodewordHelper.ParseAndValidate(small);
            if (parsed.IsFailure) { throw Invalid(frameEnd - TrailerCodewordHelper.Size, parsed.Error!.ToString()); }
            var trailer = parsed.Value;
            if (trailer.TailLen != head || TrailerCodewordHelper.ComputePayloadLength(head, trailer.TailMetaLen, trailer.PaddingLen).IsFailure) {
                throw Invalid(start, "HeadLen/TailLen or descriptor layout mismatch.");
            }
            if (trailer.PaddingLen != 0) {
                ReadExactly(handle, small[..trailer.PaddingLen], frameEnd - FrameLayout.LengthAfterPayloadCrcCoverage - trailer.PaddingLen);
                for (int i = 0; i < trailer.PaddingLen; i++) {
                    if (small[i] != 0) { throw Invalid(frameEnd - FrameLayout.LengthAfterPayloadCrcCoverage - trailer.PaddingLen + i, "Nonzero padding."); }
                }
            }
            ReadExactly(handle, small[..RbfLayout.FenceSize], frameEnd);
            if (!small[..RbfLayout.FenceSize].SequenceEqual(RbfLayout.Fence)) { throw Invalid(frameEnd, "Trailing Fence mismatch."); }
            previousStart = start;
            start = frameEnd + RbfLayout.FenceSize;
        }

        using var source = new BorrowedSource(handle);
        // Full streaming CRC only for the last closed frame and terminal prefix.
        // Historical payload auditing remains an explicit offline operation.
        long qualificationStart = previousStart ?? RbfLayout.FirstFrameOffset;
        var inspection = RbfPrefixInspector.InspectSuffix(source, qualificationStart);
        if (inspection.Status is not (RbfPrefixStatus.Complete or RbfPrefixStatus.IncompleteSuffix or RbfPrefixStatus.Unresolved)) {
            throw Invalid(inspection.IssueOffset, inspection.Detail);
        }
        if (start == originalLength) {
            if (inspection.Status != RbfPrefixStatus.Complete) { throw Invalid(inspection.IssueOffset, inspection.Detail); }
            return new(RbfTailRecoveryAction.None, originalLength, originalLength, null, null);
        }
        if (!writable) { throw Invalid(start, "Terminal frame needs recovery; read-only open cannot modify it."); }

        RbfTailRecoveryAction action;
        SizedPtr? ticket = null;
        if (originalLength - start < sizeof(uint)) {
            // Inspector established that the existing HeadLen bytes admit a legal completion.
            RbfWriteInstrumentation.SetLength(handle, start);
            RbfWriteInstrumentation.Flush(handle);
            action = RbfTailRecoveryAction.Truncated;
        }
        else {
            long frameEnd = start + frameLength;
            ticket = SizedPtr.Create(start, frameLength);
            if (originalLength >= frameEnd) {
                int presentFence = (int)(originalLength - frameEnd);
                RbfWriteInstrumentation.Write(handle, RbfLayout.Fence[presentFence..], originalLength);
                RbfWriteInstrumentation.Flush(handle);
                action = RbfTailRecoveryAction.CompletedFence;
            }
            else {
                CompleteTombstone(handle, start, frameLength, originalLength);
                action = RbfTailRecoveryAction.CompletedTombstone;
            }
        }

        long finalLength = RandomAccess.GetLength(handle);
        long validationStart = action == RbfTailRecoveryAction.Truncated ? qualificationStart : start;
        var final = RbfPrefixInspector.InspectSuffix(source, validationStart);
        if (final.Status != RbfPrefixStatus.Complete) { throw Invalid(final.IssueOffset, final.Detail); }
        return new(action, originalLength, finalLength, start, ticket);
    }

    private static void CompleteTombstone(SafeFileHandle handle, long start, int length, long eof) {
        long crcStart = start + length - FrameLayout.LengthAfterPayloadCrcCoverage;
        if (eof > crcStart) {
            // Remove only the unfinished CRC/trailer, never coverage, HeadLen, or prior frames.
            RbfWriteInstrumentation.SetLength(handle, crcStart);
            RbfWriteInstrumentation.Flush(handle);
            eof = crcStart;
        }
        byte[] chunk = new byte[RbfPrefixInspector.ChunkSize];
        uint raw = RollingCrc.DefaultInitValue;
        long position = start + sizeof(uint);
        while (position < eof) {
            int count = (int)Math.Min(chunk.Length, eof - position);
            ReadExactly(handle, chunk.AsSpan(0, count), position);
            raw = RollingCrc.CrcForward(raw, chunk.AsSpan(0, count));
            position += count;
        }
        chunk.AsSpan().Clear();
        while (position < crcStart) {
            int count = (int)Math.Min(chunk.Length, crcStart - position);
            raw = RollingCrc.CrcForward(raw, chunk.AsSpan(0, count));
            RbfWriteInstrumentation.Write(handle, chunk.AsSpan(0, count), position);
            position += count;
        }
        Span<byte> suffix = stackalloc byte[FrameLayout.LengthAfterPayloadCrcCoverage + RbfLayout.FenceSize];
        BinaryPrimitives.WriteUInt32LittleEndian(suffix, raw ^ RollingCrc.DefaultFinalXor);
        TrailerCodewordHelper.Serialize(suffix[sizeof(uint)..],
            TrailerCodewordHelper.BuildDescriptor(isTombstone: true, paddingLen: 0, tailMetaLen: 0), 0, (uint)length);
        RbfLayout.Fence.CopyTo(suffix[FrameLayout.LengthAfterPayloadCrcCoverage..]);
        RbfWriteInstrumentation.Write(handle, suffix, crcStart);
        RbfWriteInstrumentation.Flush(handle);
    }

    private static InvalidDataException Invalid(long offset, string detail) =>
        new($"Invalid RBF at offset {offset}: {detail}");

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset, bool measure = true) {
        while (!destination.IsEmpty) {
            var metrics = measure ? RbfReadMetrics.Current : null;
            metrics?.ReadRequested(destination.Length);
            metrics?.RawRequested(destination.Length);
            int actual = RandomAccess.Read(handle, destination, offset);
            metrics?.ReadReturned(actual);
            metrics?.RawReturned(offset, actual, offset, destination.Length);
            if (actual == 0) { throw new IOException($"Unexpected EOF while reading RBF at offset {offset}."); }
            offset += actual;
            destination = destination[actual..];
        }
    }

    /// <summary>Read/seek adapter that never owns or closes the file handle.</summary>
    private sealed class BorrowedSource(SafeFileHandle handle) : Stream {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => RandomAccess.GetLength(handle);
        public override long Position { get; set; }
        public override int Read(Span<byte> destination) {
            var metrics = RbfReadMetrics.Current;
            metrics?.ReadRequested(destination.Length);
            metrics?.RawRequested(destination.Length);
            int actual = RandomAccess.Read(handle, destination, Position);
            metrics?.ReadReturned(actual);
            metrics?.RawReturned(Position, actual, Position, destination.Length);
            Position += actual;
            return actual;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
