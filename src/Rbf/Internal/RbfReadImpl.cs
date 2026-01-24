using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Hashing;
using System.Diagnostics;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 原始操作集。
/// </summary>
internal static class RbfReadImpl {
    #region ReadTrailerBefore（ScanReverse 核心方法）

    /// <summary>
    /// 读取指定位置之前的帧元信息（只读 TrailerCodeword，不校验 PayloadCrc）。
    /// </summary>
    /// <param name="file">RBF 文件句柄。</param>
    /// <param name="fenceEndOffset">帧尾 Fence 的 EndOffsetExclusive。</param>
    /// <returns>成功时返回 RbfFrameInfo，失败时返回错误。</returns>
    /// <remarks>
    /// <para>算法步骤（design-draft.md §4.2）：</para>
    /// <list type="number">
    ///   <item>边界检查：fenceEndOffset 必须足够容纳 HeaderFence + MinFrame + Fence</item>
    ///   <item>一次读取 TrailerCodeword + Fence（20 字节）</item>
    ///   <item>验证 Fence 是否为 "RBF1"</item>
    ///   <item>验证 TrailerCrc32C</item>
    ///   <item>验证 FrameDescriptor 保留位（bit 28-16 = 0）</item>
    ///   <item>验证 TailLen 值域</item>
    ///   <item>验证 frameStart 不越过 HeaderFence</item>
    ///   <item>计算并验证 PayloadLength</item>
    /// </list>
    /// <para>规范引用：@[A-READ-TRAILER-BEFORE]</para>
    /// </remarks>
    internal static AteliaResult<RbfFrameInfo> ReadTrailerBefore(
        SafeFileHandle file,
        long fenceEndOffset) {
        const int TrailerAndFenceSize = TrailerCodewordHelper.Size + RbfLayout.FenceSize; // 20B

        // 1. 边界检查：fenceEndOffset 必须容纳 HeaderFence + MinFrame + Fence
        long minOffset = RbfLayout.HeaderOnlyLength + RbfLayout.MinFrameLength + RbfLayout.FenceSize;
        if (fenceEndOffset < minOffset) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"No frame before offset {fenceEndOffset}: minimum required is {minOffset}.",
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
        // @[F-FRAMEBYTES-FIELD-OFFSETS]: MinFrameLength = 24
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
        // PayloadLength = TailLen - FixedOverhead - UserMetaLen - PaddingLen
        // FixedOverhead = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16) = 24
        int payloadLen = (int)trailer.TailLen - FrameLayout.FixedOverhead - trailer.UserMetaLen - trailer.PaddingLen;
        if (payloadLen < 0) {
            return AteliaResult<RbfFrameInfo>.Failure(
                new RbfFramingError(
                    $"Computed PayloadLength is negative: {payloadLen} (TailLen={trailer.TailLen}, UserMetaLen={trailer.UserMetaLen}, PaddingLen={trailer.PaddingLen}).",
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
                UserMetaLength: trailer.UserMetaLen,
                IsTombstone: trailer.IsTombstone
            )
        );
    }

    #endregion
    /// <summary>
    /// 从 ArrayPool 借缓存读取帧。调用方 MUST 调用 Dispose() 归还 buffer。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 生命周期：成功时，调用方拥有 buffer 所有权，MUST 调用 Dispose。
    /// 失败路径：buffer 在方法内部自动归还，调用方无需处理。
    /// </remarks>
    public static AteliaResult<RbfPooledFrame> ReadPooledFrame(SafeFileHandle file, SizedPtr ticket) {
        // 1. 参数校验（不含 buffer 长度）
        var error = CheckTicket(ticket, out long offset, out int ticketLength);
        if (error != null) { return AteliaResult<RbfPooledFrame>.Failure(error); }

        // 2. 从 ArrayPool 借 buffer
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(ticketLength);

        try {
            // 3. 调用 ReadFrameCore（限定 Span 长度）
            var result = ReadFrameCore(file, offset, ticketLength, rentedBuffer.AsSpan(0, ticketLength), ticket);

            // 4. 失败路径：归还 buffer 并返回错误
            if (!result.IsSuccess) {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                return AteliaResult<RbfPooledFrame>.Failure(result.Error!);
            }

            // 5. 成功路径：直接构造 RbfPooledFrame（class 直接持有 buffer）
            var frame = result.Value;
            var pooledFrame = new RbfPooledFrame(
                buffer: rentedBuffer,
                ptr: frame.Ticket,
                tag: frame.Tag,
                payloadOffset: FrameLayout.PayloadOffset,
                payloadLength: frame.Payload.Length,
                isTombstone: frame.IsTombstone
            );

            return AteliaResult<RbfPooledFrame>.Success(pooledFrame);
        }
        catch {
            // 异常路径：归还 buffer 避免泄漏
            ArrayPool<byte>.Shared.Return(rentedBuffer);
            throw;
        }
    }
    private static AteliaError? CheckTicket(SizedPtr ticket, out long offset, out int length) {
        offset = ticket.Offset;
        length = ticket.Length;

        // 由SizedPtr的契约与表示方式确保
        Debug.Assert(offset % RbfLayout.Alignment == 0);
        Debug.Assert(length % RbfLayout.Alignment == 0);

        if (length < FrameLayout.MinFrameLength) {
            return new RbfArgumentError(
                $"Length ({length}) is less than minimum frame length ({FrameLayout.MinFrameLength}).",
                RecoveryHint: $"Minimum valid frame size is {FrameLayout.MinFrameLength} bytes (empty payload, 4-byte status)."
            );
        }

        return null;
    }

    /// <summary>
    /// 将帧读入调用方提供的 buffer，返回解析后的 RbfFrame。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= ptr.LengthBytes。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 生命周期警告：返回的 RbfFrame.Payload 直接引用 buffer，
    /// 调用方 MUST 确保 buffer 在使用 Payload 期间有效。
    /// 使用 RandomAccess.Read 实现，无状态，并发安全。
    /// </remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, SizedPtr ticket, Span<byte> buffer) {
        var error = CheckTicket(ticket, out long offset, out int ticketLength) ?? CheckBufferLength(ticket.Length, buffer.Length);
        if (error != null) { return AteliaResult<RbfFrame>.Failure(error); }

        return ReadFrameCore(file, offset, ticketLength, buffer[..ticketLength], ticket);
    }
    private static AteliaError? CheckBufferLength(int ticketLength, int bufferLength) {
        // Buffer 长度校验
        if (bufferLength < ticketLength) {
            return new RbfBufferTooSmallError(
                $"Buffer too small: required {ticketLength} bytes, provided {bufferLength} bytes.",
                RequiredBytes: ticketLength,
                ProvidedBytes: bufferLength,
                RecoveryHint: "Ensure buffer is large enough to hold the entire frame."
            );
        }

        return null;
    }

