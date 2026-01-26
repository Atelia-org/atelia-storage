using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 原始操作集。</summary>
partial class RbfReadImpl {
    /// <summary>读取指定位置之前的帧元信息（只读 TrailerCodeword，不校验 PayloadCrc）。</summary>
    /// <param name="file">RBF 文件句柄。</param>
    /// <param name="fenceEndOffset">帧尾 Fence 的 EndOffsetExclusive。</param>
    /// <returns>成功时返回 RbfFrameInfo，失败时返回错误。</returns>
    /// <remarks>
    /// 规范引用：@[A-READ-TRAILER-BEFORE]
    /// </remarks>
    internal static AteliaResult<RbfFrameInfo> ReadTrailerBefore(
        SafeFileHandle file,
        long fenceEndOffset
    ) {
        const int TrailerAndFenceSize = TrailerCodewordHelper.Size + RbfLayout.FenceSize; // 20B

        // 1. 边界检查：fenceEndOffset 必须 >= MinFirstFrameFenceEnd
        if (fenceEndOffset < RbfLayout.MinFirstFrameFenceEnd) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"No frame before offset {fenceEndOffset}: minimum required is {RbfLayout.MinFirstFrameFenceEnd}.",
                    RecoveryHint: "The offset may be at or before the first frame."
                )
            );
        }

        // 2. 一次读取 TrailerCodeword + Fence (20B)
        Span<byte> buffer = stackalloc byte[TrailerAndFenceSize];
        long readOffset = fenceEndOffset - TrailerAndFenceSize;
        int bytesRead = RandomAccess.Read(file, buffer, readOffset);

        if (bytesRead < TrailerAndFenceSize) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Short read: expected {TrailerAndFenceSize} bytes, got {bytesRead}.",
                    RecoveryHint: "The file may be truncated."
                )
            );
        }

        // 3. 验证 Fence（末尾 4 字节）
        if (!buffer[^RbfLayout.FenceSize..].SequenceEqual(RbfLayout.Fence)) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    "Expected Fence ('RBF1') not found.",
                    RecoveryHint: "The frame boundary marker is missing or corrupted."
                )
            );
        }

        // 4. 解析 TrailerCodeword（前 16 字节）
        var trailerSpan = buffer[..TrailerCodewordHelper.Size];
        var trailer = TrailerCodewordHelper.Parse(trailerSpan);

        // 5. 验证 TrailerCrc32C
        if (!TrailerCodewordHelper.CheckTrailerCrc(trailerSpan)) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    "TrailerCrc32C verification failed.",
                    RecoveryHint: "The frame trailer is corrupted."
                )
            );
        }

        // 6. 验证 FrameDescriptor 保留位（bit 28-16 MUST = 0）
        if (!TrailerCodewordHelper.ValidateReservedBits(trailer.FrameDescriptor)) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"FrameDescriptor reserved bits are not zero: 0x{trailer.FrameDescriptor:X8}.",
                    RecoveryHint: "The frame descriptor is invalid or from a newer format version."
                )
            );
        }

        // 7. 验证 TailLen（包含 int 可表示性检查）
        // @[F-FRAMEBYTES-LAYOUT]: MinFrameLength = 24
        // TailLen MUST 在 [MinFrameLength, int.MaxValue] 且 4B 对齐
        if (trailer.TailLen < RbfLayout.MinFrameLength ||
            trailer.TailLen > int.MaxValue ||
            (trailer.TailLen & RbfLayout.AlignmentMask) != 0) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Invalid TailLen: {trailer.TailLen} (min={RbfLayout.MinFrameLength}, max={int.MaxValue}, must be 4B-aligned).",
                    RecoveryHint: "The frame length field is corrupted."
                )
            );
        }

        // 8. 计算并验证 frameStart
        // frameStart = fenceEndOffset - FenceSize - TailLen
        long frameStart = fenceEndOffset - RbfLayout.FenceSize - trailer.TailLen;
        if (frameStart < RbfLayout.HeaderOnlyLength) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Frame extends before HeaderFence: frameStart={frameStart}, HeaderOnlyLength={RbfLayout.HeaderOnlyLength}.",
                    RecoveryHint: "The TailLen value is too large for this position."
                )
            );
        }

        // 9. 计算 PayloadLength
        // PayloadLength = TailLen - FixedOverhead - TailMetaLen - PaddingLen
        // FixedOverhead = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16) = 24
        int payloadLen = (int)trailer.TailLen - FrameLayout.FixedOverhead - trailer.TailMetaLen - trailer.PaddingLen;
        if (payloadLen < 0) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Computed PayloadLength is negative: {payloadLen} (TailLen={trailer.TailLen}, TailMetaLen={trailer.TailMetaLen}, PaddingLen={trailer.PaddingLen}).",
                    RecoveryHint: "The frame descriptor fields are inconsistent."
                )
            );
        }

        // 10. 构造 RbfFrameInfo
        var ticket = SizedPtr.Create(frameStart, (int)trailer.TailLen);
        return AteliaResult<RbfFrameInfo>.Success(
            new RbfFrameInfo(
                Ticket: ticket,
                Tag: trailer.FrameTag,
                PayloadLength: payloadLen,
                TailMetaLength: trailer.TailMetaLen,
                IsTombstone: trailer.IsTombstone
            )
        );
    }
}
