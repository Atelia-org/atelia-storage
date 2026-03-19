using System.Buffers.Binary;
using Atelia;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 帧写入核心逻辑（v0.40 布局）。</summary>
/// <remarks>
/// 统一 Append 与 Builder 的尾部构建逻辑：
/// - <see cref="ValidateFrameStartOffset"/>：校验帧起点仍可由 <see cref="SizedPtr"/> 表示
/// - <see cref="WriteTail"/>：构建 PayloadCrc + TrailerCodeword + Fence
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]: PayloadCrc 覆盖 Payload + TailMeta + Padding
/// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
/// </remarks>
internal static class RbfFrameWriteCore {
    /// <summary>尾部固定长度 = PayloadCrc(4) + TrailerCodeword(16) + Fence(4) = 24 字节。</summary>
    internal const int TailSize = FrameLayout.PayloadCrcSize + FrameLayout.TrailerCodewordSize + RbfLayout.FenceSize;

    /// <summary>校验帧起始偏移是否仍可由 <see cref="SizedPtr"/> 表示。</summary>
    /// <param name="frameStart">帧起始偏移。</param>
    /// <returns>成功时返回 null，失败时返回 RbfArgumentError。</returns>
    /// <remarks>
    /// 对 RBF 而言，尾部 Fence 不属于 SizedPtr 指向的帧区间，因此
    /// `frameStart + frameLength + FenceSize` 不需要落在 <see cref="SizedPtr.MaxOffset"/> 内。
    /// 真正需要满足的是：
    /// - frameStart &gt;= 0
    /// - frameStart 4B 对齐
    /// - frameStart &lt;= <see cref="SizedPtr.MaxOffset"/>
    /// </remarks>
    internal static AteliaError? ValidateFrameStartOffset(long frameStart) {
        if (frameStart > SizedPtr.MaxOffset) {
            return new RbfArgumentError(
                $"Frame start offset ({frameStart}) exceeds SizedPtr.MaxOffset ({SizedPtr.MaxOffset}).",
                RecoveryHint: "The file tail has moved beyond the last offset representable by SizedPtr. Rollover to a new file."
            );
        }
        return null;
    }

    /// <summary>填充帧尾部（PayloadCrc + TrailerCodeword + Fence）。</summary>
    /// <param name="buffer">目标 buffer，MUST 至少 <see cref="TailSize"/> 字节。</param>
    /// <param name="layout">帧布局。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <param name="payloadCrc">已 finalized 的 PayloadCrc（已含 XOR）。</param>
    /// <remarks>
    /// 调用方职责：
    /// - Padding 已写入（本方法不负责 padding）
    /// - PayloadCrc 已 finalized（已 XOR DefaultFinalXor）
    /// 布局：[PayloadCrc(4)][TrailerCodeword(16)][Fence(4)]
    /// </remarks>
    internal static void WriteTail(Span<byte> buffer, in FrameLayout layout, uint tag, bool isTombstone, uint payloadCrc) {
        // 写入 PayloadCrc (4 bytes, LE)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, payloadCrc);

        // 写入 TrailerCodeword + Fence
        WriteTrailerAndFence(buffer[FrameLayout.PayloadCrcSize..], in layout, tag, isTombstone);
    }

    /// <summary>填充 TrailerCodeword + Fence（不含 PayloadCrc）。</summary>
    /// <param name="buffer">目标 buffer，MUST 至少 <see cref="TrailerAndFenceSize"/> 字节。</param>
    /// <param name="layout">帧布局。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <remarks>
    /// 供 RbfAppendImpl 使用：其 CRC 计算流程需要预留 PayloadCrc 空洞，
    /// 但 TrailerCodeword + Fence 需要提前填充。
    /// 布局：[TrailerCodeword(16)][Fence(4)]
    /// </remarks>
    internal static void WriteTrailerAndFence(Span<byte> buffer, in FrameLayout layout, uint tag, bool isTombstone) {
        // 写入 TrailerCodeword (16 bytes)
        layout.FillTrailer(buffer[..FrameLayout.TrailerCodewordSize], tag, isTombstone);

        // 写入 Fence (4 bytes, "RBF1")
        RbfLayout.Fence.CopyTo(buffer[FrameLayout.TrailerCodewordSize..]);
    }

    /// <summary>TrailerCodeword + Fence 的大小（20 字节）。</summary>
    internal const int TrailerAndFenceSize = FrameLayout.TrailerCodewordSize + RbfLayout.FenceSize;
}
