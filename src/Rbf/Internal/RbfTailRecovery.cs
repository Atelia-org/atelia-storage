using System.Buffers.Binary;
using Atelia.Data;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal;

/// <summary>Structure-only open and single incomplete RBF3 tail recovery on one borrowed owned handle.</summary>
internal static class RbfTailRecovery {
    private const int ClosingBlockSize = TrailerCodewordHelper.Size + sizeof(uint) + RbfLayout.FenceSize;
    private const int FenceSearchChunkSize = 64 * 1024;

    internal static RbfTailRecoveryReport Open(SafeFileHandle handle, bool writable) => Open(handle, writable, out _);

    internal static RbfTailRecoveryReport Open(SafeFileHandle handle, bool writable, out RbfProfile profile) {
        long originalLength = RandomAccess.GetLength(handle);
        if (originalLength < RbfLayout.HeaderOnlyLength) { throw Invalid(0, "Incomplete HeaderFence."); }
        Span<byte> header = stackalloc byte[RbfLayout.FenceSize];
        var metrics = RbfReadMetrics.Current;
        metrics?.HeaderRequested(header.Length);
        ReadExactly(handle, header, 0, measure: false);
        metrics?.HeaderReturned(header.Length);
        if (header.SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf1))) {
            profile = RbfProfile.Rbf1;
            if (writable) { throw Invalid(0, "RBF1 supports read-only open; writable open requires RBF3."); }
            ValidateLegacyChain(handle, originalLength);
            return None(originalLength);
        }
        if (!header.SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf3))) {
            // RBF2 was an experimental byte-length format, never the production units profile.
            throw Invalid(0, "Unsupported HeaderFence.");
        }
        profile = RbfProfile.Rbf3;
        return OpenNew(handle, originalLength, writable);
    }

    private static RbfTailRecoveryReport OpenNew(SafeFileHandle handle, long originalLength, bool writable) {
        if (originalLength == RbfLayout.HeaderOnlyLength) { return None(originalLength); }
        long minClosedLength = RbfLayout.HeaderOnlyLength + RbfLayout.GetMinFrameLength(RbfProfile.Rbf3) + RbfLayout.FenceSize;
        if ((originalLength & RbfLayout.AlignmentMask) == 0 && originalLength >= minClosedLength) {
            Span<byte> closing = stackalloc byte[ClosingBlockSize];
            ReadExactly(handle, closing, originalLength - closing.Length);
            if (closing[^RbfLayout.FenceSize..].SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf3))) {
                // A complete EOF Fence is the first candidate. Its bad structure must not fall back.
                ValidateClosedFrame(handle, originalLength, closing);
                return None(originalLength);
            }
        }

        long start = FindLastFenceEnd(handle, originalLength);
        if (start > RbfLayout.HeaderOnlyLength) { ReadClosedFrame(handle, start); }
        RequireFrameStart(start);
        long present = originalLength - start;
        if (present == 0) { return None(originalLength); }
        if (present > (long)SizedPtr.MaxLength + RbfLayout.FenceSize - 1) {
            throw Invalid(start, "Terminal suffix exceeds the single-frame bound.");
        }

        if (present < sizeof(uint)) {
            // Every 1-3 byte LE prefix admits some U in [7, 2^26-1]. Do not guess that U.
            return Truncate(handle, originalLength, start, writable);
        }
        Span<byte> head = stackalloc byte[sizeof(uint)];
        ReadExactly(handle, head, start);
        uint units = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var decodedLength = RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf3, units);
        if (decodedLength.IsFailure) { throw Invalid(start, decodedLength.Error!.ToString()); }
        int frameLength = decodedLength.Value;
        if (present < frameLength - sizeof(uint)) {
            // The body has not finished. No partial trailer or content is used as qualification.
            return Truncate(handle, originalLength, start, writable);
        }
        if (present > (long)frameLength + RbfLayout.FenceSize - 1) {
            throw Invalid(start, "Bytes extend beyond one terminal frame and partial Fence.");
        }

        Span<byte> encodedTrailer = stackalloc byte[TrailerCodewordHelper.Size];
        long trailerOffset = start + frameLength - sizeof(uint) - encodedTrailer.Length;
        ReadExactly(handle, encodedTrailer, trailerOffset);
        uint encodedUnits = BinaryPrimitives.ReadUInt32LittleEndian(encodedTrailer[^sizeof(uint)..]);
        uint key = encodedUnits ^ units;
        ValidateNewTrailer(handle, start, frameLength, key, encodedTrailer);

        Span<byte> expected = stackalloc byte[sizeof(uint) + RbfLayout.FenceSize];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, key);
        RbfLayout.GetFence(RbfProfile.Rbf3).CopyTo(expected[sizeof(uint)..]);
        int existing = checked((int)(present - (frameLength - sizeof(uint))));
        if (existing != 0) {
            Span<byte> actual = stackalloc byte[sizeof(uint) + RbfLayout.FenceSize];
            ReadExactly(handle, actual[..existing], start + frameLength - sizeof(uint));
            if (!actual[..existing].SequenceEqual(expected[..existing])) {
                throw Invalid(start + frameLength - sizeof(uint), "Existing TailKey/Fence contradicts the unique completion.");
            }
        }
        if (!writable) { throw Invalid(start, "Terminal frame needs recovery; read-only open cannot modify it."); }
        RbfWriteInstrumentation.Write(handle, expected[existing..], originalLength);
        RbfWriteInstrumentation.Flush(handle);
        long finalLength = RandomAccess.GetLength(handle);
        long expectedLength = start + frameLength + RbfLayout.FenceSize;
        if (finalLength != expectedLength) { throw Invalid(finalLength, "Unexpected length after tail completion."); }
        ReadClosedFrame(handle, finalLength);
        return new(RbfTailRecoveryAction.CompletedTail, originalLength, finalLength, start, SizedPtr.Create(start, frameLength));
    }

    private static RbfTailRecoveryReport Truncate(SafeFileHandle handle, long originalLength, long start, bool writable) {
        if (!writable) { throw Invalid(start, "Terminal frame needs truncation; read-only open cannot modify it."); }
        RbfWriteInstrumentation.SetLength(handle, start);
        RbfWriteInstrumentation.Flush(handle);
        long finalLength = RandomAccess.GetLength(handle);
        if (finalLength != start) { throw Invalid(finalLength, "Unexpected length after tail truncation."); }
        if (start != RbfLayout.HeaderOnlyLength) { ReadClosedFrame(handle, start); }
        return new(RbfTailRecoveryAction.Truncated, originalLength, finalLength, start, null);
    }

    private static void ReadClosedFrame(SafeFileHandle handle, long fenceEnd) {
        if (fenceEnd < RbfLayout.HeaderOnlyLength + RbfLayout.GetMinFrameLength(RbfProfile.Rbf3) + RbfLayout.FenceSize ||
            (fenceEnd & RbfLayout.AlignmentMask) != 0) {
            throw Invalid(fenceEnd, "Fence has no complete predecessor frame.");
        }
        Span<byte> closing = stackalloc byte[ClosingBlockSize];
        ReadExactly(handle, closing, fenceEnd - closing.Length);
        ValidateClosedFrame(handle, fenceEnd, closing);
    }

    private static void ValidateClosedFrame(SafeFileHandle handle, long fenceEnd, ReadOnlySpan<byte> closing) {
        if (!closing[^RbfLayout.FenceSize..].SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf3))) {
            throw Invalid(fenceEnd - RbfLayout.FenceSize, "Trailing Fence mismatch.");
        }
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(closing.Slice(TrailerCodewordHelper.Size, sizeof(uint)));
        var parsed = RbfWireCodec.ParseEncodedTrailer(closing[..TrailerCodewordHelper.Size], key);
        if (parsed.IsFailure) { throw Invalid(fenceEnd - closing.Length, parsed.Error!.ToString()); }
        int frameLength = checked((int)parsed.Value.TailLen);
        long start = fenceEnd - RbfLayout.FenceSize - frameLength;
        RequireFrameStart(start);
        Span<byte> left = stackalloc byte[RbfLayout.FenceSize + sizeof(uint)];
        ReadExactly(handle, left, start - RbfLayout.FenceSize);
        if (!left[..RbfLayout.FenceSize].SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf3))) {
            throw Invalid(start - RbfLayout.FenceSize, "Left Fence mismatch.");
        }
        var headLength = RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf3, BinaryPrimitives.ReadUInt32LittleEndian(left[RbfLayout.FenceSize..]));
        if (headLength.IsFailure || headLength.Value != frameLength) { throw Invalid(start, "HeadLenUnits/TailLenUnits mismatch."); }
        ValidatePadding(handle, start, frameLength, key, parsed.Value.PaddingLen);
    }

    private static void ValidateNewTrailer(SafeFileHandle handle, long start, int frameLength, uint key, ReadOnlySpan<byte> encodedTrailer) {
        var parsed = RbfWireCodec.ParseEncodedTrailer(encodedTrailer, key);
        if (parsed.IsFailure) { throw Invalid(start + frameLength - sizeof(uint) - encodedTrailer.Length, parsed.Error!.ToString()); }
        if (parsed.Value.TailLen != (uint)frameLength) { throw Invalid(start, "HeadLenUnits/TailLenUnits mismatch."); }
        ValidatePadding(handle, start, frameLength, key, parsed.Value.PaddingLen);
    }

    private static void ValidatePadding(SafeFileHandle handle, long start, int frameLength, uint key, int paddingLength) {
        if (paddingLength == 0) { return; }
        Span<byte> padding = stackalloc byte[3];
        long offset = start + frameLength - ClosingBlockSize - paddingLength;
        ReadExactly(handle, padding[..paddingLength], offset);
        for (int i = 0; i < paddingLength; i++) {
            int phase = (i - paddingLength) & RbfLayout.AlignmentMask;
            if (padding[i] != (byte)(key >> (phase * 8))) { throw Invalid(offset + i, "Nonzero decoded padding."); }
        }
    }

    private static long FindLastFenceEnd(SafeFileHandle handle, long eof) {
        // Only complete globally aligned words are candidates. The scan itself reads at most M+7 bytes.
        long lower = Math.Max(0, eof - ((long)SizedPtr.MaxLength + 7));
        lower = (lower + RbfLayout.AlignmentMask) & ~(long)RbfLayout.AlignmentMask;
        long end = eof & ~(long)RbfLayout.AlignmentMask;
        byte[] chunk = new byte[FenceSearchChunkSize];
        uint fence = RbfLayout.GetFenceWord(RbfProfile.Rbf3);
        while (end > lower) {
            int count = (int)Math.Min(chunk.Length, end - lower);
            long offset = end - count;
            ReadExactly(handle, chunk.AsSpan(0, count), offset);
            for (int i = count - sizeof(uint); i >= 0; i -= RbfLayout.Alignment) {
                if (BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(i, sizeof(uint))) == fence) {
                    return offset + i + RbfLayout.FenceSize;
                }
            }
            end = offset;
        }
        throw Invalid(lower, "No aligned Fence within the single-tail scan bound.");
    }

    private static void ValidateLegacyChain(SafeFileHandle handle, long length) {
        long start = RbfLayout.FirstFrameOffset;
        Span<byte> small = stackalloc byte[TrailerCodewordHelper.Size];
        while (start < length) {
            RequireFrameStart(start);
            if (length - start < sizeof(uint)) { throw Invalid(start, "Incomplete RBF1 HeadLen."); }
            ReadExactly(handle, small[..sizeof(uint)], start);
            var decodedLength = RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf1, BinaryPrimitives.ReadUInt32LittleEndian(small));
            if (decodedLength.IsFailure) { throw Invalid(start, decodedLength.Error!.ToString()); }
            int frameLength = decodedLength.Value;
            long frameEnd = start + frameLength;
            if (length - frameEnd < RbfLayout.FenceSize) { throw Invalid(start, "Incomplete RBF1 terminal frame or Fence."); }
            ReadExactly(handle, small, frameEnd - small.Length);
            var parsed = RbfWireCodec.ParseTrailer(RbfProfile.Rbf1, small);
            if (parsed.IsFailure) { throw Invalid(frameEnd - small.Length, parsed.Error!.ToString()); }
            if (parsed.Value.TailLen != (uint)frameLength) { throw Invalid(start, "RBF1 HeadLen/TailLen mismatch."); }
            int paddingLength = parsed.Value.PaddingLen;
            if (paddingLength != 0) {
                long paddingOffset = frameEnd - TrailerCodewordHelper.Size - sizeof(uint) - paddingLength;
                ReadExactly(handle, small[..paddingLength], paddingOffset);
                for (int i = 0; i < paddingLength; i++) {
                    if (small[i] != 0) { throw Invalid(paddingOffset + i, "Nonzero RBF1 padding."); }
                }
            }
            ReadExactly(handle, small[..RbfLayout.FenceSize], frameEnd);
            if (!small[..RbfLayout.FenceSize].SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf1))) { throw Invalid(frameEnd, "RBF1 trailing Fence mismatch."); }
            start = frameEnd + RbfLayout.FenceSize;
        }
    }

    private static void RequireFrameStart(long start) {
        if (start < RbfLayout.FirstFrameOffset || start > SizedPtr.MaxOffset || (start & RbfLayout.AlignmentMask) != 0) {
            throw Invalid(start, "Frame start is outside the supported aligned address range.");
        }
    }

    private static RbfTailRecoveryReport None(long length) => new(RbfTailRecoveryAction.None, length, length, null, null);

    private static InvalidDataException Invalid(long offset, string detail) => new($"Invalid RBF at offset {offset}: {detail}");

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
}