    private static AteliaResult<RbfFrame> ReadFrameCore(SafeFileHandle file, long offset, int ticketLength, Span<byte> frameBuffer, SizedPtr ticket) {
        // 1. 准备
        Debug.Assert(ticketLength == frameBuffer.Length); // 为简化实现，要求传入恰好长度的buffer

        // 2. 调用 ReadRaw，只读取帧所需的字节
        int bytesRead = RandomAccess.Read(file, frameBuffer, offset);

        // 3. 检查短读
        if (bytesRead < ticketLength) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfArgumentError(
                    $"Short read: expected {ticketLength} bytes but got {bytesRead}.",
                    RecoveryHint: "The ptr may point beyond end of file or file was truncated."
                )
            );
        }

        // 4. 构造结果
        return ValidateAndParseCore(ticketLength, frameBuffer, ticket);
    }

    /// <summary>
    /// 解析并验证帧数据（v0.40 布局）。
    /// </summary>
    /// <remarks>
    /// <para>v0.40 帧布局：[HeadLen][Payload][UserMeta][Padding][PayloadCrc][TrailerCodeword]</para>
    /// <para>Framing 校验清单：</para>
    /// <list type="bullet">
    ///   <item>HeadLen == TailLen</item>
    ///   <item>FrameDescriptor 保留位 (bit 28-16) 为 0</item>
    ///   <item>UserMetaLen &lt;= 65535（16-bit 值域）</item>
    ///   <item>PaddingLen &lt;= 3（2-bit 值域）</item>
    ///   <item>PayloadCrc32C 校验通过</item>
    ///   <item>TrailerCrc32C 校验通过</item>
    ///   <item>PayloadLength &gt;= 0</item>
    /// </list>
    /// </remarks>
    private static AteliaResult<RbfFrame> ValidateAndParseCore(int ticketLength, ReadOnlySpan<byte> frameBuffer, SizedPtr ticket) {
        // 1. 准备
        Debug.Assert(ticketLength == frameBuffer.Length);

        // 2. 验证基本帧格式：HeadLen == ticketLength
        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(frameBuffer[..FrameLayout.HeadLenSize]);
        if (headLen != ticketLength) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfFramingError(
                    $"HeadLen mismatch: file has {headLen}, expected {ticketLength}.",
                    RecoveryHint: "The frame may be corrupted or ptr.Length is incorrect."
                )
            );
        }

        // 3. 从 TrailerCodeword 解析并验证（v0.40）
        var trailerResult = FrameLayout.ResultFromTrailer(frameBuffer, out var trailer);
        if (!trailerResult.IsSuccess) {
            return AteliaResult<RbfFrame>.Failure(trailerResult.Error!);
        }

        var layout = trailerResult.Value;

        // 4. 验证 HeadLen == TailLen
        if (trailer.TailLen != ticketLength) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfFramingError(
                    $"TailLen mismatch: TailLen={trailer.TailLen}, HeadLen={ticketLength}.",
                    RecoveryHint: "The frame boundaries are corrupted."
                )
            );
        }

        // 5. PayloadCrc32C 校验
        // @[F-CRC32C-COVERAGE]: 覆盖 Payload + UserMeta + Padding
        ReadOnlySpan<byte> payloadCrcCoverage = frameBuffer.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameBuffer.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize));
        uint computedPayloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);

        if (expectedPayloadCrc != computedPayloadCrc) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfCrcMismatchError(
                    $"PayloadCrc mismatch: expected 0x{expectedPayloadCrc:X8}, computed 0x{computedPayloadCrc:X8}.",
                    RecoveryHint: "The frame payload data is corrupted."
                )
            );
        }

        // 6. 构造 RbfFrame 并返回
        // Tag 从 TrailerCodeword 读取（v0.40 Tag 不在头部）
        ReadOnlySpan<byte> payload = frameBuffer.Slice(FrameLayout.PayloadOffset, layout.PayloadLength);

        var frame = new RbfFrame {
            Ticket = ticket,
            Tag = trailer.FrameTag,
            Payload = payload,
            IsTombstone = trailer.IsTombstone
        };

        return AteliaResult<RbfFrame>.Success(frame);
    }
}
