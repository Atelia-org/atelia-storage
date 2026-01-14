using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 原始操作集。
/// </summary>
internal static partial class RbfRawOps {
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
    public static int ComputeFrameLen(int payloadLen, out int statusLen) {
        statusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);
        int headLen = RbfConstants.FrameFixedOverheadBytes + payloadLen + statusLen;
        return headLen;
    }
}
