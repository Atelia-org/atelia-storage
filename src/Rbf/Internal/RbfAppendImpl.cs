using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Hashing;
using System.Diagnostics;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 帧写入实现（v0.40 布局）。
/// </summary>
/// <remarks>
/// <para>v0.40 帧布局：[HeadLen][Payload][UserMeta][Padding][PayloadCrc][TrailerCodeword]</para>
/// <para>规范引用：</para>
/// <list type="bullet">
///   <item>@[F-FRAMEBYTES-FIELD-OFFSETS]</item>
///   <item>@[F-CRC32C-COVERAGE]: PayloadCrc 覆盖 Payload + UserMeta + Padding</item>
///   <item>@[F-TRAILERCRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen</item>
/// </list>
/// </remarks>
internal static class RbfAppendImpl {
    /// 统一缓冲区大小（4KB，复用于 header 和 trailer）。
    private const int MaxBufferSizeShift = 12;
    private const int MaxBufferSize = 1 << MaxBufferSizeShift;

    public static int[] GetPayloadEdgeCase() {
        // 由 UnifiedBufferSize 是 4 的倍数且 Rbf 按 4B 对齐保证
        const int OneMax = MaxBufferSize - FrameLayout.FixedOverhead;
        const int TwoMin = OneMax + 1;
        const int TwoMax = 2 * MaxBufferSize - FrameLayout.FixedOverhead;
        const int ThreeMin = TwoMax + 1;
        return [OneMax, TwoMin, TwoMax, ThreeMin];
    }

