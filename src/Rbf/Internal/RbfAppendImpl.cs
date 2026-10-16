using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Binary;
using Atelia.Data.Hashing;

namespace Atelia.Rbf.Internal;

// A per-call test seam for selection-stage faults and explicit high keys; null uses Data's production selector.
internal delegate uint RbfEscapeKeySelector(uint fence, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta, ReadOnlySpan<byte> footer);

/// <summary>RBF3 帧写入实现。</summary>
/// <remarks>
/// 帧布局：[HeadLenUnits][XOR(Payload, TailMeta, Padding, PayloadCrc, TrailerCodeword)][TailKey]
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]: PayloadCrc 覆盖 Payload + TailMeta + Padding
/// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
/// </remarks>
internal static class RbfAppendImpl {
    private const int Rbf3SmallBufferSize = 8 * 1024;
    internal const int EscapeScratchSize = 1 << 20;

    internal static AteliaResult<SizedPtr> AppendRbf3(
        SafeFileHandle file,
        scoped ref long fileOffset,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> tailMeta,
        uint tag,
        ref byte[]? scratch,
        ArrayPool<byte> pool,
        Action markWriteFaulted,
        RbfEscapeKeySelector? selectKey = null,
        bool isTombstone = false
    ) {
        var layoutResult = FrameLayout.TryCreate(payload.Length, tailMeta.Length);
        if (layoutResult.IsFailure) { return layoutResult.Error!; }
        var startError = RbfFrameWriteCore.ValidateFrameStartOffset(fileOffset);
        if (startError is not null) { return startError; }
        FrameLayout layout = layoutResult.Value;
        SizedPtr ticket = SizedPtr.Create(fileOffset, layout.FrameLength);

        // This whole preparation stage precedes the first output operation. Input stays borrowed.
        Span<byte> footerBuffer = stackalloc byte[3 + RbfFrameWriteCore.PlaintextTailSize];
        Span<byte> footer = footerBuffer[..(layout.PaddingLength + RbfFrameWriteCore.PlaintextTailSize)];
        footer[..layout.PaddingLength].Clear();
        uint crc = RollingCrc.CrcForward(RollingCrc.DefaultInitValue, payload);
        crc = RollingCrc.CrcForward(crc, tailMeta);
        crc = RollingCrc.CrcForward(crc, footer[..layout.PaddingLength]) ^ RollingCrc.DefaultFinalXor;
        RbfFrameWriteCore.WritePlaintextTail(footer[layout.PaddingLength..], in layout, tag, isTombstone, crc);
        uint fence = RbfLayout.FenceWord;
        uint key = selectKey is null ? XorEscape.SelectKey(fence, payload, tailMeta, footer) : selectKey(fence, payload, tailMeta, footer);

        int totalLength = layout.FrameLength + RbfLayout.FenceSize;
        if (totalLength <= Rbf3SmallBufferSize) {
            Span<byte> small = stackalloc byte[Rbf3SmallBufferSize];
            BinaryPrimitives.WriteUInt32LittleEndian(small, layout.WireFrameLength);
            int offset = FrameLayout.HeadLenSize;
            CopyBodyPart(payload, small, key, ref offset);
            CopyBodyPart(tailMeta, small, key, ref offset);
            CopyBodyPart(footer, small, key, ref offset);
            BinaryPrimitives.WriteUInt32LittleEndian(small[offset..], key);
            BinaryPrimitives.WriteUInt32LittleEndian(small[(offset + sizeof(uint))..], fence);
            try { WritePrepared(file, small[..totalLength], ref fileOffset); }
            catch { markWriteFaulted(); throw; }
            return ticket;
        }

        if (key == 0) {
            Span<byte> head = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(head, layout.WireFrameLength);
            Span<byte> tail = stackalloc byte[3 + RbfFrameWriteCore.PlaintextTailSize + 2 * sizeof(uint)];
            footer.CopyTo(tail);
            BinaryPrimitives.WriteUInt32LittleEndian(tail[footer.Length..], key);
            BinaryPrimitives.WriteUInt32LittleEndian(tail[(footer.Length + sizeof(uint))..], fence);
            try {
                WritePrepared(file, head, ref fileOffset);
                if (!payload.IsEmpty) { WritePrepared(file, payload, ref fileOffset); }
                if (!tailMeta.IsEmpty) { WritePrepared(file, tailMeta, ref fileOffset); }
                WritePrepared(file, tail[..(footer.Length + 2 * sizeof(uint))], ref fileOffset);
            }
            catch { markWriteFaulted(); throw; }
            return ticket;
        }

        // Rent failures also precede output. Keep one bounded buffer per file, never a second whole frame.
        if (scratch is null) {
            byte[] rented = pool.Rent(EscapeScratchSize);
            if (rented.Length < EscapeScratchSize) {
                pool.Return(rented);
                throw new InvalidOperationException("ArrayPool returned an undersized RBF output buffer.");
            }
            scratch = rented;
        }
        Span<byte> output = scratch.AsSpan(0, EscapeScratchSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output, layout.WireFrameLength);
        int used = sizeof(uint);
        int phase = 0;
        bool outputStarted = false;
        try {
            WriteEncodedPart(file, payload, output, key, ref phase, ref used, ref fileOffset, ref outputStarted);
            WriteEncodedPart(file, tailMeta, output, key, ref phase, ref used, ref fileOffset, ref outputStarted);
            WriteEncodedPart(file, footer, output, key, ref phase, ref used, ref fileOffset, ref outputStarted);
            Span<byte> closure = stackalloc byte[2 * sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(closure, key);
            BinaryPrimitives.WriteUInt32LittleEndian(closure[sizeof(uint)..], fence);
            WriteRawPart(file, closure, output, ref used, ref fileOffset, ref outputStarted);
            if (used != 0) { WritePrepared(file, output[..used], ref fileOffset, ref outputStarted); }
        }
        catch { if (outputStarted) { markWriteFaulted(); } throw; }
        return ticket;
    }

    private static void CopyBodyPart(ReadOnlySpan<byte> source, Span<byte> output, uint key, ref int offset) {
        XorEscape.Copy(source, output[offset..], key, (offset - sizeof(uint)) & 3);
        offset += source.Length;
    }

    private static void WritePrepared(SafeFileHandle file, ReadOnlySpan<byte> bytes, scoped ref long fileOffset) {
        RbfWriteInstrumentation.Write(file, bytes, fileOffset);
        fileOffset += bytes.Length;
    }

    private static void WritePrepared(SafeFileHandle file, ReadOnlySpan<byte> bytes, scoped ref long fileOffset, ref bool outputStarted) {
        outputStarted = true;
        WritePrepared(file, bytes, ref fileOffset);
    }

    private static void WriteEncodedPart(SafeFileHandle file, ReadOnlySpan<byte> source, Span<byte> output, uint key,
        ref int phase, ref int used, scoped ref long fileOffset, ref bool outputStarted) {
        while (!source.IsEmpty) {
            int count = Math.Min(source.Length, output.Length - used);
            XorEscape.Copy(source[..count], output[used..], key, phase);
            phase = (phase + (count & 3)) & 3;
            used += count;
            source = source[count..];
            if (used == output.Length) { WritePrepared(file, output, ref fileOffset, ref outputStarted); used = 0; }
        }
    }

    private static void WriteRawPart(SafeFileHandle file, ReadOnlySpan<byte> source, Span<byte> output,
        ref int used, scoped ref long fileOffset, ref bool outputStarted) {
        while (!source.IsEmpty) {
            int count = Math.Min(source.Length, output.Length - used);
            source[..count].CopyTo(output[used..]);
            used += count;
            source = source[count..];
            if (used == output.Length) { WritePrepared(file, output, ref fileOffset, ref outputStarted); used = 0; }
        }
    }

}
