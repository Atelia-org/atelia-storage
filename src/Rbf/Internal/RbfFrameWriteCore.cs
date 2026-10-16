using System.Buffers.Binary;
using Atelia;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF3 帧写入核心逻辑。</summary>
/// <remarks>
/// 统一 Append 与 Builder 的尾部构建逻辑：
/// - <see cref="ValidateFrameStartOffset"/>：校验帧起点仍可由 <see cref="SizedPtr"/> 表示
/// - <see cref="WritePlaintextTail"/>：构建待转义的 PayloadCrc + TrailerCodeword
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]: PayloadCrc 覆盖 Payload + TailMeta + Padding
/// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
/// </remarks>
internal static class RbfFrameWriteCore {
    /// <summary>明文尾部 = PayloadCrc(4) + TrailerCodeword(16)，不含 Key 或 Fence。</summary>
    internal const int PlaintextTailSize = FrameLayout.PayloadCrcSize + FrameLayout.TrailerCodewordSize;

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
        if (frameStart < 0 || (frameStart & RbfLayout.AlignmentMask) != 0) {
            return new RbfArgumentError("Frame start offset must be nonnegative and aligned to four bytes.");
        }
        if (frameStart > SizedPtr.MaxOffset) {
            return new RbfArgumentError(
                $"Frame start offset ({frameStart}) exceeds SizedPtr.MaxOffset ({SizedPtr.MaxOffset}).",
                RecoveryHint: "The file tail has moved beyond the last offset representable by SizedPtr. Rollover to a new file."
            );
        }
        return null;
    }

    /// <summary>构建供 CRC 后搜索/XOR 的完整明文尾部；layout 决定 TailLen 的 wire 单位。</summary>
    internal static void WritePlaintextTail(Span<byte> buffer, in FrameLayout layout, uint tag, bool isTombstone, uint payloadCrc) {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, payloadCrc);
        layout.FillTrailer(buffer.Slice(FrameLayout.PayloadCrcSize, FrameLayout.TrailerCodewordSize), tag, isTombstone);
    }

}
