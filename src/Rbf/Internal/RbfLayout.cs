using System.Diagnostics;
using System.Buffers.Binary;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF wire format 的偏移与长度定义（集中管理）。
/// </summary>
/// <remarks>
/// 规范引用：
/// - @[F-FENCE-VALUE-IS-RBF1-ASCII-4B]
/// - @[F-FRAMEBYTES-FIELD-OFFSETS]
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

    // === TrailerCodeword (v0.40) ===
    /// <summary>TrailerCodeword 固定 16 字节。</summary>
    internal const int TrailerCodewordSize = 16;

    // === PayloadCrc (v0.40) ===
    /// <summary>PayloadCrc32C 大小（4 字节）。</summary>
    internal const int PayloadCrcSize = sizeof(uint);

    // === MinFrameLength (v0.40) ===
    /// <summary>
    /// 最小帧长度 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16) = 24 字节。
    /// 参见 @[F-FRAMEBYTES-FIELD-OFFSETS]。
    /// </summary>
    internal const int MinFrameLength = 24;
}

/// <summary>
/// 帧布局计算器（v0.40 格式）。
/// </summary>
/// <remarks>
/// <para>关于数据长度命名：定长又不可分的用 Size，变长或复合的用 Length。</para>
/// <para>v0.40 布局：[HeadLen][Payload][UserMeta][Padding][PayloadCrc][TrailerCodeword]</para>
/// <para>规范引用：</para>
/// <list type="bullet">
///   <item>@[F-FRAMEBYTES-FIELD-OFFSETS]</item>
///   <item>@[F-CRC32C-COVERAGE]</item>
///   <item>@[F-PADDING-CALCULATION]</item>
/// </list>
/// </remarks>
internal readonly struct FrameLayout {
    private readonly int _payloadLength;
    private readonly int _userMetaLength;
    private readonly int _paddingLength;

    internal FrameLayout(int payloadLength, int userMetaLength = 0) {
        Debug.Assert(0 <= payloadLength, "payloadLength must be non-negative");
        Debug.Assert(payloadLength <= MaxPayloadLength, "payloadLength exceeds MaxPayloadLength");
        Debug.Assert(0 <= userMetaLength, "userMetaLength must be non-negative");
        Debug.Assert(userMetaLength <= MaxUserMetaLength, "userMetaLength exceeds MaxUserMetaLength");

        _payloadLength = payloadLength;
        _userMetaLength = userMetaLength;
        // @[F-PADDING-CALCULATION]: PaddingLen = (4 - ((payloadLen + userMetaLen) % 4)) % 4
        _paddingLength = (RbfLayout.Alignment - ((payloadLength + userMetaLength) & RbfLayout.AlignmentMask)) & RbfLayout.AlignmentMask;

        // 确保 FrameLength 不超过 MaxFrameLength（防止 payloadLength + userMetaLength 接近上限时溢出）
        Debug.Assert(FrameLength <= MaxFrameLength, "FrameLength exceeds MaxFrameLength");
    }

    #region Field Size / Length
    // === Frame Header ===
    internal const int FrameLenSize = sizeof(uint);
    internal const int HeadLenSize = FrameLenSize;

    // === Frame Payload ===
    internal int PayloadLength => _payloadLength;

    // === UserMeta ===
    internal int UserMetaLength => _userMetaLength;

    // === Padding ===
    internal int PaddingLength => _paddingLength;

    // === Frame Trailer (v0.40) ===
    internal const int PayloadCrcSize = RbfLayout.PayloadCrcSize;
    internal const int TrailerCodewordSize = RbfLayout.TrailerCodewordSize;
    internal const int TailLenSize = FrameLenSize;

    /// <summary>
    /// 帧总长度 = HeadLen + Payload + UserMeta + Padding + PayloadCrc + TrailerCodeword。
    /// </summary>
    internal int FrameLength => HeadLenSize + _payloadLength + _userMetaLength + _paddingLength + PayloadCrcSize + TrailerCodewordSize;
    #endregion

    #region Statistics
    /// <summary>固定开销 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16) = 24。</summary>
    internal const int FixedOverhead = HeadLenSize + PayloadCrcSize + TrailerCodewordSize;

    internal const int MinFrameLength = RbfLayout.MinFrameLength; // 24
    internal const int MaxFrameLength = SizedPtr.MaxLength;
    internal const int MaxPayloadLength = SizedPtr.MaxLength - FixedOverhead;
    internal const int MaxUserMetaLength = 65535; // 16-bit，参见 @[F-FRAMEDESCRIPTOR-LAYOUT]
    internal const int MaxPaddingLength = 3; // 2-bit，参见 @[F-FRAMEDESCRIPTOR-LAYOUT]
    #endregion

    #region Relative To FrameStart
    internal const int HeadLenOffset = 0;
    /// <summary>Payload 起始偏移 = HeadLenSize (4)。v0.40 格式中 Tag 不在头部。</summary>
    internal const int PayloadOffset = HeadLenSize;

    /// <summary>UserMeta 起始偏移。</summary>
    internal int UserMetaOffset => PayloadOffset + _payloadLength;

    /// <summary>Padding 起始偏移。</summary>
    internal int PaddingOffset => UserMetaOffset + _userMetaLength;

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

    /// <summary>PayloadCrc 覆盖长度 = Payload + UserMeta + Padding。</summary>
    internal int PayloadCrcCoverageLength => _payloadLength + _userMetaLength + _paddingLength;
    #endregion

    #region DEPRECATED (旧格式兼容层，将在 Task 6.3/6.4/6.5 中移除)
    // 以下常量仅为临时编译兼容，不应在新代码中使用。

    /// <summary>[DEPRECATED] 旧格式的 Tag 大小。v0.40 Tag 已移至 TrailerCodeword。</summary>
    [Obsolete("v0.40 格式中 Tag 不在头部，将在 Task 6.3 中移除")]
    internal const int TagSize = sizeof(uint);

    /// <summary>[DEPRECATED] 旧格式的 Tag 偏移。v0.40 Tag 已移至 TrailerCodeword。</summary>
    [Obsolete("v0.40 格式中 Tag 不在头部，将在 Task 6.3 中移除")]
    internal const int TagOffset = HeadLenOffset + HeadLenSize;

    /// <summary>[DEPRECATED] 旧格式的 CRC 大小。</summary>
    [Obsolete("v0.40 使用双 CRC（PayloadCrc + TrailerCrc），将在 Task 6.3/6.4 中重构")]
    internal const int CrcSize = sizeof(uint);

    /// <summary>[DEPRECATED] 旧格式的 CRC 覆盖起始。</summary>
    [Obsolete("v0.40 使用 PayloadCrcCoverageStart，将在 Task 6.3/6.4 中重构")]
    internal const int CrcCoverageStart = PayloadOffset;

    /// <summary>[DEPRECATED] 旧格式的 CRC 覆盖结束。</summary>
    [Obsolete("v0.40 使用 PayloadCrcCoverageEnd，将在 Task 6.4 中重构")]
    internal int CrcCoverageEnd => PayloadCrcOffset;

    /// <summary>[DEPRECATED] 旧格式 CRC 后长度。</summary>
    [Obsolete("v0.40 布局不再使用此常量，将在 Task 6.3 中移除")]
    internal const int LengthAfterCrcCoverage = CrcSize;

    /// <summary>[DEPRECATED] 旧格式最小状态长度。</summary>
    [Obsolete("v0.40 使用 Padding 替代 Status，将在 Task 6.5 中移除")]
    internal const int MinStatusLength = 1;

    /// <summary>[DEPRECATED] 旧格式最大状态长度。</summary>
    [Obsolete("v0.40 使用 Padding 替代 Status，将在 Task 6.5 中移除")]
    internal const int MaxStatusLength = RbfLayout.Alignment;

    /// <summary>[DEPRECATED] 旧格式最小开销（不含状态）。</summary>
    [Obsolete("v0.40 使用 FixedOverhead，将在 Task 6.3 中移除")]
    internal const int MinOverheadLen = FixedOverhead;

    /// <summary>[DEPRECATED] 旧格式开销（不含 Status）= HeadLen(4) + Tag(4) + TailLen(4) + CRC(4) = 16。</summary>
    [Obsolete("v0.40 使用 FixedOverhead，将在 Task 6.5 中移除")]
    internal const int OverheadLenButStatus = 16;

    /// <summary>[DEPRECATED] 旧格式最小 Trailer 长度。</summary>
    [Obsolete("v0.40 使用 TrailerCodewordSize，将在 Task 6.4 中移除")]
    internal const int MinTrailerLength = MinStatusLength + TailLenSize + CrcSize;

    /// <summary>[DEPRECATED] 旧格式 CRC 偏移（实例属性）。</summary>
    [Obsolete("v0.40 使用 PayloadCrcOffset，将在 Task 6.4 中移除")]
    internal int CrcOffset => PayloadCrcOffset;

    /// <summary>[DEPRECATED] 旧格式状态长度（实例属性）。</summary>
    [Obsolete("v0.40 使用 PaddingLength，将在 Task 6.3/6.4 中移除")]
    internal int StatusLength => _paddingLength > 0 ? _paddingLength : RbfLayout.Alignment;

    /// <summary>[DEPRECATED] 旧格式状态偏移。</summary>
    [Obsolete("v0.40 使用 PaddingOffset，将在 Task 6.3/6.4 中移除")]
    internal int StatusOffset => PaddingOffset;

    /// <summary>[DEPRECATED] 旧格式 TailLen 偏移。</summary>
    [Obsolete("v0.40 TailLen 在 TrailerCodeword 内部，将在 Task 6.4 中移除")]
    internal int TailLenOffset => TrailerCodewordOffset + 12; // TrailerCodeword 内 TailLen 的偏移

    /// <summary>[DEPRECATED] 旧格式 Trailer 长度。</summary>
    [Obsolete("v0.40 使用 TrailerCodewordSize，将在 Task 6.3 中移除")]
    internal int TrailerLength => PayloadCrcSize + TrailerCodewordSize;

    /// <summary>
    /// 从完整帧 buffer 解析 TrailerCodeword 并构造 FrameLayout（v0.40 格式）。
    /// </summary>
    /// <param name="frameBuffer">完整帧数据（从 HeadLen 到 TrailerCodeword 末尾）。</param>
    /// <param name="trailer">输出：解析后的 TrailerCodeword 数据。</param>
    /// <returns>成功时返回 FrameLayout，失败时返回错误。</returns>
    /// <remarks>
    /// <para>Framing 校验：</para>
    /// <list type="bullet">
    ///   <item>FrameDescriptor 保留位 (bit 28-16) 为 0</item>
    ///   <item>TrailerCrc32C 校验通过</item>
    ///   <item>PayloadLength &gt;= 0</item>
    ///   <item>TailLen &gt;= MinFrameLength</item>
    ///   <item>TailLen 4B 对齐</item>
    /// </list>
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
        // @[F-TRAILERCRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
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
        // @[F-FRAMEDESCRIPTOR-LAYOUT]: bit 28-16 MUST 为 0
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

        // 7. 计算 PayloadLength
        // PayloadLength = TailLen - FixedOverhead - UserMetaLen - PaddingLen
        int payloadLength = (int)trailer.TailLen - FixedOverhead - trailer.UserMetaLen - trailer.PaddingLen;
        if (payloadLength < 0) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError(
                    $"Computed PayloadLength is negative: {payloadLength} (TailLen={trailer.TailLen}, UserMetaLen={trailer.UserMetaLen}, PaddingLen={trailer.PaddingLen}).",
                    RecoveryHint: "The frame descriptor fields are inconsistent."
                )
            );
        }

        // 8. 构造 FrameLayout
        return AteliaResult<FrameLayout>.Success(new FrameLayout(payloadLength, trailer.UserMetaLen));
    }

    /// <summary>
    /// 填充 TrailerCodeword（v0.40 格式）。
    /// </summary>
    /// <param name="buffer">目标 buffer，MUST 至少 16 字节。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <remarks>
    /// <para>TrailerCodeword 布局（固定 16 字节）：</para>
    /// <code>
    /// [0-3]   TrailerCrc32C   (u32 BE)  ← SealTrailerCrc 计算并写入
    /// [4-7]   FrameDescriptor (u32 LE)
    /// [8-11]  FrameTag        (u32 LE)
    /// [12-15] TailLen         (u32 LE)  ← 等于 FrameLength
    /// </code>
    /// <para>规范引用：</para>
    /// <list type="bullet">
    ///   <item>@[F-TRAILER-CRC-BIG-ENDIAN]: TrailerCrc 按 BE 存储</item>
    ///   <item>@[F-TRAILERCRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen</item>
    /// </list>
    /// </remarks>
    internal void FillTrailer(Span<byte> buffer, uint tag, bool isTombstone = false) {
        // 构建 FrameDescriptor
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(isTombstone, _paddingLength, _userMetaLength);

        // 序列化（不含 CRC）
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, descriptor, tag, (uint)FrameLength);

        // 计算并写入 TrailerCrc（使用 RollingCrc.SealCodewordBackward）
        TrailerCodewordHelper.SealTrailerCrc(buffer);
    }

    /// <summary>[DEPRECATED] 旧格式 FillTrailer。</summary>
    [Obsolete("v0.40 需要重写填充逻辑，已被新的 FillTrailer(buffer, tag, isTombstone) 替代")]
    internal void FillTrailer(Span<byte> buffer, ref int offset, bool isTombstone = false) {
        // 临时 stub：抛出异常，强制使用新 API
        throw new NotSupportedException("FillTrailer(buffer, ref offset, isTombstone) is deprecated in v0.40. Use FillTrailer(buffer, tag, isTombstone) instead.");
    }

    /// <summary>[DEPRECATED] 旧格式 ValidateStatusConsistency。</summary>
    [Obsolete("v0.40 使用 Padding 替代 Status，将在 Task 6.4 中移除")]
    internal AteliaError? ValidateStatusConsistency(ReadOnlySpan<byte> frameBuffer) {
        // 临时 stub：总是返回 null（不校验）
        return null;
    }
    #endregion
}
