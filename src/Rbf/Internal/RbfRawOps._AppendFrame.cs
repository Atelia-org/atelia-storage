using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

partial class RbfRawOps {
    private static void SerializeFrameCore(
        Span<byte> dest,
        int headLen, uint tag,
        ReadOnlySpan<byte> payload,
        bool isTombstone,
        int statusLen
    ) {
        if (dest.Length < headLen) { throw new ArgumentException($"Buffer too small. Required: {headLen}, Actual: {dest.Length}", nameof(dest)); }

        // HeadLen (offset 0)
        BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)headLen);

        // Tag (offset TagFieldOffset)
        BinaryPrimitives.WriteUInt32LittleEndian(dest[RbfConstants.TagFieldOffset..], tag);

        // Payload (offset PayloadFieldOffset)
        payload.CopyTo(dest[RbfConstants.PayloadFieldOffset..]);

        // Status (offset PayloadFieldOffset + payloadLen)
        int payloadLen = payload.Length;
        int statusOffset = RbfConstants.PayloadFieldOffset + payloadLen;
        FrameStatusHelper.FillStatus(dest.Slice(statusOffset, statusLen), isTombstone, statusLen);

        // TailLen (offset statusOffset + statusLen)
        int tailLenOffset = statusOffset + statusLen;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[tailLenOffset..], (uint)headLen);

        // CRC32C (offset headLen - CrcFieldLength)
        // CRC 覆盖：Tag(4) + Payload(N) + Status(1-4) + TailLen(4)
        var crcInput = dest[RbfConstants.TagFieldOffset..(headLen - RbfConstants.CrcFieldLength)];
        uint crc = Crc32CHelper.Compute(crcInput);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[(headLen - RbfConstants.CrcFieldLength)..], crc);
    }

    internal static SizedPtr _AppendFrame(
        SafeFileHandle file,
        long writeOffset,
        uint tag,
        ReadOnlySpan<byte> payload,
        out long nextTailOffset
    ) {
        int headLen = ComputeHeadLen(payload.Length, out int statusLen);
        int totalLen = checked(headLen + RbfConstants.FenceLength);

        // 约定：stackalloc 小帧，ArrayPool 大帧
        const int MaxStackAllocSize = 512;
        byte[]? rentedBuffer = null;
        Span<byte> buffer = totalLen <= MaxStackAllocSize
            ? stackalloc byte[totalLen]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLen)).AsSpan(0, totalLen);

        try {
            // FrameBytes
            SerializeFrameCore(buffer, headLen, tag, payload, false, statusLen);

            // Trailing Fence
            RbfConstants.Fence.CopyTo(buffer.Slice(headLen, RbfConstants.FenceLength));

            // 单次写入：FrameBytes + Fence
            RandomAccess.Write(file, buffer, writeOffset);

            nextTailOffset = writeOffset + totalLen;
            return SizedPtr.Create((ulong)writeOffset, (uint)headLen);
        }
        finally {
            if (rentedBuffer != null) {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }
}