    /// <summary>
    /// 追加帧到文件（v0.40 布局）。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="writeOffset">写入偏移。</param>
    /// <param name="tag">帧标签（移至 TrailerCodeword）。</param>
    /// <param name="payload">Payload 数据。</param>
    /// <param name="nextTailOffset">输出：下一帧的起始偏移（当前帧末尾 Fence 之后）。</param>
    /// <param name="userMeta">用户元数据（默认空，最大 4000 字节以确保 tail buffer 有足够空间）。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <returns>帧票据（SizedPtr）。</returns>
    /// <remarks>
    /// <para>v0.40 写入顺序：HeadLen → Payload → UserMeta → Padding → PayloadCrc → TrailerCodeword → Fence</para>
    /// <para>注意：多次 RandomAccess.Write 不保证原子性。若应用依赖 crash-consistency，需在更上层实现屏障/fsync 语义。</para>
    /// </remarks>
    public static SizedPtr Append(
        SafeFileHandle file,
        long writeOffset,
        uint tag,
        ReadOnlySpan<byte> payload,
        out long nextTailOffset,
        ReadOnlySpan<byte> userMeta = default,
        bool isTombstone = false
    ) {
        // UserMeta 长度约束：确保 tail buffer 有足够空间容纳 UserMeta + Padding + PayloadCrc + TrailerCodeword + Fence
        // MaxBufferSize(4096) - PayloadCrc(4) - TrailerCodeword(16) - Fence(4) - MaxPadding(3) = 4069
        // 保守设置为 4000，留出安全余量
        const int MaxUserMetaLengthForAppend = 4000;
        if (userMeta.Length > MaxUserMetaLengthForAppend) {
            throw new ArgumentException(
                $"UserMeta length ({userMeta.Length}) exceeds maximum allowed ({MaxUserMetaLengthForAppend}) for single Append operation.",
                nameof(userMeta));
        }

        FrameLayout layout = new FrameLayout(payload.Length, userMeta.Length);
        int totalWriteLen = checked(layout.FrameLength + RbfLayout.FenceSize);
        int actualBufferSize = Math.Min(totalWriteLen, MaxBufferSize);

        // write buffer，复用于 header 和 trailer
        Span<byte> buffer = stackalloc byte[actualBufferSize];

        // 头部能容纳的 payload 长度（v0.40: 没有 Tag 在头部，直接是 Payload）
        const int HeadPayloadCap = MaxBufferSize - FrameLayout.PayloadOffset;

        // 必要，文件游标
        nextTailOffset = writeOffset;
        // 必要，缓存游标
        int headWriteLen, tailWriteLen;

        // PayloadCrc 状态（使用 RollingCrc）
        uint payloadCrc = RollingCrc.DefaultInitValue;

        // payload 分配
        int headPayloadLen = Math.Min(payload.Length, HeadPayloadCap);
        int middlePayloadLen, tailPayloadLen;

        // ==================== 写入 HeadLen ====================
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)layout.FrameLength);
        headWriteLen = FrameLayout.HeadLenSize;

        // ==================== 写入头部 Payload ====================
        payload[..headPayloadLen].CopyTo(buffer[FrameLayout.PayloadOffset..]);
        headWriteLen += headPayloadLen;

        // 剩余 payload
        int remainingPayloadLen = payload.Length - headPayloadLen;

        // Trailer 和 Fence 总长度
        int trailerAndFenceLen = FrameLayout.PayloadCrcSize + FrameLayout.TrailerCodewordSize + RbfLayout.FenceSize;

        if (totalWriteLen <= MaxBufferSize) {
            Debug.Assert(remainingPayloadLen == 0);
            // ==================== 单次写入模式 ====================
            // Header + Payload + UserMeta + Padding + PayloadCrc + TrailerCodeword + Fence 都在一个 buffer

            // 写入 UserMeta
            if (userMeta.Length > 0) {
                userMeta.CopyTo(buffer[layout.UserMetaOffset..]);
                headWriteLen += userMeta.Length;
            }

            // 写入 Padding（清零即可，FrameLayout 已计算好）
            if (layout.PaddingLength > 0) {
                buffer.Slice(layout.PaddingOffset, layout.PaddingLength).Clear();
                headWriteLen += layout.PaddingLength;
            }

            // 计算 PayloadCrc：覆盖 Payload + UserMeta + Padding
            // @[F-CRC32C-COVERAGE]
            var payloadCrcCoverage = buffer.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
            payloadCrc = RollingCrc.CrcForward(payloadCrc, payloadCrcCoverage);
            payloadCrc ^= RollingCrc.DefaultFinalXor;

            // 写入 PayloadCrc（LE）
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[layout.PayloadCrcOffset..], payloadCrc);
            headWriteLen += FrameLayout.PayloadCrcSize;

            // 填充 TrailerCodeword + Fence
            FillTrailerAndFence(buffer, ref headWriteLen, in layout, tag, isTombstone);

            // 写盘
            RandomAccess.Write(file, buffer[..headWriteLen], nextTailOffset);
            nextTailOffset += headWriteLen;

            middlePayloadLen = 0;
            tailPayloadLen = 0;
            tailWriteLen = 0;
        }
        else {
            Debug.Assert(0 <= remainingPayloadLen);
            // ==================== 多次写入模式 ====================

            // 写盘：头部（HeadLen + 部分 Payload）
            RandomAccess.Write(file, buffer[..headWriteLen], nextTailOffset);
            nextTailOffset += headWriteLen;

            // 更新 PayloadCrc：头部 Payload 部分
            payloadCrc = RollingCrc.CrcForward(payloadCrc, buffer.Slice(FrameLayout.PayloadOffset, headPayloadLen));

            tailWriteLen = 0; // 初始化尾游标

            // 计算尾部需要的空间
            int userMetaPaddingTrailerFenceLen = userMeta.Length + layout.PaddingLength + trailerAndFenceLen;

            if (MaxBufferSize * 2 < totalWriteLen) {
                Debug.Assert(MaxBufferSize < remainingPayloadLen + userMetaPaddingTrailerFenceLen);
                // ==================== 三次写入模式 ====================
                // 为避免 copy payload 到 buffer，把剩下的 payload 全都写到中段
                middlePayloadLen = remainingPayloadLen;
                tailPayloadLen = 0;

                // 写盘：中段 Payload（零拷贝）
                var middlePayload = payload.Slice(headPayloadLen, remainingPayloadLen);
                RandomAccess.Write(file, middlePayload, nextTailOffset);
                nextTailOffset += middlePayload.Length;

                // 更新 PayloadCrc
                payloadCrc = RollingCrc.CrcForward(payloadCrc, middlePayload);
            }
            else {
                Debug.Assert(remainingPayloadLen + userMetaPaddingTrailerFenceLen <= MaxBufferSize);
                // ==================== 两次写入模式 ====================
                middlePayloadLen = 0;
                tailPayloadLen = remainingPayloadLen;

                // 剩余 payload 可以用尾 buffer 写完
                if (remainingPayloadLen > 0) {
                    payload.Slice(headPayloadLen, remainingPayloadLen).CopyTo(buffer);
                    tailWriteLen += remainingPayloadLen;
                }
            }

            // ==================== 构建尾部 buffer ====================
            // 尾部包含：[尾部 Payload（如有）][UserMeta][Padding][PayloadCrc][TrailerCodeword][Fence]

            // 写入 UserMeta
            if (userMeta.Length > 0) {
                userMeta.CopyTo(buffer[tailWriteLen..]);
                tailWriteLen += userMeta.Length;
            }

            // 写入 Padding
            if (layout.PaddingLength > 0) {
                buffer.Slice(tailWriteLen, layout.PaddingLength).Clear();
                tailWriteLen += layout.PaddingLength;
            }

            // 更新 PayloadCrc：尾部 Payload + UserMeta + Padding
            // (如果是两次写入，tailPayloadLen > 0; 如果是三次写入，tailPayloadLen == 0)
            int tailCrcCoverageStart = 0; // 从 buffer 开始
            int tailCrcCoverageLen = tailPayloadLen + userMeta.Length + layout.PaddingLength;
            if (tailCrcCoverageLen > 0) {
                payloadCrc = RollingCrc.CrcForward(payloadCrc, buffer.Slice(tailCrcCoverageStart, tailCrcCoverageLen));
            }
            payloadCrc ^= RollingCrc.DefaultFinalXor;

            // 写入 PayloadCrc（LE）
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[tailWriteLen..], payloadCrc);
            tailWriteLen += FrameLayout.PayloadCrcSize;

            // 填充 TrailerCodeword + Fence
            FillTrailerAndFence(buffer, ref tailWriteLen, in layout, tag, isTombstone);

            // 写盘：尾部
            RandomAccess.Write(file, buffer[..tailWriteLen], nextTailOffset);
            nextTailOffset += tailWriteLen;
        }

        Debug.Assert(headWriteLen + middlePayloadLen + tailWriteLen == totalWriteLen);
        Debug.Assert(headPayloadLen + middlePayloadLen + tailPayloadLen == payload.Length);
        return SizedPtr.Create(writeOffset, layout.FrameLength);
    }

    /// <summary>
    /// 填充 TrailerCodeword 和 Fence（v0.40）。
    /// </summary>
    /// <param name="buffer">目标 buffer。</param>
    /// <param name="bufferUsed">当前已使用的字节数（会被更新）。</param>
    /// <param name="layout">帧布局。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    private static void FillTrailerAndFence(Span<byte> buffer, ref int bufferUsed, in FrameLayout layout, uint tag, bool isTombstone) {
        // 填充 TrailerCodeword
        var trailerCodeword = buffer.Slice(bufferUsed, TrailerCodewordHelper.Size);
        layout.FillTrailer(trailerCodeword, tag, isTombstone);
        bufferUsed += TrailerCodewordHelper.Size;

        // Fence
        RbfLayout.Fence.CopyTo(buffer[bufferUsed..]);
        bufferUsed += RbfLayout.FenceSize;
    }
}
