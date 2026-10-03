using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf.ReadCache;
using System.Diagnostics;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 原始操作集。</summary>
internal static partial class RbfReadImpl {
    #region Read Policy Abstraction (Phase 1)

    /// <summary>读取帧策略接口（静态多态，零运行时开销）。</summary>
    /// <typeparam name="TInput">输入类型（SizedPtr 或 RbfFrameInfo）。</typeparam>
    private interface IReadFramePolicy<TInput> where TInput : allows ref struct {
        /// <summary>验证输入参数。</summary>
        /// <returns>错误时返回 AteliaError，成功返回 null。</returns>
        static abstract AteliaError? ValidateInput(scoped in TInput input);

        /// <summary>获取帧长度。</summary>
        static abstract int GetTicketLength(scoped in TInput input);

        /// <summary>获取文件偏移量。</summary>
        static abstract long GetOffset(scoped in TInput input);

        /// <summary>验证并解析帧数据。</summary>
        static abstract AteliaResult<RbfFrame> ValidateAndParse(scoped in TInput input, int ticketLength, Span<byte> frameBuffer, RbfProfile profile);
    }

    /// <summary>SizedPtr 输入的读取策略。</summary>
    private readonly struct SizedPtrReadPolicy : IReadFramePolicy<SizedPtr> {
        public static AteliaError? ValidateInput(scoped in SizedPtr ticket) {
            // 由SizedPtr的契约与表示方式确保
            Debug.Assert(ticket.Offset % RbfLayout.Alignment == 0);
            Debug.Assert(ticket.Length % RbfLayout.Alignment == 0);

            if (ticket.Length < FrameLayout.MinFrameLength) {
                return new RbfArgumentError(
                    $"Length ({ticket.Length}) is less than minimum frame length ({FrameLayout.MinFrameLength}).",
                    RecoveryHint: $"Minimum valid frame size is {FrameLayout.MinFrameLength} bytes (empty payload, 4-byte status)."
                );
            }

            return null;
        }

        public static int GetTicketLength(scoped in SizedPtr input) => input.Length;

        public static long GetOffset(scoped in SizedPtr input) => input.Offset;

        public static AteliaResult<RbfFrame> ValidateAndParse(scoped in SizedPtr input, int ticketLength, Span<byte> frameBuffer, RbfProfile profile) =>
            ValidateAndParseCore(ticketLength, frameBuffer, input, profile);
    }

    /// <summary>RbfFrameInfo 输入的读取策略。</summary>
    private readonly struct FrameInfoReadPolicy : IReadFramePolicy<RbfFrameInfo> {
        public static AteliaError? ValidateInput(scoped in RbfFrameInfo input) => null; // RbfFrameInfo 已在构造时验证

        public static int GetTicketLength(scoped in RbfFrameInfo input) => input.Ticket.Length;

        public static long GetOffset(scoped in RbfFrameInfo input) => input.Ticket.Offset;

        public static AteliaResult<RbfFrame> ValidateAndParse(scoped in RbfFrameInfo input, int ticketLength, Span<byte> frameBuffer, RbfProfile profile) =>
            ValidateAndParseCoreFromInfo(ticketLength, frameBuffer, in input, profile);
    }

    #endregion
    #region ReadPooledFrame Public API

    /// <summary>从 ArrayPool 借缓存读取帧。调用方 MUST 调用 Dispose() 归还 buffer。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 生命周期：成功时，调用方拥有 buffer 所有权，MUST 调用 Dispose。
    /// 失败路径：buffer 在方法内部自动归还，调用方无需处理。
    /// </remarks>
    public static AteliaResult<RbfPooledFrame> ReadPooledFrame(RandomAccessReader reader, SizedPtr ticket) =>
        ReadPooledFrameCore<SizedPtr, SizedPtrReadPolicy>(reader, in ticket);

    /// <summary>从 ArrayPool 借缓存读取帧（已验证的 RbfFrameInfo 快路径）。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="info">已验证的帧元信息句柄。</param>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 复用创建 info 时已验证的 TrailerCRC/元信息，本次仍校验 PayloadCRC。
    /// </remarks>
    public static AteliaResult<RbfPooledFrame> ReadPooledFrame(RandomAccessReader reader, scoped in RbfFrameInfo info) =>
        ReadPooledFrameCore<RbfFrameInfo, FrameInfoReadPolicy>(reader, in info);

