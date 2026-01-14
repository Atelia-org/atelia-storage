using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 原始操作集。
/// </summary>
internal static class RbfRawOps {
    // 帧布局计算 (Frame Layout)

    /// <summary>
    /// 计算 FrameBytes 总长度（HeadLen 字段值）。
    /// </summary>
    /// <param name="payloadLen">Payload 字节数。</param>
    /// <param name="statusLen">输出参数：计算得到的 StatusLen（1-4 字节）。</param>
    /// <returns>HeadLen = 16 + payloadLen + statusLen（其中 16 = HeadLen(4) + Tag(4) + TailLen(4) + CRC(4)）</returns>
    /// <remarks>
    /// <para>布局：HeadLen(4) + Tag(4) + Payload(N) + Status(1-4) + TailLen(4) + CRC(4)</para>
    /// <para>参见 @[F-FRAMEBYTES-FIELD-OFFSETS]。</para>
    /// <para>StatusLen 由 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT] 定义的公式计算，保证 4 字节对齐。</para>
    /// </remarks>
    public static int ComputeHeadLen(int payloadLen, out int statusLen) {
        statusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);
        int headLen = 4 + 4 + payloadLen + statusLen + 4 + 4;
        return headLen;
    }

    private static void SerializeFrameCore(
        Span<byte> dest,
        int headLen, uint tag,
        ReadOnlySpan<byte> payload,
        bool isTombstone,
        int statusLen) {
        if (dest.Length < headLen) {
            throw new ArgumentException($"Buffer too small. Required: {headLen}, Actual: {dest.Length}", nameof(dest));
        }

        // HeadLen (offset 0)
        BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)headLen);

        // Tag (offset 4)
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], tag);

        // Payload (offset 8)
        payload.CopyTo(dest[8..]);

        // Status (offset 8+N)
        int payloadLen = payload.Length;
        int statusOffset = 8 + payloadLen;
        FrameStatusHelper.FillStatus(dest.Slice(statusOffset, statusLen), isTombstone, statusLen);

        // TailLen (offset 8+N+statusLen)
        int tailLenOffset = statusOffset + statusLen;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[tailLenOffset..], (uint)headLen);

        // CRC32C (offset headLen-4)
        // CRC 覆盖：Tag(4) + Payload(N) + Status(1-4) + TailLen(4)
        var crcInput = dest[4..(headLen - 4)];
        uint crc = Crc32CHelper.Compute(crcInput);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[(headLen - 4)..], crc);
    }

    internal static SizedPtr _AppendFrame(
        SafeFileHandle file,
        long writeOffset,
        uint tag,
        ReadOnlySpan<byte> payload,
        out long nextTailOffset) {
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
        } finally {
            if (rentedBuffer != null) {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    // 读路径 (Read Path)

    /// <summary>
    /// 随机读取指定位置的帧。
    /// </summary>
    /// <param name="file">文件句柄（需具备 Read 权限）。</param>
    /// <param name="ptr">帧位置凭据。</param>
    /// <returns>读取结果（成功含帧，失败含错误码）。</returns>
    /// <remarks>使用 RandomAccess.Read 实现，无状态，并发安全。</remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, SizedPtr ptr) {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 创建逆向扫描序列。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="scanOrigin">文件逻辑长度（扫描起点）。</param>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false。</param>
    /// <returns>逆向扫描序列结构。</returns>
    /// <remarks>
    /// <para>RawOps 层直接实现过滤逻辑，与 Facade 层 @[S-RBF-SCANREVERSE-TOMBSTONE-FILTER] 保持一致。</para>
    /// </remarks>
    public static RbfReverseSequence ScanReverse(SafeFileHandle file, long scanOrigin, bool showTombstone = false) {
        throw new NotImplementedException();
    }

    // 写路径 (Write Path)

    /// <summary>
    /// 开始构建一个帧（Complex Path）。
    /// </summary>
    /// <remarks>
    /// <para><b>[Internal]</b>：仅限程序集内调用。</para>
    /// <para>返回的 Builder 内部持有 file 引用和 writeOffset。</para>
    /// </remarks>
    internal static RbfFrameBuilder _BeginFrame(SafeFileHandle file, long writeOffset, uint tag) {
        throw new NotImplementedException();
    }
}
