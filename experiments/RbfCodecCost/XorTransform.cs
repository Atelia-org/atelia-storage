using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Atelia.Data.Hashing;

namespace RbfCodecCost;

internal enum XorMode {
    Scalar,
    Vector
}

/// <summary>
/// Experiment-only alternative: each complete little-endian body word is independently
/// added modulo 2^32 when encoding, and subtracted modulo 2^32 when decoding.
/// This is not an arbitrary-byte-slice transform and does not select a production profile.
/// </summary>
internal static class AddTransform {
    public static void CopyWords(
        ReadOnlySpan<byte> alignedSource,
        Span<byte> destination,
        uint key,
        bool decode,
        XorMode mode = XorMode.Vector
    ) {
        Validate(alignedSource.Length, mode);
        if (destination.Length < alignedSource.Length) {
            throw new ArgumentException("Destination is shorter than source.", nameof(destination));
        }
        destination = destination[..alignedSource.Length];
        if (key == 0) {
            alignedSource.CopyTo(destination);
            return;
        }

        if (alignedSource.Overlaps(destination, out int destinationOffset) && destinationOffset > 0) {
            for (int cursor = alignedSource.Length; (cursor -= sizeof(uint)) >= 0;) {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(alignedSource[cursor..]);
                uint transformed = decode ? unchecked(word - key) : unchecked(word + key);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[cursor..], transformed);
            }
            return;
        }

        int position = 0;
        int vectorBytes = Vector<byte>.Count;
        if (mode == XorMode.Vector && Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && alignedSource.Length >= vectorBytes) {
            var mask = new Vector<uint>(key);
            while (position <= alignedSource.Length - vectorBytes) {
                var value = new Vector<uint>(MemoryMarshal.Cast<byte, uint>(alignedSource.Slice(position, vectorBytes)));
                var transformed = decode ? value - mask : value + mask;
                transformed.CopyTo(MemoryMarshal.Cast<byte, uint>(destination.Slice(position, vectorBytes)));
                position += vectorBytes;
            }
        }
        for (; position < alignedSource.Length; position += sizeof(uint)) {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(alignedSource[position..]);
            uint transformed = decode ? unchecked(word - key) : unchecked(word + key);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[position..], transformed);
        }
    }

    public static void InPlaceWords(Span<byte> bytes, uint key, bool decode, XorMode mode = XorMode.Vector) {
        if (key == 0) {
            Validate(bytes.Length, mode);
            return;
        }
        CopyWords(bytes, bytes, key, decode, mode);
    }

    /// <summary>Decodes complete coverage words in place and returns finalized forward CRC32C.</summary>
    public static uint DecodeCoverageAndCrcWords(Span<byte> encodedCoverage, uint key) {
        Validate(encodedCoverage.Length, XorMode.Scalar);
        if (key == 0) { return RollingCrc.CrcForward(encodedCoverage); }

        uint rawCrc = RollingCrc.DefaultInitValue;
        Span<byte> remain = encodedCoverage;
        while (remain.Length >= sizeof(ulong)) {
            ulong encoded = BinaryPrimitives.ReadUInt64LittleEndian(remain);
            // A single ulong subtraction would let borrow cross the two word boundaries.
            uint low = unchecked((uint)encoded - key);
            uint high = unchecked((uint)(encoded >> 32) - key);
            ulong plaintext = low | ((ulong)high << 32);
            BinaryPrimitives.WriteUInt64LittleEndian(remain, plaintext);
            rawCrc = BitOperations.Crc32C(rawCrc, plaintext);
            remain = remain[sizeof(ulong)..];
        }
        if (!remain.IsEmpty) {
            uint plaintext = unchecked(BinaryPrimitives.ReadUInt32LittleEndian(remain) - key);
            BinaryPrimitives.WriteUInt32LittleEndian(remain, plaintext);
            rawCrc = BitOperations.Crc32C(rawCrc, plaintext);
        }
        return rawCrc ^ RollingCrc.DefaultFinalXor;
    }

    private static void Validate(int length, XorMode mode) {
        if ((length & 3) != 0) { throw new ArgumentException("Source length must contain complete 4-byte body words.", nameof(length)); }
        if (mode != XorMode.Scalar && mode != XorMode.Vector) { throw new ArgumentOutOfRangeException(nameof(mode)); }
    }
}

