using System.Buffers.Binary;
using Atelia;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 帧写入核心逻辑（v0.40 布局）。</summary>
/// <remarks>
/// 统一 Append 与 Builder 的尾部构建逻辑：
/// - <see cref="ValidateEndOffset"/>：校验写入后 EndOffset 不超出 SizedPtr 可表示范围
/// - <see cref="WriteTail"/>：构建 PayloadCrc + TrailerCodeword + Fence
///
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]: PayloadCrc 覆盖 Payload + TailMeta + Padding
/// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
/// </remarks>
internal static class RbfFrameWriteCore {
    /// <summary>尾部固定长度 = PayloadCrc(4) + TrailerCodeword(16) + Fence(4) = 24 字节。</summary>
    internal const int TailSize = FrameLayout.PayloadCrcSize + FrameLayout.TrailerCodewordSize + RbfLayout.FenceSize;

    /// <summary>最大允许的写入后 EndOffset（等于 SizedPtr.MaxOffset）。</summary>
    /// <remarks>
    /// 校验条件是 `frameOffset + frameLength + fenceSize &lt;= MaxEndOffset`，
    /// 通过此校验可确保生成的 SizedPtr 有效（Offset + Length 不超出可表示范围）。
    /// </remarks>
    internal const long MaxEndOffset = SizedPtr.MaxOffset;

    /// <summary>校验写入后 EndOffset 是否超出 SizedPtr 可表示范围。</summary>
    /// <param name="frameStart">帧起始偏移。</param>
    /// <param name="frameLength">帧长度（不含 Fence）。</param>
    /// <returns>成功时返回 null，失败时返回 RbfArgumentError。</returns>
    /// <remarks>
    /// EndOffset = frameStart + frameLength + FenceSize。
    /// 若超出 MaxEndOffset，返回 Failure，提示文件已达最大容量限制。
    /// </remarks>
    internal static AteliaError? ValidateEndOffset(long frameStart, int frameLength) {
        long endOffset = frameStart + frameLength + RbfLayout.FenceSize;
        if (endOffset > MaxEndOffset) {
            return new RbfArgumentError(
                $"EndOffset ({endOffset}) exceeds MaxEndOffset ({MaxEndOffset}).",
                RecoveryHint: "The file has reached its maximum size limit. Rollover to a new file.");
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
    ///
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
    ///
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

