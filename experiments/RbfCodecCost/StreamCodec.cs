using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Microsoft.Win32.SafeHandles;

namespace RbfCodecCost;

// Experimental codec only. Output buffers are bounded and owned by the caller.
internal sealed class StreamCodec(byte[] workspace) {
    public long WriteCalls { get; private set; }
    public long WrittenBytes { get; private set; }

    private void Write(SafeFileHandle handle, ReadOnlySpan<byte> bytes, ref long offset) {
        if (bytes.IsEmpty) return;
        RandomAccess.Write(handle, bytes, offset);
        offset += bytes.Length;
        WriteCalls++;
        WrittenBytes += bytes.Length;
    }

    public void Append(SafeFileHandle handle, PreparedFrame frame, ref long offset) {
        if (workspace.Length < 4096) throw new ArgumentException("Workspace must be at least 4KiB.");
        if (frame.FrameLength + 4 <= Math.Min(workspace.Length, 8192)) {
            PrototypeCodec.Serialize(frame, workspace.AsSpan(0, frame.FrameLength + 4));
            Write(handle, workspace.AsSpan(0, frame.FrameLength + 4), ref offset);
            return;
        }
        if (frame.Key == 0) {
            Span<byte> head = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)frame.FrameLength);
            Write(handle, head, ref offset);
            foreach (byte[] chunk in frame.Chunks) Write(handle, chunk, ref offset);
            Span<byte> tail = stackalloc byte[32];
            frame.Footer.CopyTo(tail);
            BinaryPrimitives.WriteUInt32LittleEndian(tail[frame.Footer.Length..], frame.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(tail[(frame.Footer.Length + 4)..], PrototypeCodec.Fence);
            Write(handle, tail[..(frame.Footer.Length + 8)], ref offset);
            return;
        }
        int used = 4, bodyOffset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(workspace, (uint)frame.FrameLength);
        foreach (byte[] chunk in frame.Chunks) PushEncoded(handle, chunk, frame.Key, ref bodyOffset, ref used, ref offset);
        PushEncoded(handle, frame.Footer, frame.Key, ref bodyOffset, ref used, ref offset);
        Span<byte> closure = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(closure, frame.Key);
        BinaryPrimitives.WriteUInt32LittleEndian(closure[4..], PrototypeCodec.Fence);
        PushRaw(handle, closure, ref used, ref offset);
        Write(handle, workspace.AsSpan(0, used), ref offset);
    }

    private void PushEncoded(SafeFileHandle handle, ReadOnlySpan<byte> source, uint key, ref int bodyOffset, ref int used, ref long offset) {
        while (!source.IsEmpty) {
            int count = Math.Min(source.Length, workspace.Length - used);
            XorTransform.Copy(source[..count], workspace.AsSpan(used, count), key, bodyOffset);
            bodyOffset += count;
            used += count;
            source = source[count..];
            if (used == workspace.Length) {
                Write(handle, workspace, ref offset);
                used = 0;
            }
        }
    }

    private void PushRaw(SafeFileHandle handle, ReadOnlySpan<byte> source, ref int used, ref long offset) {
        while (!source.IsEmpty) {
            int count = Math.Min(source.Length, workspace.Length - used);
            source[..count].CopyTo(workspace.AsSpan(used));
            used += count;
            source = source[count..];
            if (used == workspace.Length) {
                Write(handle, workspace, ref offset);
                used = 0;
            }
        }
    }

    public static int ReadExactly(SafeFileHandle handle, Span<byte> target, long offset) {
        int calls = 0;
        while (!target.IsEmpty) {
            int count = RandomAccess.Read(handle, target, offset);
            calls++;
            if (count == 0) throw new EndOfStreamException();
            target = target[count..];
            offset += count;
        }
        return calls;
    }

    // Returns CRC only after validating the complete plaintext framing.
    // This is a local checked-read prototype, not Open/recovery/member qualification.
    public static uint DecodeAndCheck(Span<byte> frame, bool fused) {
        int length = frame.Length;
        if (length < 28 || (length & 3) != 0 || length > PrototypeCodec.MaxFrameLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame) != length) throw new InvalidDataException("HeadLen");
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(frame[^4..]);
        // Random search permits all uint32 Keys except the raw structural Fence.
        if (key == PrototypeCodec.Fence) throw new InvalidDataException("TailKey");
        // Decode/validate the trailer first, before using its coverage/padding lengths.
        int trailerOffset = length - 20;
        XorTransform.InPlace(frame.Slice(trailerOffset, 16), key, trailerOffset - 4);
        Span<byte> trailer = frame.Slice(trailerOffset, 16);
        if (BinaryPrimitives.ReadUInt32BigEndian(trailer) != RollingCrc.CrcBackward(trailer[4..]))
            throw new InvalidDataException("TrailerCRC");
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
        int pad = (int)((descriptor >> 29) & 3), meta = (int)(descriptor & 65535);
        int coverage = length - 28;
        if ((descriptor & 0x1FFF0000) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(trailer[12..]) != length ||
            pad > coverage || meta > coverage - pad) throw new InvalidDataException("Descriptor/TailLen");
        Span<byte> payloadCoverage = frame.Slice(4, coverage);
        uint crc;
        if (fused) crc = XorTransform.DecodeCoverageAndCrc(payloadCoverage, key);
        else {
            XorTransform.InPlace(payloadCoverage, key, 0);
            crc = RollingCrc.CrcForward(payloadCoverage);
        }
        XorTransform.InPlace(frame.Slice(4 + coverage, 4), key, coverage);
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4 + coverage, 4)) != crc)
            throw new InvalidDataException("PayloadCRC");
        foreach (byte value in payloadCoverage[^pad..]) if (value != 0) throw new InvalidDataException("Padding");
        return crc;
    }
}