    #endregion

    #region ReadPooledFrame Core (Phase 1: Generic Implementation)

    /// <summary>通用读取帧实现（静态多态，零运行时开销）。</summary>
    /// <typeparam name="TInput">输入类型。</typeparam>
    /// <typeparam name="TPolicy">读取策略。</typeparam>
    private static AteliaResult<RbfPooledFrame> ReadPooledFrameCore<TInput, TPolicy>(RandomAccessReader reader, scoped in TInput input)
        where TInput : allows ref struct
        where TPolicy : IReadFramePolicy<TInput> {
        reader.EnsureUsable();
        // 1. 参数校验
        var error = TPolicy.ValidateInput(in input);
        if (error != null) { return error; }

        int ticketLength = TPolicy.GetTicketLength(in input);
        long offset = TPolicy.GetOffset(in input);
        error = CheckProfileTicketLength(reader.Profile, ticketLength);
        if (error != null) { return error; }
        error = reader.ValidateTicket(SizedPtr.Create(offset, ticketLength));
        if (error != null) { return error; }

        // 2. 从 ArrayPool 借 buffer
        reader.BufferRentObserver?.Invoke(ticketLength);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(ticketLength);

        try {
            // 3. 调用通用 ReadFrameCore（限定 Span 长度）
            var result = ReadFrameCore<TInput, TPolicy>(reader, in input, offset, ticketLength, rentedBuffer.AsSpan(0, ticketLength));

            // 4. 失败路径：归还 buffer 并返回错误
            if (!result.IsSuccess) {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                return result.Error!;
            }

            // 5. 成功路径：直接构造 RbfPooledFrame（class 直接持有 buffer）
            var frame = result.Value;
            var pooledFrame = new RbfPooledFrame(
                buffer: rentedBuffer,
                ptr: frame.Ticket,
                tag: frame.Tag,
                payloadOffset: FrameLayout.PayloadOffset,
                payloadAndMetaLength: frame.PayloadAndMeta.Length,
                tailMetaLength: frame.TailMetaLength,
                isTombstone: frame.IsTombstone
            );

            return pooledFrame;
        }
        catch {
            // 异常路径：归还 buffer 避免泄漏
            ArrayPool<byte>.Shared.Return(rentedBuffer);
            throw;
        }
    }

    #endregion
    #region ReadFrame Public API

    /// <summary>将帧读入调用方提供的 buffer，返回解析后的 RbfFrame。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= ptr.LengthBytes。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 生命周期警告：返回的 RbfFrame.Payload 直接引用 buffer，
    /// 调用方 MUST 确保 buffer 在使用 Payload 期间有效。
    /// 本静态方法使用传入的共享 RandomAccessReader（可能含 cache 与 fault 状态）；复用同一 reader 的调用由所属 File 合同要求调用方串行。
    /// </remarks>
    public static AteliaResult<RbfFrame> ReadFrame(RandomAccessReader reader, SizedPtr ticket, Span<byte> buffer) {
        reader.EnsureUsable();
        var error = SizedPtrReadPolicy.ValidateInput(in ticket) ?? CheckProfileTicketLength(reader.Profile, ticket.Length) ?? reader.ValidateTicket(ticket) ?? CheckBufferLength(ticket.Length, buffer.Length);
        if (error != null) { return error; }

        int ticketLength = ticket.Length;
        return ReadFrameCore<SizedPtr, SizedPtrReadPolicy>(reader, in ticket, ticket.Offset, ticketLength, buffer[..ticketLength]);
    }

    /// <summary>读取已验证的帧到提供的 buffer 中，复用已验证 Trailer 元信息。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="info">已验证的帧元信息句柄。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= info.Ticket.Length。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// info 创建时的 TrailerCRC 资格不代替本次 PayloadCRC 校验。
    /// </remarks>
    public static AteliaResult<RbfFrame> ReadFrame(RandomAccessReader reader, scoped in RbfFrameInfo info, Span<byte> buffer) {
        reader.EnsureUsable();
        int ticketLength = info.Ticket.Length;
        var error = CheckProfileTicketLength(reader.Profile, ticketLength) ?? reader.ValidateTicket(info.Ticket) ?? CheckBufferLength(ticketLength, buffer.Length);
        if (error != null) { return error; }

        return ReadFrameCore<RbfFrameInfo, FrameInfoReadPolicy>(reader, in info, info.Ticket.Offset, ticketLength, buffer[..ticketLength]);
    }

