using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

partial class RbfReadImpl {
    /// <summary>从 SizedPtr 读取帧元信息（只读 TrailerCodeword，L2 信任）。</summary>
    /// <param name="file">RBF 文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfFrameInfo（已绑定 file 句柄），失败时返回错误。</returns>
    /// <remarks>
    /// 最小化 I/O：只读取 TrailerCodeword（16B），不读 Payload。
    /// L2 信任：执行 TrailerCrc 校验、reserved bits 校验、TailLen 一致性校验。
    /// 唯一入口：这是创建 RbfFrameInfo 的内部验证路径之一，完成所有结构性验证。
    /// </remarks>
    public static AteliaResult<RbfFrameInfo> ReadFrameInfo(
        SafeFileHandle file,
        SizedPtr ticket
    ) {
        // 1. 基本参数校验
        int ticketLength = ticket.Length;
        if (ticketLength < FrameLayout.MinFrameLength) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfArgumentError(
                    $"Ticket length ({ticketLength}) is less than minimum frame length ({FrameLayout.MinFrameLength}).",
                    RecoveryHint: $"Minimum valid frame size is {FrameLayout.MinFrameLength} bytes."
                )
            );
        }

        // 2. 计算 TrailerCodeword 偏移并读取（16B）
        long trailerOffset = ticket.Offset + ticketLength - TrailerCodewordHelper.Size;
        Span<byte> trailerBuffer = stackalloc byte[TrailerCodewordHelper.Size];
        int bytesRead = RandomAccess.Read(file, trailerBuffer, trailerOffset);

        if (bytesRead < TrailerCodewordHelper.Size) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Short read for TrailerCodeword: expected {TrailerCodewordHelper.Size} bytes, got {bytesRead}.",
                    RecoveryHint: "The file may be truncated."
                )
            );
        }

        // 3. 验证 TrailerCrc32C
        if (!TrailerCodewordHelper.CheckTrailerCrc(trailerBuffer)) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfCrcMismatchError(
                    "TrailerCrc32C verification failed.",
                    RecoveryHint: "The frame trailer is corrupted."
                )
            );
        }

        // 4. 解析 TrailerCodeword
        var trailer = TrailerCodewordHelper.Parse(trailerBuffer);

        // 5. 验证 FrameDescriptor 保留位（bit 28-16 MUST = 0）
        if (!TrailerCodewordHelper.ValidateReservedBits(trailer.FrameDescriptor)) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"FrameDescriptor reserved bits are not zero: 0x{trailer.FrameDescriptor:X8}.",
                    RecoveryHint: "The frame descriptor is invalid or from a newer format version."
                )
            );
        }

        // 6. 验证 TailLen == ticket.Length（一致性校验）
        if (trailer.TailLen != (uint)ticketLength) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"TailLen ({trailer.TailLen}) does not match ticket length ({ticketLength}).",
                    RecoveryHint: "The ticket may be stale or corrupted."
                )
            );
        }

        // 7. 计算 PayloadLength
        int payloadLen = ticketLength - FrameLayout.FixedOverhead - trailer.TailMetaLen - trailer.PaddingLen;
        if (payloadLen < 0) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Computed PayloadLength is negative: {payloadLen} (TailLen={trailer.TailLen}, TailMetaLen={trailer.TailMetaLen}, PaddingLen={trailer.PaddingLen}).",
                    RecoveryHint: "The frame descriptor fields are inconsistent."
                )
            );
        }

        // 8. 构造 RbfFrameInfo（绑定 file 句柄）
        return AteliaResult<RbfFrameInfo>.Success(
            new RbfFrameInfo(
                file: file,
                ticket: ticket,
                tag: trailer.FrameTag,
                payloadLength: payloadLen,
                tailMetaLength: trailer.TailMetaLen,
                isTombstone: trailer.IsTombstone
            )
        );
    }
}
