using System.Diagnostics;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF wire format 的偏移与长度定义（集中管理）。</summary>
/// <remarks>
/// 规范引用：
/// - @[F-FENCE-RBF1-ASCII-4B]
/// - @[F-FRAMEBYTES-LAYOUT]
/// </remarks>
internal static class RbfLayout {
    // === Alignment ===
    internal const int Alignment = 1 << SizedPtr.AlignmentShift;
    internal const int AlignmentMask = Alignment - 1;

    // === Fence ===
    internal static ReadOnlySpan<byte> Fence => "RBF1"u8;
    internal const int FenceSize = sizeof(uint);
    internal const int TailKeySize = sizeof(uint);

    internal static ReadOnlySpan<byte> GetFence(RbfProfile profile) => profile switch {
        RbfProfile.Rbf1 => Fence,
        RbfProfile.Rbf3 => "RBF3"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown RBF profile.")
    };

    internal static uint GetFenceWord(RbfProfile profile) => profile switch {
        RbfProfile.Rbf1 => 0x31464252u,
        RbfProfile.Rbf3 => 0x33464252u,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown RBF profile.")
    };

    internal static int GetTailKeySize(RbfProfile profile) => profile switch {
        RbfProfile.Rbf1 => 0,
        RbfProfile.Rbf3 => TailKeySize,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown RBF profile.")
    };

    internal static int GetFixedOverhead(RbfProfile profile) => MinFrameLength + GetTailKeySize(profile);
    internal static int GetMinFrameLength(RbfProfile profile) => GetFixedOverhead(profile);
    internal static int GetMaxPayloadAndMetaLength(RbfProfile profile) => SizedPtr.MaxLength - GetFixedOverhead(profile);

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
    /// <summary>最小帧长度 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16) = 24 字节。
    /// 参见 @[F-FRAMEBYTES-LAYOUT]。</summary>
    internal const int MinFrameLength = sizeof(uint) + PayloadCrcSize + TrailerCodewordSize;
}

/// <summary>帧布局计算器，默认构造保留 RBF1 byte-wire 语义。</summary>
/// <remarks>
/// 关于数据长度命名：定长又不可分的用 Size，变长或复合的用 Length。
/// v0.40 布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]
/// - @[F-PADDING-CALCULATION]
/// </remarks>
internal readonly struct FrameLayout {
    private readonly int _payloadLength;
    private readonly int _tailMetaLength;
    private readonly int _paddingLength;

    internal FrameLayout(int payloadLength, int tailMetaLength = 0) : this(RbfProfile.Rbf1, payloadLength, tailMetaLength) { }

    internal FrameLayout(RbfProfile profile, int payloadLength, int tailMetaLength = 0) {
        Profile = profile;
        Debug.Assert(0 <= tailMetaLength, "tailMetaLength must be non-negative");
        Debug.Assert(tailMetaLength <= MaxTailMetaLength, "tailMetaLength exceeds MaxTailMetaLength");
        Debug.Assert(0 <= payloadLength, "payloadLength must be non-negative");
        Debug.Assert((long)payloadLength + tailMetaLength <= RbfLayout.GetMaxPayloadAndMetaLength(profile), "payloadLength exceeds MaxPayloadLength");

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

    internal RbfProfile Profile { get; }
    internal int TailKeyLength => RbfLayout.GetTailKeySize(Profile);
    internal uint WireFrameLength => RbfWireCodec.EncodeFrameLength(Profile, FrameLength);
    internal int EncodedBodyLength => FrameLength - HeadLenSize - TailKeyLength;

    /// <summary>以 bytes 表示的帧总长度，RBF3 另含 raw TailKey。</summary>
    internal int FrameLength => RbfLayout.GetFixedOverhead(Profile) + _payloadLength + _tailMetaLength + _paddingLength;
    #endregion

    #region Statistics
    /// <summary>固定开销 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16) = 24。</summary>
    internal const int FixedOverhead = HeadLenSize + PayloadCrcSize + TrailerCodewordSize;

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

    /// <summary>RBF3 raw TailKey 起始偏移；RBF1 中等于帧结束位置。</summary>
    internal int TailKeyOffset => TrailerCodewordOffset + TrailerCodewordSize;
    #endregion

    #region PayloadCrc Coverage
    /// <summary>PayloadCrc 覆盖起始 = Payload 起始。</summary>
    internal const int PayloadCrcCoverageStart = PayloadOffset;

    /// <summary>PayloadCrc 覆盖结束 = PayloadCrc 偏移（不含 PayloadCrc 本身）。</summary>
    internal int PayloadCrcCoverageEnd => PayloadCrcOffset;

    /// <summary>PayloadCrc 覆盖长度 = Payload + TailMeta + Padding。</summary>
    internal int PayloadCrcCoverageLength => _payloadLength + _tailMetaLength + _paddingLength;
    internal const int LengthAfterPayloadCrcCoverage = PayloadCrcSize + TrailerCodewordSize;
    #endregion

    #region TrailerCodeword 操作

    /// <summary>从完整 RBF1 帧解析 TrailerCodeword 并构造 FrameLayout。</summary>
    internal static AteliaResult<FrameLayout> ResultFromTrailer(scoped ReadOnlySpan<byte> frameBuffer, out TrailerCodewordData trailer) {
        return ResultFromTrailer(RbfProfile.Rbf1, frameBuffer, out trailer);
    }

    /// <summary>从完整帧的 plaintext body 解析 Trailer；RBF3 raw TailKey 仍在帧末尾。</summary>
    internal static AteliaResult<FrameLayout> ResultFromTrailer(RbfProfile profile, scoped ReadOnlySpan<byte> frameBuffer, out TrailerCodewordData trailer) {
        int minimum = RbfLayout.GetMinFrameLength(profile);
        if (frameBuffer.Length < minimum) {
            trailer = default;
            return new RbfFramingError(
                $"Frame buffer too small: {frameBuffer.Length} bytes, minimum {minimum} bytes.",
                RecoveryHint: "The frame data is truncated."
            );
        }

        int trailerOffset = frameBuffer.Length - RbfLayout.GetTailKeySize(profile) - TrailerCodewordSize;
        var trailerResult = RbfWireCodec.ParseTrailer(profile, frameBuffer.Slice(trailerOffset, TrailerCodewordSize));
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

        var payloadLengthResult = TrailerCodewordHelper.ComputePayloadLength(profile, trailer.TailLen, trailer.TailMetaLen, trailer.PaddingLen);
        if (!payloadLengthResult.IsSuccess) { return payloadLengthResult.Error!; }

        int payloadLength = payloadLengthResult.Value;

        return new FrameLayout(profile, payloadLength, trailer.TailMetaLen);
    }

    /// <summary>填充 plaintext TrailerCodeword，按 profile 写入原始 wire 长度。</summary>
    /// <param name="buffer">目标 buffer，MUST 至少 16 字节。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <remarks>
    /// TrailerCodeword 布局（固定 16 字节）：
    /// <code>
    /// [0-3]   TrailerCrc32C   (u32 BE)  ← SealTrailerCrc 计算并写入
    /// [4-7]   FrameDescriptor (u32 LE)
    /// [8-11]  FrameTag        (u32 LE)
    /// [12-15] TailLen         (u32 LE)  ← RBF1 bytes / RBF3 units
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