    #endregion

    #region ReadFrame Core (Phase 2: Unified Read Logic)

    /// <summary>通用读取并解析帧（依赖策略提供 ValidateAndParse）。</summary>
    private static AteliaResult<RbfFrame> ReadFrameCore<TInput, TPolicy>(
        RandomAccessReader reader,
        scoped in TInput input,
        long offset,
        int ticketLength,
        Span<byte> frameBuffer
    )
        where TInput : allows ref struct
        where TPolicy : IReadFramePolicy<TInput> {
        // 1. 准备
        Debug.Assert(ticketLength == frameBuffer.Length); // 为简化实现，要求传入恰好长度的buffer

        // 2. 调用 ReadRaw，只读取帧所需的字节
        int bytesRead = reader.Read(frameBuffer, offset);

        // 3. 检查短读
        if (bytesRead < ticketLength) {
            return new RbfArgumentError(
                $"Short read: expected {ticketLength} bytes but got {bytesRead}.",
                RecoveryHint: "The ptr may point beyond end of file or file was truncated."
            );
        }

        // 4. 委托策略执行验证与解析
        return TPolicy.ValidateAndParse(in input, ticketLength, frameBuffer, reader.Profile);
    }

    #endregion

    #region Buffer Validation
    private static AteliaError? CheckProfileTicketLength(RbfProfile profile, int ticketLength) {
        int minimum = RbfLayout.GetMinFrameLength(profile);
        return ticketLength < minimum
            ? new RbfArgumentError($"Length ({ticketLength}) is less than minimum frame length ({minimum}).")
            : null;
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

    #endregion

    #region Validate and Parse (Phase 3: Shared Helpers)

    /// <summary>验证 HeadLen == ticketLength。</summary>
    private static AteliaError? ValidateHeadLen(ReadOnlySpan<byte> frameBuffer, int ticketLength, RbfProfile profile) {
        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(frameBuffer[..FrameLayout.HeadLenSize]);
        var lengthResult = RbfWireCodec.DecodeFrameLength(profile, headLen);
        if (lengthResult.IsFailure || lengthResult.Value != ticketLength) {
            return new RbfFramingError(
                $"HeadLen mismatch: file has {headLen}, expected {ticketLength}.",
                RecoveryHint: "The frame may be corrupted or ptr.Length is incorrect."
            );
        }
        return null;
    }

    /// <summary>验证 PayloadCrc32C。</summary>
    /// <remarks>@[F-PAYLOAD-CRC-COVERAGE]: 覆盖 Payload + TailMeta + Padding</remarks>
    private static AteliaError? ValidatePayloadCrc(ReadOnlySpan<byte> frameBuffer, in FrameLayout layout) {
        ReadOnlySpan<byte> payloadCrcCoverage = frameBuffer.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameBuffer.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize));
        uint computedPayloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);

        if (expectedPayloadCrc != computedPayloadCrc) {
            return new RbfCrcMismatchError(
                $"PayloadCrc mismatch: expected 0x{expectedPayloadCrc:X8}, computed 0x{computedPayloadCrc:X8}.",
                RecoveryHint: "The frame payload data is corrupted."
            );
        }
        return null;
    }

    /// <summary>解析并验证帧数据（v0.40 布局）。</summary>
    /// <remarks>
    /// v0.40 帧布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
    /// Framing 校验清单：
    /// - HeadLen == TailLen
    /// - FrameDescriptor 保留位 (bit 28-16) 为 0
    /// - TailMetaLen &lt;= 65535（16-bit 值域）
    /// - PaddingLen &lt;= 3（2-bit 值域）
    /// - PayloadCrc32C 校验通过
    /// - TrailerCrc32C 校验通过
    /// - PayloadLength &gt;= 0
    /// </remarks>
    private static AteliaResult<RbfFrame> ValidateAndParseCore(int ticketLength, Span<byte> frameBuffer, SizedPtr ticket, RbfProfile profile) {
        // 1. 准备
        Debug.Assert(ticketLength == frameBuffer.Length);

        // 2. 验证基本帧格式：HeadLen == ticketLength
        var headLenError = ValidateHeadLen(frameBuffer, ticketLength, profile);
        if (headLenError != null) { return headLenError; }

        // 3. 从 TrailerCodeword 解析并验证（v0.40）
        int tailSize = TrailerCodewordHelper.Size + RbfLayout.GetTailKeySize(profile);
        var trailerResult = ParseTailBlock(profile, frameBuffer[^tailSize..], out uint key);
        if (!trailerResult.IsSuccess) { return trailerResult.Error!; }
        var trailer = trailerResult.Value;

        // 4. 验证 HeadLen == TailLen
        if (trailer.TailLen != ticketLength) {
            return new RbfFramingError(
                $"TailLen mismatch: TailLen={trailer.TailLen}, HeadLen={ticketLength}.",
                RecoveryHint: "The frame boundaries are corrupted."
            );
        }

        int payloadLength = ticketLength - RbfLayout.GetFixedOverhead(profile) - trailer.TailMetaLen - trailer.PaddingLen;
        var layout = new FrameLayout(profile, payloadLength, trailer.TailMetaLen);
        if (profile == RbfProfile.Rbf3) {
            // The reader/cache copied encoded bytes into the caller's owned buffer.
            // Header and raw Key stay outside the continuous XOR body.
            XorEscape.InPlace(frameBuffer.Slice(FrameLayout.PayloadOffset, ticketLength - 8), key);
            foreach (byte value in frameBuffer.Slice(layout.PaddingOffset, layout.PaddingLength)) {
                if (value != 0) { return new RbfFramingError("Nonzero frame padding."); }
            }
        }

        // 5. PayloadCrc32C 校验
        var payloadCrcError = ValidatePayloadCrc(frameBuffer, in layout);
        if (payloadCrcError != null) { return payloadCrcError; }

        // 6. 构造 RbfFrame 并返回
        // Tag 从 TrailerCodeword 读取（v0.40 Tag 不在头部）
        ReadOnlySpan<byte> payloadAndMeta = frameBuffer.Slice(FrameLayout.PayloadOffset, layout.PayloadAndMetaLength);

        var frame = new RbfFrame(
            ticket: ticket,
            tag: trailer.FrameTag,
            payloadAndMeta: payloadAndMeta,
            tailMetaLength: layout.TailMetaLength,
            isTombstone: trailer.IsTombstone
        );

        return frame;
    }

    /// <summary>解析并验证帧数据（基于已验证的 RbfFrameInfo）。</summary>
    /// <remarks>
    /// 正常文件的已提交帧不可修改，复用 info 已验证的 TrailerCRC/Key/元信息。
    /// 本次仍校验 HeadLen 和完整 PayloadCRC。
    /// </remarks>
    private static AteliaResult<RbfFrame> ValidateAndParseCoreFromInfo(int ticketLength, Span<byte> frameBuffer, scoped in RbfFrameInfo info, RbfProfile profile) {
        Debug.Assert(ticketLength == frameBuffer.Length);
        var headLenError = ValidateHeadLen(frameBuffer, ticketLength, profile);
        if (headLenError != null) { return headLenError; }
        var layout = new FrameLayout(profile, info.PayloadLength, info.TailMetaLength);
        if (layout.FrameLength != ticketLength) {
            return new RbfFramingError(
                $"Frame length derived from RbfFrameInfo does not match ticket length: derived={layout.FrameLength}, ticket={ticketLength}.",
                RecoveryHint: "The RbfFrameInfo does not identify this frame layout."
            );
        }
        if (profile == RbfProfile.Rbf3) {
            XorEscape.InPlace(frameBuffer.Slice(FrameLayout.PayloadOffset, ticketLength - 8), info.EscapeKey);
            foreach (byte value in frameBuffer.Slice(layout.PaddingOffset, layout.PaddingLength)) {
                if (value != 0) { return new RbfFramingError("Nonzero frame padding."); }
            }
        }
        var payloadCrcError = ValidatePayloadCrc(frameBuffer, in layout);
        if (payloadCrcError != null) { return payloadCrcError; }
        return new RbfFrame(info.Ticket, info.Tag,
            frameBuffer.Slice(FrameLayout.PayloadOffset, layout.PayloadAndMetaLength),
            info.TailMetaLength, info.IsTombstone);
    }

    #endregion
}
