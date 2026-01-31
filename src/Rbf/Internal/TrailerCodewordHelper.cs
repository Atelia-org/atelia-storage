using System.Buffers.Binary;
using Atelia.Data.Hashing;

namespace Atelia.Rbf.Internal;

/// <summary>TrailerCodeword 解析结果（值类型，统一解码一次）。</summary>
/// <remarks>
/// 调用 <c>TrailerCodewordHelper.Parse()</c> 一次即可获得所有字段，无需再调用 DecodeDescriptor。
/// FrameDescriptor 的位字段已展开为只读属性，避免重复解码。
/// 参见设计文档 design-draft.md §3.2。
/// </remarks>
internal readonly struct TrailerCodewordData {
    /// <summary>TrailerCrc32C（已从 BE 解码）。</summary>
    required public uint TrailerCrc32C { get; init; }

    /// <summary>FrameDescriptor 原始值（LE 解码）。</summary>
    required public uint FrameDescriptor { get; init; }

    /// <summary>FrameTag（LE 解码）。</summary>
    required public uint FrameTag { get; init; }

    /// <summary>TailLen（LE 解码）。</summary>
    required public uint TailLen { get; init; }

    // 从 FrameDescriptor 解码的字段（一次解码，多次使用）
    // @[F-FRAME-DESCRIPTOR-LAYOUT]: bit31=IsTombstone, bit30-29=PaddingLen, bit28-16=Reserved, bit15-0=TailMetaLen

    /// <summary>是否为墓碑帧（bit 31）。</summary>
    public bool IsTombstone => (FrameDescriptor >> 31) != 0;

    /// <summary>Padding 长度（bit 30-29，值域 0-3）。</summary>
    public int PaddingLen => (int)((FrameDescriptor >> 29) & 0x3);

    /// <summary>TailMeta 长度（bit 15-0，值域 0-65535）。</summary>
    public int TailMetaLen => (int)(FrameDescriptor & 0xFFFF);
}

/// <summary>TrailerCodeword 编解码辅助类。</summary>
/// <remarks>
/// TrailerCodeword 布局（固定 16 字节）：
/// <code>
/// ┌───────────────┬─────────────────┬──────────┬─────────┐
/// │ TrailerCrc32C │ FrameDescriptor │ FrameTag │ TailLen │
/// │   4B **BE**   │     4B LE       │  4B LE   │  4B LE  │
/// └───────────────┴─────────────────┴──────────┴─────────┘
/// </code>
/// 规范引用：
/// - @[F-TRAILER-CRC-BIG-ENDIAN]: TrailerCrc32C 按 BE 存储
/// - @[F-FRAME-DESCRIPTOR-LAYOUT]: FrameDescriptor 位布局
/// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
/// </remarks>
internal static class TrailerCodewordHelper {
    // 字段大小（基元）
    internal const int TrailerCrcSize = sizeof(uint);
    internal const int DescriptorSize = sizeof(uint);
    internal const int TagSize = sizeof(uint);
    internal const int TailLenSize = sizeof(uint);

    /// <summary>TrailerCodeword 总大小（派生自各字段大小）。</summary>
    public const int Size = TrailerCrcSize + DescriptorSize + TagSize + TailLenSize;

    // 字段偏移（派生自字段大小）
    private const int TrailerCrcOffset = 0;
    private const int DescriptorOffset = TrailerCrcOffset + TrailerCrcSize;
    private const int TagOffset = DescriptorOffset + DescriptorSize;
    private const int TailLenOffset = TagOffset + TagSize;

    // FrameDescriptor 位掩码
    private const uint TombstoneMask = 0x8000_0000u;      // bit 31
    private const uint PaddingLenMask = 0x6000_0000u;     // bit 30-29
    private const int PaddingLenShift = 29;
    private const uint ReservedMask = 0x1FFF_0000u;       // bit 28-16 (MUST=0)
    private const uint TailMetaLenMask = 0x0000_FFFFu;    // bit 15-0

