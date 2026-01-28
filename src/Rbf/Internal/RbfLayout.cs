using System.Diagnostics;
using System.Buffers.Binary;
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

/// <summary>帧布局计算器（v0.40 格式）。</summary>
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

    internal FrameLayout(int payloadLength, int tailMetaLength = 0) {
        Debug.Assert(0 <= tailMetaLength, "tailMetaLength must be non-negative");
        Debug.Assert(tailMetaLength <= MaxTailMetaLength, "tailMetaLength exceeds MaxTailMetaLength");
        Debug.Assert(0 <= payloadLength, "payloadLength must be non-negative");
        Debug.Assert(payloadLength + tailMetaLength <= MaxPayloadAndMetaLength, "payloadLength exceeds MaxPayloadLength");

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

    /// <summary>帧总长度 = HeadLen + Payload + TailMeta + Padding + PayloadCrc + TrailerCodeword。</summary>
    internal int FrameLength => HeadLenSize + _payloadLength + _tailMetaLength + _paddingLength + PayloadCrcSize + TrailerCodewordSize;
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

    /// <summary>从完整帧 buffer 解析 TrailerCodeword 并构造 FrameLayout（v0.40 格式）。</summary>
    /// <param name="frameBuffer">完整帧数据（从 HeadLen 到 TrailerCodeword 末尾）。</param>
    /// <param name="trailer">输出：解析后的 TrailerCodeword 数据。</param>
    /// <returns>成功时返回 FrameLayout，失败时返回错误。</returns>
    /// <remarks>
    /// Framing 校验：
    /// - FrameDescriptor 保留位 (bit 28-16) 为 0
    /// - TrailerCrc32C 校验通过
    /// - PayloadLength &gt;= 0
    /// - TailLen &gt;= MinFrameLength
    /// - TailLen 4B 对齐
    /// </remarks>
    internal static AteliaResult<FrameLayout> ResultFromTrailer(ReadOnlySpan<byte> frameBuffer, out TrailerCodewordData trailer) {
        // 1. 确保 buffer 足够大
        if (frameBuffer.Length < MinFrameLength) {
            trailer = default;
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"Frame buffer too small: {frameBuffer.Length} bytes, minimum {MinFrameLength} bytes.",
                    RecoveryHint: "The frame data is truncated."
                )
            );
        }

        // 2. 定位 TrailerCodeword（帧末尾 16 字节）
        var trailerSpan = frameBuffer.Slice(frameBuffer.Length - TrailerCodewordSize, TrailerCodewordSize);

        // 3. 验证 TrailerCrc32C
        // @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
        if (!TrailerCodewordHelper.CheckTrailerCrc(trailerSpan)) {
            trailer = default;
            return AteliaResult<FrameLayout>.Failure(
                new RbfCrcMismatchError(
                    "TrailerCrc32C verification failed.",
                    RecoveryHint: "The frame trailer is corrupted."
                )
            );
        }

        // 4. 解析 TrailerCodeword
        trailer = TrailerCodewordHelper.Parse(trailerSpan);

        // 5. 验证 FrameDescriptor 保留位
        // @[F-FRAME-DESCRIPTOR-LAYOUT]: bit 28-16 MUST 为 0
        if (!TrailerCodewordHelper.ValidateReservedBits(trailer.FrameDescriptor)) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"FrameDescriptor reserved bits are not zero: 0x{trailer.FrameDescriptor:X8}.",
                    RecoveryHint: "The frame descriptor is invalid or from a newer format version."
                )
            );
        }

        // 6. 验证 TailLen == frameBuffer.Length（完整帧契约）
        // 调用方承诺传入完整帧数据，TailLen 必须与实际长度匹配
        if (trailer.TailLen != (uint)frameBuffer.Length) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"TailLen ({trailer.TailLen}) does not match frame buffer length ({frameBuffer.Length}).",
                    RecoveryHint: "The frame data is incomplete or TailLen is corrupted."
                )
            );
        }

        // 7. 验证 TailLen 基本合法性
        if (trailer.TailLen < MinFrameLength) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"TailLen too small: {trailer.TailLen}, minimum {MinFrameLength}.",
                    RecoveryHint: "The frame length field is corrupted."
                )
            );
        }

        if ((trailer.TailLen & RbfLayout.AlignmentMask) != 0) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"TailLen not 4-byte aligned: {trailer.TailLen}.",
                    RecoveryHint: "The frame length field is corrupted."
                )
            );
        }

        // 8. 计算 PayloadLength
        // PayloadLength = TailLen - FixedOverhead - TailMetaLen - PaddingLen
        int payloadLength = (int)trailer.TailLen - FixedOverhead - trailer.TailMetaLen - trailer.PaddingLen;
        if (payloadLength < 0) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"Computed PayloadLength is negative: {payloadLength} (TailLen={trailer.TailLen}, TailMetaLen={trailer.TailMetaLen}, PaddingLen={trailer.PaddingLen}).",
                    RecoveryHint: "The frame descriptor fields are inconsistent."
                )
            );
        }

        // 9. 构造 FrameLayout
        return AteliaResult<FrameLayout>.Success(new FrameLayout(payloadLength, trailer.TailMetaLen));
    }

    /// <summary>填充 TrailerCodeword（v0.40 格式）。</summary>
    /// <param name="buffer">目标 buffer，MUST 至少 16 字节。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <remarks>
    /// TrailerCodeword 布局（固定 16 字节）：
    /// <code>
    /// [0-3]   TrailerCrc32C   (u32 BE)  ← SealTrailerCrc 计算并写入
    /// [4-7]   FrameDescriptor (u32 LE)
    /// [8-11]  FrameTag        (u32 LE)
    /// [12-15] TailLen         (u32 LE)  ← 等于 FrameLength
    /// </code>
    /// 规范引用：
    /// - @[F-TRAILER-CRC-BIG-ENDIAN]: TrailerCrc 按 BE 存储
    /// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
    /// </remarks>
    internal void FillTrailer(Span<byte> buffer, uint tag, bool isTombstone = false) {
        // 构建 FrameDescriptor
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(isTombstone, _paddingLength, _tailMetaLength);

        // 序列化并写入 CRC
        TrailerCodewordHelper.Serialize(buffer, descriptor, tag, (uint)FrameLength);
    }
    #endregion
}
