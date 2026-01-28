using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Hashing;
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
        static abstract AteliaResult<RbfFrame> ValidateAndParse(scoped in TInput input, int ticketLength, ReadOnlySpan<byte> frameBuffer);
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

        public static AteliaResult<RbfFrame> ValidateAndParse(scoped in SizedPtr input, int ticketLength, ReadOnlySpan<byte> frameBuffer) =>
            ValidateAndParseCore(ticketLength, frameBuffer, input);
    }

    /// <summary>RbfFrameInfo 输入的读取策略。</summary>
    private readonly struct FrameInfoReadPolicy : IReadFramePolicy<RbfFrameInfo> {
        public static AteliaError? ValidateInput(scoped in RbfFrameInfo input) => null; // RbfFrameInfo 已在构造时验证

        public static int GetTicketLength(scoped in RbfFrameInfo input) => input.Ticket.Length;

        public static long GetOffset(scoped in RbfFrameInfo input) => input.Ticket.Offset;

        public static AteliaResult<RbfFrame> ValidateAndParse(scoped in RbfFrameInfo input, int ticketLength, ReadOnlySpan<byte> frameBuffer) =>
            ValidateAndParseCoreFromInfo(ticketLength, frameBuffer, in input);
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
    public static AteliaResult<RbfPooledFrame> ReadPooledFrame(SafeFileHandle file, SizedPtr ticket) =>
        ReadPooledFrameCore<SizedPtr, SizedPtrReadPolicy>(file, in ticket);

    /// <summary>从 ArrayPool 借缓存读取帧（已验证的 RbfFrameInfo 快路径）。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="info">已验证的帧元信息句柄。</param>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 此路径跳过 TrailerCodeword 校验与解析，仅做 I/O 级校验与 PayloadCrc 校验。
    /// </remarks>
    public static AteliaResult<RbfPooledFrame> ReadPooledFrame(SafeFileHandle file, scoped in RbfFrameInfo info) =>
        ReadPooledFrameCore<RbfFrameInfo, FrameInfoReadPolicy>(file, in info);

    #endregion

    #region ReadPooledFrame Core (Phase 1: Generic Implementation)

    /// <summary>通用读取帧实现（静态多态，零运行时开销）。</summary>
    /// <typeparam name="TInput">输入类型。</typeparam>
    /// <typeparam name="TPolicy">读取策略。</typeparam>
    private static AteliaResult<RbfPooledFrame> ReadPooledFrameCore<TInput, TPolicy>(SafeFileHandle file, scoped in TInput input)
        where TInput : allows ref struct
        where TPolicy : IReadFramePolicy<TInput> {
        // 1. 参数校验
        var error = TPolicy.ValidateInput(in input);
        if (error != null) { return AteliaResult<RbfPooledFrame>.Failure(error); }

        int ticketLength = TPolicy.GetTicketLength(in input);
        long offset = TPolicy.GetOffset(in input);

        // 2. 从 ArrayPool 借 buffer
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(ticketLength);

        try {
            // 3. 调用通用 ReadFrameCore（限定 Span 长度）
            var result = ReadFrameCore<TInput, TPolicy>(file, in input, offset, ticketLength, rentedBuffer.AsSpan(0, ticketLength));

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
                payloadAndMetaLength: frame.PayloadAndMeta.Length,
                tailMetaLength: frame.TailMetaLength,
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
    /// 使用 RandomAccess.Read 实现，无状态，并发安全。
    /// </remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, SizedPtr ticket, Span<byte> buffer) {
        var error = SizedPtrReadPolicy.ValidateInput(in ticket) ?? CheckBufferLength(ticket.Length, buffer.Length);
        if (error != null) { return AteliaResult<RbfFrame>.Failure(error); }

        int ticketLength = ticket.Length;
        return ReadFrameCore<SizedPtr, SizedPtrReadPolicy>(file, in ticket, ticket.Offset, ticketLength, buffer[..ticketLength]);
    }

    /// <summary>读取已验证的帧到提供的 buffer 中（跳过 TrailerCodeword 校验）。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="info">已验证的帧元信息句柄。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= info.Ticket.Length。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 仅做 I/O 级校验与 PayloadCrc 校验，跳过 TrailerCodeword CRC/保留位/长度一致性校验。
    /// </remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, scoped in RbfFrameInfo info, Span<byte> buffer) {
        int ticketLength = info.Ticket.Length;
        var error = CheckBufferLength(ticketLength, buffer.Length);
        if (error != null) { return AteliaResult<RbfFrame>.Failure(error); }

        return ReadFrameCore<RbfFrameInfo, FrameInfoReadPolicy>(file, in info, info.Ticket.Offset, ticketLength, buffer[..ticketLength]);
    }

    #endregion

    #region ReadFrame Core (Phase 2: Unified Read Logic)

    /// <summary>通用读取并解析帧（依赖策略提供 ValidateAndParse）。</summary>
    private static AteliaResult<RbfFrame> ReadFrameCore<TInput, TPolicy>(
        SafeFileHandle file,
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

        // 4. 委托策略执行验证与解析
        return TPolicy.ValidateAndParse(in input, ticketLength, frameBuffer);
    }

    #endregion

    #region Buffer Validation
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
    private static AteliaError? ValidateHeadLen(ReadOnlySpan<byte> frameBuffer, int ticketLength) {
        uint headLen = BinaryPrimitives.ReadUInt32LittleEndian(frameBuffer[..FrameLayout.HeadLenSize]);
        if (headLen != ticketLength) {
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
    private static AteliaResult<RbfFrame> ValidateAndParseCore(int ticketLength, ReadOnlySpan<byte> frameBuffer, SizedPtr ticket) {
        // 1. 准备
        Debug.Assert(ticketLength == frameBuffer.Length);

        // 2. 验证基本帧格式：HeadLen == ticketLength
        var headLenError = ValidateHeadLen(frameBuffer, ticketLength);
        if (headLenError != null) { return AteliaResult<RbfFrame>.Failure(headLenError); }

        // 3. 从 TrailerCodeword 解析并验证（v0.40）
        var trailerResult = FrameLayout.ResultFromTrailer(frameBuffer, out var trailer);
        if (!trailerResult.IsSuccess) { return AteliaResult<RbfFrame>.Failure(trailerResult.Error!); }

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
        var payloadCrcError = ValidatePayloadCrc(frameBuffer, in layout);
        if (payloadCrcError != null) { return AteliaResult<RbfFrame>.Failure(payloadCrcError); }

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

        return AteliaResult<RbfFrame>.Success(frame);
    }

    /// <summary>解析并验证帧数据（基于已验证的 RbfFrameInfo）。</summary>
    /// <remarks>
    /// 跳过 TrailerCodeword 校验与解析，使用 info 中的 Tag/PayloadLength/TailMetaLength。
    /// 仍然执行 HeadLen 校验与 PayloadCrc 校验。
    /// </remarks>
    private static AteliaResult<RbfFrame> ValidateAndParseCoreFromInfo(int ticketLength, ReadOnlySpan<byte> frameBuffer, scoped in RbfFrameInfo info) {
        // 1. 准备
        Debug.Assert(ticketLength == frameBuffer.Length);

        // 2. 验证基本帧格式：HeadLen == ticketLength
        var headLenError = ValidateHeadLen(frameBuffer, ticketLength);
        if (headLenError != null) { return AteliaResult<RbfFrame>.Failure(headLenError); }

        // 3. 使用 info 计算布局（跳过 TrailerCodeword 解析）
        var layout = new FrameLayout(info.PayloadLength, info.TailMetaLength);
        if (layout.FrameLength != ticketLength) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfFramingError(
                    $"Frame length derived from RbfFrameInfo does not match ticket length: derived={layout.FrameLength}, ticket={ticketLength}.",
                    RecoveryHint: "The RbfFrameInfo may be stale or corrupted."
                )
            );
        }

        // 4. PayloadCrc32C 校验
        var payloadCrcError = ValidatePayloadCrc(frameBuffer, in layout);
        if (payloadCrcError != null) { return AteliaResult<RbfFrame>.Failure(payloadCrcError); }

        // 5. 构造 RbfFrame 并返回
        ReadOnlySpan<byte> payloadAndMeta = frameBuffer.Slice(FrameLayout.PayloadOffset, layout.PayloadAndMetaLength);

        var frame = new RbfFrame(
            ticket: info.Ticket,
            tag: info.Tag,
            payloadAndMeta: payloadAndMeta,
            tailMetaLength: info.TailMetaLength,
            isTombstone: info.IsTombstone
        );

        return AteliaResult<RbfFrame>.Success(frame);
    }

    #endregion
}
