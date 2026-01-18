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
/// - @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]
/// </remarks>
internal static class RbfLayout {
    // === Alignment ===
    internal const int Alignment = 1 << SizedPtr.AlignmentShift;
    internal const int AlignmentMask = Alignment-1;

    // === Fence ===
    internal static ReadOnlySpan<byte> Fence => "RBF1"u8;
    internal const int FenceSize = sizeof(uint);

    // === Header ===
    internal const int HeaderFenceOffset = 0;
    internal const int HeaderOnlyLength = HeaderFenceOffset + FenceSize;
    internal const int FirstFrameOffset = HeaderOnlyLength;
}

/// <summary>
/// 关于数据长度命名：定长又不可分的用Size，变成或复合的用Length
/// </summary>
/// /// <remarks>
/// 规范引用：
/// - @[F-FRAMEBYTES-FIELD-OFFSETS]
/// - @[F-CRC32C-COVERAGE]
/// </remarks>
internal readonly struct FrameLayout {
    readonly int _payloadLength;
    internal FrameLayout(int payloadLength) {
        Debug.Assert(0 <= payloadLength, "in FrameLayout(int payloadLength) Assert(0 < payloadLength)");
        Debug.Assert(payloadLength <= MaxPayloadLength, "in FrameLayout(int payloadLength) Assert(payloadLength <= MaxPayloadLength)");
        _payloadLength = payloadLength;
    }
    #region Field Size / Length
    // === Frame Header ===
    internal const int FrameLenSize = sizeof(uint);
    internal const int HeadLenSize = FrameLenSize;
    internal const int TagSize = sizeof(uint);

    // === Frame Payload ===
    internal int PayloadLength => _payloadLength;

    // === Frame Trailer ===
    internal int StatusLength => RbfLayout.Alignment - (PayloadLength & RbfLayout.AlignmentMask);
    internal const int TailLenSize = FrameLenSize;
    internal const int CrcSize = sizeof(uint);
    internal int FrameLength => PayloadLength + StatusLength + OverheadLenButStatus;
    #endregion
    #region Statistics
    internal const int OverheadLenButStatus = FrameLenSize + TagSize + TailLenSize + CrcSize;
    internal const int MinStatusLength = 1; // 充要条件是：`(PayloadLength % RbfLayout.Alignment) == RbfLayout.AlignmentMask - 1`
    internal const int MaxStatusLength = RbfLayout.Alignment; // 充要条件是：`(PayloadLength % RbfLayout.Alignment) == 0`
    internal const int MinOverheadLen = OverheadLenButStatus + MinStatusLength;
    internal const int MinFrameLength = OverheadLenButStatus + MaxStatusLength;
    internal const int MaxFrameLength = SizedPtr.MaxLength;
    internal const int MinTrailerLength = MinStatusLength + TailLenSize + CrcSize;
    internal const int MaxPayloadLength = SizedPtr.MaxLength - MinOverheadLen;
    #endregion
    #region Relative To FrameStart
    internal const int HeadLenOffset = 0;
    internal const int TagOffset = HeadLenOffset + HeadLenSize;
    internal const int PayloadOffset = TagOffset + TagSize;
    internal int StatusOffset => PayloadOffset + PayloadLength;
    internal int TailLenOffset => StatusOffset + StatusLength;
    internal int CrcOffset => TailLenOffset + TailLenSize;
    #endregion
    #region Crc
    internal const int CrcCoverageStart = TagOffset;
    internal int CrcCoverageEnd => CrcOffset;
    internal const int LengthAfterCrcCoverage = CrcSize;
    #endregion
    #region Trailer
    /// <summary>Trailer 长度 = Status + TailLen + CRC（不含 Fence）</summary>
    internal int TrailerLength => StatusLength + TailLenSize + CrcSize;

    /// <summary>
    /// 将 Trailer 字节填入缓冲区指定位置。
    /// </summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">写入起始偏移（会被推进）</param>
    /// <param name="isTombstone">是否为墓碑帧</param>
    /// <remarks>不含 CRC 回填（由调用方在写盘前完成）和 Fence。</remarks>
    internal void FillTrailer(Span<byte> buffer, ref int offset, bool isTombstone = false) {
        // Status
        FrameStatusHelper.FillStatus(buffer.Slice(offset, StatusLength), isTombstone, StatusLength);
        offset += StatusLength;

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], (uint)FrameLength);
        offset += TailLenSize;

        // CRC hole（CRC 会由调用方在最终写入前回填）
        offset += CrcSize;
    }

    /// <summary>
    /// 校验 Status 区域是否全字节同值。
    /// </summary>
    /// <param name="frameBuffer">完整帧缓冲区（长度 == FrameLength）</param>
    /// <returns>校验失败时返回错误，成功返回 null。</returns>
    internal AteliaError? ValidateStatusConsistency(ReadOnlySpan<byte> frameBuffer) {
        byte statusByte = frameBuffer[TailLenOffset - 1];
        ReadOnlySpan<byte> statusRegion = frameBuffer.Slice(StatusOffset, StatusLength);
        if (MemoryExtensions.IndexOfAnyExcept(statusRegion, statusByte) >= 0) {
            return new RbfFramingError(
                "Status bytes are not consistent (all bytes should be identical).",
                RecoveryHint: "The status region is corrupted."
            );
        }
        return null;
    }
    #endregion
    #region Relative To FrameEnd
    internal static AteliaResult<FrameLayout> ResultFromTrailer(ReadOnlySpan<byte> buffer, out bool isTombstone, out int statusLength) {
        isTombstone = false;
        statusLength = MinStatusLength;

        int frameEnd = buffer.Length;
        if (frameEnd < MinTrailerLength) {
            return AteliaResult<FrameLayout>.Failure(
                new RbfFramingError($"Buffer too small for trailer. Length:{frameEnd}, MinTrailerLength:{MinTrailerLength}")
            );
        }

        int endLenOffset = frameEnd - CrcSize - TailLenSize;
        int statusTailOffset = endLenOffset - 1;
        byte statusTail = buffer[statusTailOffset];
        var statusError = FrameStatusHelper.DecodeStatusByte(statusTail, out isTombstone, out statusLength);
        if (statusError != null) {
            return AteliaResult<FrameLayout>.Failure(statusError);
        }

        uint endLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[endLenOffset..]);
        if (MaxFrameLength < endLen || endLen < MinFrameLength) {
            return AteliaResult<FrameLayout>.Failure(new RbfFramingError($"Invalid RBF frame TailLen:{endLen}. Valid range:[{MinFrameLength}, {MaxFrameLength}]"));
        }

        int payloadLength = (int)endLen - statusLength - OverheadLenButStatus;
        if (payloadLength < 0) {
            return AteliaResult<FrameLayout>.Failure(new RbfFramingError($"Negative PayloadLength:{payloadLength}. StatusTail:{statusTail}, TailLen:{endLen}"));
        }

        return AteliaResult<FrameLayout>.Success(new FrameLayout(payloadLength));
    }
    #endregion
}