    /// <summary>从完整 16 字节 buffer 解析 TrailerCodeword。</summary>
    /// <param name="buffer">MUST 为完整 16 字节 TrailerCodeword。</param>
    /// <returns>解析后的结构体（字段已从各自端序解码，FrameDescriptor 已展开为属性）。</returns>
    /// <exception cref="ArgumentException">buffer 长度不足 16 字节时抛出。</exception>
    public static TrailerCodewordData Parse(ReadOnlySpan<byte> buffer) {
        if (buffer.Length < Size) { throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer)); }

        return new TrailerCodewordData {
            TrailerCrc32C = BinaryPrimitives.ReadUInt32BigEndian(buffer[TrailerCrcOffset..]),
            FrameDescriptor = BinaryPrimitives.ReadUInt32LittleEndian(buffer[DescriptorOffset..]),
            FrameTag = BinaryPrimitives.ReadUInt32LittleEndian(buffer[TagOffset..]),
            TailLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[TailLenOffset..])
        };
    }

    /// <summary>构建 FrameDescriptor。</summary>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    /// <param name="paddingLen">Padding 长度（0-3）。</param>
    /// <param name="tailMetaLen">TailMeta 长度（0-65535）。</param>
    /// <returns>组装后的 FrameDescriptor。</returns>
    /// <exception cref="ArgumentOutOfRangeException">paddingLen 或 tailMetaLen 超出值域时抛出。</exception>
    public static uint BuildDescriptor(bool isTombstone, int paddingLen, int tailMetaLen) {
        if (paddingLen < 0 || paddingLen > 3) { throw new ArgumentOutOfRangeException(nameof(paddingLen), paddingLen, "PaddingLen must be 0-3."); }
        if (tailMetaLen < 0 || tailMetaLen > 65535) { throw new ArgumentOutOfRangeException(nameof(tailMetaLen), tailMetaLen, "TailMetaLen must be 0-65535."); }

        uint descriptor = 0;
        if (isTombstone) {
            descriptor |= TombstoneMask;
        }
        descriptor |= ((uint)paddingLen << PaddingLenShift) & PaddingLenMask;
        descriptor |= (uint)tailMetaLen & TailMetaLenMask;
        // Reserved bits (bit 28-16) 保持为 0

        return descriptor;
    }

    /// <summary>序列化 TrailerCodeword 并计算写入 CRC。</summary>
    /// <param name="buffer">目标 buffer，MUST 至少 16 字节。</param>
    /// <param name="descriptor">FrameDescriptor 值。</param>
    /// <param name="tag">FrameTag 值。</param>
    /// <param name="tailLen">TailLen 值。</param>
    /// <returns>计算出的 TrailerCrc32C。</returns>
    /// <exception cref="ArgumentException">buffer 长度不足 16 字节时抛出。</exception>
    public static uint Serialize(Span<byte> buffer, uint descriptor, uint tag, uint tailLen) {
        if (buffer.Length < Size) { throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer)); }

        // 写入各字段（LE）
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[DescriptorOffset..], descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[TagOffset..], tag);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[TailLenOffset..], tailLen);

        return RollingCrc.SealCodewordBackward(buffer[..Size]);
    }

    /// <summary>验证 TrailerCrc32C（使用 RollingCrc.CheckCodewordBackward）。</summary>
    /// <param name="trailerCodeword">完整 16 字节 TrailerCodeword。</param>
    /// <returns>CRC 校验是否通过。</returns>
    /// <exception cref="ArgumentException">buffer 长度不足 16 字节时抛出。</exception>
    public static bool CheckTrailerCrc(ReadOnlySpan<byte> trailerCodeword) {
        if (trailerCodeword.Length < Size) { throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(trailerCodeword)); }
        return RollingCrc.CheckCodewordBackward(trailerCodeword[..Size]);
    }

    /// <summary>验证 FrameDescriptor 的保留位是否为 0。</summary>
    /// <param name="descriptor">FrameDescriptor 值。</param>
    /// <returns>保留位是否为 0。</returns>
    public static bool ValidateReservedBits(uint descriptor) {
        return (descriptor & ReservedMask) == 0;
    }

    internal static AteliaResult<TrailerCodewordData> ParseAndValidate(ReadOnlySpan<byte> trailerCodeword) {
        // @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
        if (!CheckTrailerCrc(trailerCodeword)) {
            return AteliaResult<TrailerCodewordData>.Failure(
                new RbfCrcMismatchError(
                    "TrailerCrc32C verification failed.",
                    RecoveryHint: "The frame trailer is corrupted."
                )
            );
        }

        var trailer = Parse(trailerCodeword);

        // @[F-FRAME-DESCRIPTOR-LAYOUT]: bit 28-16 MUST 为 0
        if (!ValidateReservedBits(trailer.FrameDescriptor)) {
            return AteliaResult<TrailerCodewordData>.Failure(
                new RbfFramingError(
                    $"FrameDescriptor reserved bits are not zero: 0x{trailer.FrameDescriptor:X8}.",
                    RecoveryHint: "The frame descriptor is invalid or from a newer format version."
                )
            );
        }

        return AteliaResult<TrailerCodewordData>.Success(trailer);
    }

    internal static AteliaResult<int> ComputePayloadLength(uint tailLen, int tailMetaLen, int paddingLen) {
        if (tailLen > int.MaxValue) {
            return AteliaResult<int>.Failure(
                new RbfFramingError(
                    $"TailLen is too large for int: {tailLen}.",
                    RecoveryHint: "The frame length field is corrupted."
                )
            );
        }

        int payloadLength = (int)tailLen - FrameLayout.FixedOverhead - tailMetaLen - paddingLen;
        if (payloadLength < 0) {
            return AteliaResult<int>.Failure(
                new RbfFramingError(
                    $"Computed PayloadLength is negative: {payloadLength} (TailLen={tailLen}, TailMetaLen={tailMetaLen}, PaddingLen={paddingLen}).",
                    RecoveryHint: "The frame descriptor fields are inconsistent."
                )
            );
        }

        return AteliaResult<int>.Success(payloadLength);
    }
}
