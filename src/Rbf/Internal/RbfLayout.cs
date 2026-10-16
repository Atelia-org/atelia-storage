using System.Diagnostics;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF wire format 的偏移与长度定义（集中管理）。</summary>
/// <remarks>
/// 规范引用：
/// - @[F-FENCE-RBF3-ASCII-4B]
/// - @[F-FRAMEBYTES-LAYOUT]
/// </remarks>
internal static class RbfLayout {
    // === Alignment ===
    internal const int Alignment = 1 << SizedPtr.AlignmentShift;
    internal const int AlignmentMask = Alignment - 1;

    // === Fence ===
    internal static ReadOnlySpan<byte> Fence => "RBF3"u8;
    internal const int FenceSize = sizeof(uint);
    internal const int TailKeySize = sizeof(uint);

    internal const uint FenceWord = 0x33464252u;

    // === Header ===
    internal const int HeaderFenceOffset = 0;
    internal const int HeaderOnlyLength = HeaderFenceOffset + FenceSize;
    internal const int FirstFrameOffset = HeaderOnlyLength;

    // === Derived Constants ===
    /// <summary>第一帧尾部 Fence 结束位置的最小可能偏移。</summary>
    internal const int MinFirstFrameFenceEnd = HeaderOnlyLength + MinFrameLength + FenceSize;

    // === TrailerCodeword (v0.40) ===
    /// <summary>TrailerCodeword 固定大小（派生自 TrailerCodewordHelper.Size）。</summary>
    internal const int TrailerCodewordSize = TrailerCodewordHelper.Size;

    // === PayloadCrc (v0.40) ===
    /// <summary>PayloadCrc32C 大小（4 字节）。</summary>
    internal const int PayloadCrcSize = sizeof(uint);

    // === MinFrameLength (v0.40) ===
    /// <summary>最小帧长度 = HeadLenUnits(4) + PayloadCrc(4) + TrailerCodeword(16) + TailKey(4) = 28 字节。
    /// 参见 @[F-FRAMEBYTES-LAYOUT]。</summary>
    internal const int MinFrameLength = sizeof(uint) + PayloadCrcSize + TrailerCodewordSize + TailKeySize;
}

