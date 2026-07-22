using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf.Internal;

partial class RbfReadImpl {
    /// <summary>从 FrameBytes 起点读取帧元信息（正向扫描路径）。</summary>
    internal static AteliaResult<RbfFrameInfo> ReadFrameInfoAt(
        RandomAccessReader reader,
        long frameStart,
        long dataTail
    ) {
        if (frameStart < RbfLayout.HeaderOnlyLength || (frameStart & RbfLayout.AlignmentMask) != 0) {
            return new RbfFramingError(
                $"Invalid frame start offset: {frameStart}.",
                RecoveryHint: "Forward scan must start at a 4-byte aligned frame boundary after HeaderFence."
            );
        }

        Span<byte> headLenBuffer = stackalloc byte[FrameLayout.HeadLenSize];
        int headBytesRead = reader.Read(headLenBuffer, frameStart);
        if (headBytesRead < FrameLayout.HeadLenSize) {
            return new RbfFramingError(
                $"Short read for HeadLen: expected {FrameLayout.HeadLenSize} bytes, got {headBytesRead}.",
                RecoveryHint: "The file may be truncated."
            );
        }

        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(headLenBuffer);
        if (headLen < FrameLayout.MinFrameLength ||
            headLen > SizedPtr.MaxLength ||
            (headLen & RbfLayout.AlignmentMask) != 0) {
            return new RbfFramingError(
                $"Invalid HeadLen: {headLen} (min={FrameLayout.MinFrameLength}, max={SizedPtr.MaxLength}, must be 4B-aligned).",
                RecoveryHint: "The frame length field is corrupted."
            );
        }

        long frameEnd = frameStart + headLen;
        long fenceEnd = frameEnd + RbfLayout.FenceSize;
        if (frameEnd < frameStart || fenceEnd < frameEnd || fenceEnd > dataTail) {
            return new RbfFramingError(
                $"Frame extends beyond data tail: frameStart={frameStart}, headLen={headLen}, dataTail={dataTail}.",
                RecoveryHint: "The file may be truncated or HeadLen is corrupted."
            );
        }

        Span<byte> fence = stackalloc byte[RbfLayout.FenceSize];
        int fenceBytesRead = reader.Read(fence, frameEnd);
        if (fenceBytesRead < RbfLayout.FenceSize || !fence.SequenceEqual(RbfLayout.Fence)) {
            return new RbfFramingError(
                "Expected Fence ('RBF1') not found after frame.",
                RecoveryHint: "The frame boundary marker is missing or corrupted."
            );
        }

        var ticket = SizedPtr.Create(frameStart, (int)headLen);
        return ReadFrameInfo(reader, ticket);
    }

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
        RandomAccessReader reader,
        SizedPtr ticket
    ) {
        // 1. 基本参数校验
        int ticketLength = ticket.Length;
        if (ticketLength < FrameLayout.MinFrameLength) {
            return new RbfArgumentError(
                $"Ticket length ({ticketLength}) is less than minimum frame length ({FrameLayout.MinFrameLength}).",
                RecoveryHint: $"Minimum valid frame size is {FrameLayout.MinFrameLength} bytes."
            );
        }

        // 2. 计算 TrailerCodeword 偏移并读取（16B）
        long trailerOffset = ticket.Offset + ticketLength - TrailerCodewordHelper.Size;
        Span<byte> trailerBuffer = stackalloc byte[TrailerCodewordHelper.Size];
        int bytesRead = reader.Read(trailerBuffer, trailerOffset);

        if (bytesRead < TrailerCodewordHelper.Size) {
            return new RbfFramingError(
                $"Short read for TrailerCodeword: expected {TrailerCodewordHelper.Size} bytes, got {bytesRead}.",
                RecoveryHint: "The file may be truncated."
            );
        }

        // 3. 验证并解析 TrailerCodeword（CRC + reserved bits）
        var trailerResult = TrailerCodewordHelper.ParseAndValidate(trailerBuffer);
        if (!trailerResult.IsSuccess) { return trailerResult.Error!; }

        var trailer = trailerResult.Value;

        // 6. 验证 TailLen == ticket.Length（一致性校验）
        if (trailer.TailLen != (uint)ticketLength) {
            return new RbfFramingError(
                $"TailLen ({trailer.TailLen}) does not match ticket length ({ticketLength}).",
                RecoveryHint: "The ticket may be stale or corrupted."
            );
        }

        // 7. 计算 PayloadLength
        var payloadLenResult = TrailerCodewordHelper.ComputePayloadLength(trailer.TailLen, trailer.TailMetaLen, trailer.PaddingLen);
        if (!payloadLenResult.IsSuccess) { return payloadLenResult.Error!; }

        int payloadLen = payloadLenResult.Value;

        // 8. 构造 RbfFrameInfo（绑定 file 句柄）
        return new RbfFrameInfo(
            reader: reader,
            ticket: ticket,
            tag: trailer.FrameTag,
            payloadLength: payloadLen,
            tailMetaLength: trailer.TailMetaLen,
            isTombstone: trailer.IsTombstone
        );
    }
}