/// <summary>Experiment-only RBF2 word XOR, with byte phase independent of span/chunk boundaries.</summary>
internal static class XorTransform {
    /// <summary>
    /// Copies and transforms exactly source.Length bytes. bodyByteOffset is the first byte's
    /// offset relative to the body start (frame offset 4), not a file offset or span offset.
    /// The key's little-endian bytes repeat every four bytes. Shifted overlaps are supported.
    /// </summary>
    public static void Copy(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        uint key,
        int bodyByteOffset,
        XorMode mode = XorMode.Vector
    ) {
        Validate(bodyByteOffset, mode);
        if (destination.Length < source.Length) {
            throw new ArgumentException("Destination is shorter than source.", nameof(destination));
        }
        destination = destination[..source.Length];
        if (key == 0) {
            source.CopyTo(destination);
            return;
        }

        int phase = bodyByteOffset & 3;
        // A forward store would overwrite future source bytes in a right-shifted overlap.
        // The production integration needs distinct buffers or exact aliasing; this fallback
        // also gives the experiment a complete, allocation-free Copy contract.
        if (source.Overlaps(destination, out int destinationOffset) && destinationOffset > 0) {
            for (int i = source.Length; --i >= 0;) {
                destination[i] = (byte)(source[i] ^ KeyByte(key, phase, i));
            }
            return;
        }

        uint phaseKey = BitOperations.RotateRight(key, phase * 8);
        int cursor = 0;
        int vectorSize = Vector<byte>.Count;
        if (mode == XorMode.Vector && Vector.IsHardwareAccelerated && source.Length >= vectorSize) {
            Vector<byte> mask;
            if (BitConverter.IsLittleEndian) {
                mask = Vector.AsVectorByte(new Vector<uint>(phaseKey));
            }
            else {
                // A native uint vector has the wrong byte order on a big-endian host.
                Span<byte> maskBytes = stackalloc byte[vectorSize];
                for (int i = 0; i < maskBytes.Length; i++) {
                    maskBytes[i] = KeyByte(key, phase, i);
                }
                mask = new Vector<byte>(maskBytes);
            }
            while (cursor <= source.Length - vectorSize) {
                var value = new Vector<byte>(source.Slice(cursor, vectorSize));
                (value ^ mask).CopyTo(destination.Slice(cursor, vectorSize));
                cursor += vectorSize;
            }
        }

        // Vector widths preserve the four-byte phase. Use complete LE words for the
        // forward scalar path too, rather than comparing a byte loop with word kernels.
        ReadOnlySpan<byte> remainingSource = source[cursor..];
        Span<byte> remainingDestination = destination[cursor..];
        ulong mask64 = phaseKey | ((ulong)phaseKey << 32);
        while (remainingSource.Length >= sizeof(ulong)) {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(remainingSource) ^ mask64;
            BinaryPrimitives.WriteUInt64LittleEndian(remainingDestination, value);
            remainingSource = remainingSource[sizeof(ulong)..];
            remainingDestination = remainingDestination[sizeof(ulong)..];
        }
        if (remainingSource.Length >= sizeof(uint)) {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(remainingSource) ^ phaseKey;
            BinaryPrimitives.WriteUInt32LittleEndian(remainingDestination, value);
            remainingSource = remainingSource[sizeof(uint)..];
            remainingDestination = remainingDestination[sizeof(uint)..];
        }
        if (remainingSource.Length >= sizeof(ushort)) {
            ushort value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(remainingSource) ^ (ushort)phaseKey);
            BinaryPrimitives.WriteUInt16LittleEndian(remainingDestination, value);
            remainingSource = remainingSource[sizeof(ushort)..];
            remainingDestination = remainingDestination[sizeof(ushort)..];
            phaseKey >>= 16;
        }
        if (!remainingSource.IsEmpty) {
            remainingDestination[0] = (byte)(remainingSource[0] ^ (byte)phaseKey);
        }
    }

    /// <summary>Transforms only the supplied span; key zero leaves its bytes untouched.</summary>
    public static void InPlace(
        Span<byte> bytes,
        uint key,
        int bodyByteOffset,
        XorMode mode = XorMode.Vector
    ) {
        if (key == 0) {
            Validate(bodyByteOffset, mode);
            return;
        }
        Copy(bytes, bytes, key, bodyByteOffset, mode);
    }

    /// <summary>
    /// Decodes only the supplied coverage (payload + meta + padding) in place, returning
    /// finalized forward CRC32C. This does not decode or validate a stored CRC or trailer.
    /// Each call starts a new CRC; it is not an incremental CRC API for separate chunks.
    /// </summary>
    public static uint DecodeCoverageAndCrc(Span<byte> encodedCoverage, uint key, int bodyByteOffset = 0) {
        if (bodyByteOffset < 0) { throw new ArgumentOutOfRangeException(nameof(bodyByteOffset)); }
        if (key == 0) { return RollingCrc.CrcForward(encodedCoverage); }

        // Rotation presents the requested phase at the little-endian low byte. All ulong
        // and uint steps preserve that phase; only the final ushort advances it by two.
        uint phaseKey = BitOperations.RotateRight(key, (bodyByteOffset & 3) * 8);
        ulong mask64 = phaseKey | ((ulong)phaseKey << 32);
        uint rawCrc = RollingCrc.DefaultInitValue;
        Span<byte> remain = encodedCoverage;
        while (remain.Length >= sizeof(ulong)) {
            ulong plaintext = BinaryPrimitives.ReadUInt64LittleEndian(remain) ^ mask64;
            BinaryPrimitives.WriteUInt64LittleEndian(remain, plaintext);
            rawCrc = BitOperations.Crc32C(rawCrc, plaintext);
            remain = remain[sizeof(ulong)..];
        }
        if (remain.Length >= sizeof(uint)) {
            uint plaintext = BinaryPrimitives.ReadUInt32LittleEndian(remain) ^ phaseKey;
            BinaryPrimitives.WriteUInt32LittleEndian(remain, plaintext);
            rawCrc = BitOperations.Crc32C(rawCrc, plaintext);
            remain = remain[sizeof(uint)..];
        }
        if (remain.Length >= sizeof(ushort)) {
            ushort plaintext = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(remain) ^ (ushort)phaseKey);
            BinaryPrimitives.WriteUInt16LittleEndian(remain, plaintext);
            rawCrc = BitOperations.Crc32C(rawCrc, plaintext);
            remain = remain[sizeof(ushort)..];
            phaseKey >>= 16;
        }
        if (!remain.IsEmpty) {
            byte plaintext = (byte)(remain[0] ^ (byte)phaseKey);
            remain[0] = plaintext;
            rawCrc = BitOperations.Crc32C(rawCrc, plaintext);
        }
        return rawCrc ^ RollingCrc.DefaultFinalXor;
    }

    private static byte KeyByte(uint key, int phase, int relativeByteOffset) =>
        (byte)(key >> (((phase + (relativeByteOffset & 3)) & 3) * 8));

    private static void Validate(int bodyByteOffset, XorMode mode) {
        if (bodyByteOffset < 0) { throw new ArgumentOutOfRangeException(nameof(bodyByteOffset)); }
        if (mode != XorMode.Scalar && mode != XorMode.Vector) { throw new ArgumentOutOfRangeException(nameof(mode)); }
    }
}