/// <summary>帧布局计算器，采用 RBF3 unit-wire 语义。</summary>
/// <remarks>
/// 关于数据长度命名：定长又不可分的用 Size，变长或复合的用 Length。
/// 布局：[HeadLenUnits][XOR(Payload, TailMeta, Padding, PayloadCrc, TrailerCodeword)][TailKey]
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]
/// - @[F-PADDING-CALCULATION]
/// </remarks>
internal readonly struct FrameLayout {
    private readonly int _payloadLength;
    private readonly int _tailMetaLength;
    private readonly int _paddingLength;

    /// <summary>校验调用方长度并计算布局；写入和公开尺寸计算共用此入口。</summary>
    internal static AteliaResult<FrameLayout> TryCreate(int payloadLength, int tailMetaLength) {
        if (payloadLength < 0) {
            return new RbfArgumentError("Payload length must be non-negative.",
                RecoveryHint: "Supply the final stored payload byte length.");
        }
        if (tailMetaLength < 0 || tailMetaLength > MaxTailMetaLength) {
            return new RbfArgumentError($"tailMetaLength {tailMetaLength} must be between 0 and MaxTailMetaLength ({MaxTailMetaLength}).",
                RecoveryHint: "Reduce TailMeta to at most 65535 bytes and supply a non-negative length.");
        }

        long payloadAndMetaLength = (long)payloadLength + tailMetaLength;
        int maxPayloadAndMetaLength = FrameLayout.MaxPayloadAndMetaLength;
        long alignedLength = (payloadAndMetaLength + RbfLayout.AlignmentMask) & ~(long)RbfLayout.AlignmentMask;
        long frameLength = FrameLayout.FixedOverhead + alignedLength;
        if (payloadAndMetaLength > maxPayloadAndMetaLength || frameLength > MaxFrameLength) {
            return new RbfArgumentError($"Payload + TailMeta exceeds the Rbf3 frame capacity of {maxPayloadAndMetaLength} bytes.",
                RecoveryHint: "Reduce the payload or split it across multiple frames.");
        }

        return new FrameLayout(payloadLength, tailMetaLength);
    }

    /// <summary>以相同布局规则反算含尾 Fence 的追加预算。</summary>
    internal static bool TryGetMaxPayloadLengthForAppendBudget(long byteBudget, int tailMetaLength, out int payloadLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
        if (tailMetaLength < 0 || tailMetaLength > MaxTailMetaLength) {
            throw new ArgumentOutOfRangeException(nameof(tailMetaLength), tailMetaLength,
                $"TailMeta length must be between 0 and {MaxTailMetaLength}.");
        }

        payloadLength = 0;
        FrameLayout minimum = TryCreate(0, tailMetaLength).Unwrap();
        if (byteBudget < (long)minimum.FrameLength + RbfLayout.FenceSize) { return false; }

        // First cap the frame budget in wide arithmetic, then round the body down to its alignment.
        long frameBudget = Math.Min(byteBudget - RbfLayout.FenceSize, MaxFrameLength);
        long bodyBudget = (frameBudget - FrameLayout.FixedOverhead) & ~(long)RbfLayout.AlignmentMask;
        payloadLength = (int)(bodyBudget - tailMetaLength);
        return true;
    }

    internal FrameLayout(int payloadLength, int tailMetaLength = 0) {
        Debug.Assert(0 <= tailMetaLength, "tailMetaLength must be non-negative");
        Debug.Assert(tailMetaLength <= MaxTailMetaLength, "tailMetaLength exceeds MaxTailMetaLength");
        Debug.Assert(0 <= payloadLength, "payloadLength must be non-negative");
        Debug.Assert((long)payloadLength + tailMetaLength <= FrameLayout.MaxPayloadAndMetaLength, "payloadLength exceeds MaxPayloadLength");

        _payloadLength = payloadLength;
        _tailMetaLength = tailMetaLength;
        // @[F-PADDING-CALCULATION]: PaddingLen = (4 - ((payloadLen + tailMetaLen) % 4)) % 4
        _paddingLength = (RbfLayout.Alignment - ((payloadLength + tailMetaLength) & RbfLayout.AlignmentMask)) & RbfLayout.AlignmentMask;

        // 确保 FrameLength 不超过 MaxFrameLength（防止 payloadLength + tailMetaLength 接近上限时溢出）
        Debug.Assert(FrameLength <= MaxFrameLength, "FrameLength exceeds MaxFrameLength");
    }

    #region Field Size / Length
    // === Frame Header ===
    internal const int FrameLenSize = sizeof(uint);
    internal const int HeadLenSize = FrameLenSize;

    // === Frame Payload ===
    internal int PayloadLength => _payloadLength;

    // === TailMeta ===
    internal int TailMetaLength => _tailMetaLength;

    // === Padding ===
    internal int PaddingLength => _paddingLength;

    internal int PayloadAndMetaLength => _payloadLength + _tailMetaLength;

    // === Frame Trailer (v0.40) ===
    internal const int PayloadCrcSize = RbfLayout.PayloadCrcSize;
    internal const int TrailerCodewordSize = RbfLayout.TrailerCodewordSize;
    internal const int TailLenSize = FrameLenSize;
    internal const int TailKeySize = RbfLayout.TailKeySize;
    internal uint WireFrameLength => RbfWireCodec.EncodeFrameLength(FrameLength);
    internal int EncodedBodyLength => FrameLength - HeadLenSize - TailKeySize;

    /// <summary>以 bytes 表示的帧总长度，含 raw TailKey。</summary>
    internal int FrameLength => FrameLayout.FixedOverhead + _payloadLength + _tailMetaLength + _paddingLength;
    #endregion

    #region Statistics
    /// <summary>固定开销 = HeadLenUnits(4) + PayloadCrc(4) + TrailerCodeword(16) + TailKey(4) = 28。</summary>
    internal const int FixedOverhead = HeadLenSize + PayloadCrcSize + TrailerCodewordSize + TailKeySize;

    internal const int MinFrameLength = FixedOverhead;
    internal const int MaxFrameLength = SizedPtr.MaxLength;
    internal const int MaxPayloadAndMetaLength = SizedPtr.MaxLength - FixedOverhead;
    internal const int MaxTailMetaLength = ushort.MaxValue; // 16-bit，参见 @[F-FRAME-DESCRIPTOR-LAYOUT]
    internal const int MaxPaddingLength = 3; // 2-bit，参见 @[F-FRAME-DESCRIPTOR-LAYOUT]
    #endregion

    #region Relative To FrameStart
    internal const int HeadLenOffset = 0;
    /// <summary>Payload 起始偏移 = HeadLenSize (4)。v0.40 格式中 Tag 不在头部。</summary>
    internal const int PayloadOffset = HeadLenSize;

    /// <summary>TailMeta 起始偏移。</summary>
    internal int TailMetaOffset => PayloadOffset + _payloadLength;

    /// <summary>Padding 起始偏移。</summary>
    internal int PaddingOffset => TailMetaOffset + _tailMetaLength;

    /// <summary>PayloadCrc32C 偏移。</summary>
    internal int PayloadCrcOffset => PaddingOffset + _paddingLength;

    /// <summary>TrailerCodeword 偏移。</summary>
    internal int TrailerCodewordOffset => PayloadCrcOffset + PayloadCrcSize;

    /// <summary>raw TailKey 起始偏移。</summary>
    internal int TailKeyOffset => TrailerCodewordOffset + TrailerCodewordSize;
    #endregion

    #region PayloadCrc Coverage
    /// <summary>PayloadCrc 覆盖起始 = Payload 起始。</summary>
    internal const int PayloadCrcCoverageStart = PayloadOffset;

    /// <summary>PayloadCrc 覆盖结束 = PayloadCrc 偏移（不含 PayloadCrc 本身）。</summary>
    internal int PayloadCrcCoverageEnd => PayloadCrcOffset;

    /// <summary>PayloadCrc 覆盖长度 = Payload + TailMeta + Padding。</summary>
    internal int PayloadCrcCoverageLength => _payloadLength + _tailMetaLength + _paddingLength;
    #endregion

    #region TrailerCodeword 操作

    /// <summary>从完整帧的 plaintext body 解析 Trailer；RBF3 raw TailKey 仍在帧末尾。</summary>
    internal static AteliaResult<FrameLayout> ResultFromTrailer(scoped ReadOnlySpan<byte> frameBuffer, out TrailerCodewordData trailer) {
        int minimum = FrameLayout.MinFrameLength;
        if (frameBuffer.Length < minimum) {
            trailer = default;
            return new RbfFramingError(
                $"Frame buffer too small: {frameBuffer.Length} bytes, minimum {minimum} bytes.",
                RecoveryHint: "The frame data is truncated."
            );
        }

        int trailerOffset = frameBuffer.Length - RbfLayout.TailKeySize - TrailerCodewordSize;
        var trailerResult = RbfWireCodec.ParseTrailer(frameBuffer.Slice(trailerOffset, TrailerCodewordSize));
        if (!trailerResult.IsSuccess) {
            trailer = default;
            return trailerResult.Error!;
        }

        trailer = trailerResult.Value;

        if (trailer.TailLen != (uint)frameBuffer.Length) {
            return new RbfFramingError(
                $"TailLen ({trailer.TailLen}) does not match frame buffer length ({frameBuffer.Length}).",
                RecoveryHint: "The frame data is incomplete or TailLen is corrupted."
            );
        }

        var payloadLengthResult = TrailerCodewordHelper.ComputePayloadLength(trailer.TailLen, trailer.TailMetaLen, trailer.PaddingLen);
        if (!payloadLengthResult.IsSuccess) { return payloadLengthResult.Error!; }

        int payloadLength = payloadLengthResult.Value;

        return new FrameLayout(payloadLength, trailer.TailMetaLen);
    }

    /// <summary>填充 plaintext TrailerCodeword，按 RBF3 写入原始 wire 长度。</summary>
    /// <param name="buffer">目标 buffer，MUST 至少 16 字节。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <remarks>
    /// TrailerCodeword 布局（固定 16 字节）：
    /// <code>
    /// [0-3]   TrailerCrc32C   (u32 BE)  ← SealTrailerCrc 计算并写入
    /// [4-7]   FrameDescriptor (u32 LE)
    /// [8-11]  FrameTag        (u32 LE)
    /// [12-15] TailLen         (u32 LE)  ← RBF3 units
    /// </code>
    /// 规范引用：
    /// - @[F-TRAILER-CRC-BIG-ENDIAN]: TrailerCrc 按 BE 存储
    /// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
    /// </remarks>
    internal void FillTrailer(Span<byte> buffer, uint tag, bool isTombstone = false) {
        // 构建 FrameDescriptor
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(isTombstone, _paddingLength, _tailMetaLength);

        // 序列化并写入 CRC
        TrailerCodewordHelper.Serialize(buffer, descriptor, tag, WireFrameLength);
    }
    #endregion
}
