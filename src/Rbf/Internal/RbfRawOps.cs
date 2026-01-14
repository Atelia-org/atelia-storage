using System.Buffers.Binary;
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
    /// <returns>HeadLen = 4 + 4 + payloadLen + statusLen + 4 + 4</returns>
    /// <remarks>
    /// <para>布局：HeadLen(4) + Tag(4) + Payload(N) + Status(1-4) + TailLen(4) + CRC(4)</para>
    /// <para>参见 @[F-FRAMEBYTES-FIELD-OFFSETS]。</para>
    /// </remarks>
    public static int ComputeHeadLen(int payloadLen) {
        int statusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);
        return 4 + 4 + payloadLen + statusLen + 4 + 4;
    }

    /// <summary>
    /// 序列化完整 FrameBytes 到目标缓冲区。
    /// </summary>
    /// <param name="dest">目标缓冲区，长度必须 ≥ ComputeHeadLen(payload.Length)。</param>
    /// <param name="tag">帧类型标识符。</param>
    /// <param name="payload">业务数据。</param>
    /// <param name="isTombstone">是否为墓碑帧。默认 false。</param>
    /// <returns>实际写入字节数（等于 HeadLen）。</returns>
    /// <remarks>
    /// <para><b>只写入 FrameBytes，不写入 Fence</b>（Fence 由调用方处理）。</para>
    /// <para>写入顺序：HeadLen → Tag → Payload → Status → TailLen → CRC。</para>
    /// <para>CRC 覆盖范围：Tag + Payload + Status + TailLen（@[F-CRC32C-COVERAGE]）。</para>
    /// </remarks>
    /// <exception cref="ArgumentException">dest 长度不足。</exception>
    public static int SerializeFrame(Span<byte> dest, uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        int payloadLen = payload.Length;
        int statusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);
        int headLen = 4 + 4 + payloadLen + statusLen + 4 + 4;

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
        int statusOffset = 8 + payloadLen;
        FrameStatusHelper.FillStatus(dest.Slice(statusOffset, statusLen), isTombstone, statusLen);

        // TailLen (offset 8+N+statusLen)
        int tailLenOffset = statusOffset + statusLen;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[tailLenOffset..], (uint)headLen);

        // CRC32C (offset 8+N+statusLen+4 = headLen-4)
        // CRC 覆盖：Tag(4) + Payload(N) + Status(1-4) + TailLen(4)
        var crcInput = dest[4..(headLen - 4)];
        uint crc = Crc32CHelper.Compute(crcInput);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[(headLen - 4)..], crc);

        return headLen;
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
