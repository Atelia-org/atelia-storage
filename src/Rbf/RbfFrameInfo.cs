using System.Buffers;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>已验证的帧元信息句柄（不含 Payload）。</summary>
/// <remarks>
/// 用于 ScanReverse 产出，支持不读取 payload 的元信息迭代。
/// PayloadLength 与 TailMetaLength 从 TrailerCodeword 解码得出。
/// 句柄语义：构造时已完成 TrailerCrc、reserved bits、TailLen 一致性等验证，
/// 后续读取方法只做 I/O 级校验（buffer length、short read），不重复结构性验证。
/// 生命周期：File 为非拥有引用，调用方 MUST 确保 File 在使用期间有效。
/// 规范引用：@[A-RBF-FRAME-INFO]
/// </remarks>
public readonly struct RbfFrameInfo : IEquatable<RbfFrameInfo> {
    /// <summary>帧位置凭据。</summary>
    public SizedPtr Ticket { get; }

    /// <summary>帧标签。</summary>
    public uint Tag { get; }

    /// <summary>Payload 长度（字节）。</summary>
    public int PayloadLength { get; }

    /// <summary>TailMeta 长度（字节）。</summary>
    public int TailMetaLength { get; }

    /// <summary>是否为墓碑帧。</summary>
    public bool IsTombstone { get; }

    /// <summary>关联的文件句柄（非拥有引用）。</summary>
    internal SafeFileHandle File { get; }

    /// <summary>内部构造函数（只能由验证路径调用）。</summary>
    internal RbfFrameInfo(
        SafeFileHandle file,
        SizedPtr ticket,
        uint tag,
        int payloadLength,
        int tailMetaLength,
        bool isTombstone
    ) {
        File = file;
        Ticket = ticket;
        Tag = tag;
        PayloadLength = payloadLength;
        TailMetaLength = tailMetaLength;
        IsTombstone = isTombstone;
    }

    #region Read Methods（成员方法）

    /// <summary>读取 TailMeta 到调用方提供的 buffer（L2 信任，不校验 PayloadCrc）。</summary>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 最小化 I/O：只读取 TailMeta 区域，不读 Payload 或 TrailerCodeword。
    /// L2 信任：依赖构造时已完成的 TrailerCrc 校验，不做 PayloadCrc。
    /// 生命周期：返回的 TailMeta 直接引用 buffer，调用方 MUST 确保 buffer 有效。
    /// </remarks>
    public AteliaResult<RbfTailMeta> ReadTailMeta(Span<byte> buffer) {
        int tailMetaLen = TailMetaLength;

        // 1. TailMetaLength == 0：直接返回成功 + 空 Span
        if (tailMetaLen == 0) {
            return AteliaResult<RbfTailMeta>.Success(
                new RbfTailMeta(Ticket, Tag, ReadOnlySpan<byte>.Empty, IsTombstone)
            );
        }

        // 2. I/O 级校验：buffer 长度
        if (buffer.Length < tailMetaLen) {
            return AteliaResult<RbfTailMeta>.Failure(
                new RbfBufferTooSmallError(
                    $"Buffer too small for TailMeta: required {tailMetaLen} bytes, provided {buffer.Length} bytes.",
                    RequiredBytes: tailMetaLen,
                    ProvidedBytes: buffer.Length,
                    RecoveryHint: "Ensure buffer is large enough to hold TailMeta."
                )
            );
        }

        // 3. 计算 TailMeta 偏移（结构性验证已在构造时完成）
        // TailMetaOffset = Ticket.Offset + PayloadOffset + PayloadLength
        long tailMetaOffset = Ticket.Offset + FrameLayout.PayloadOffset + PayloadLength;

        // 4. 读取 TailMeta 数据
        var tailMetaBuffer = buffer[..tailMetaLen];
        int tailMetaBytesRead = RandomAccess.Read(File, tailMetaBuffer, tailMetaOffset);

        // 5. I/O 级校验：short read
        if (tailMetaBytesRead < tailMetaLen) {
            return AteliaResult<RbfTailMeta>.Failure(
                new RbfArgumentError(
                    $"Short read for TailMeta: expected {tailMetaLen} bytes, got {tailMetaBytesRead}.",
                    RecoveryHint: "The file may be truncated or info is stale."
                )
            );
        }

        // 6. 构造并返回 RbfTailMeta
        return AteliaResult<RbfTailMeta>.Success(
            new RbfTailMeta(Ticket, Tag, tailMetaBuffer, IsTombstone)
        );
    }

    /// <summary>读取 TailMeta（自动租用 buffer，L2 信任）。</summary>
    /// <returns>成功时返回 RbfPooledTailMeta，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// Buffer 租用：只租 TailMetaLength 大小，不租整帧大小。
    /// TailMetaLength = 0：不租 buffer，返回无 buffer 的 RbfPooledTailMeta。
    /// 生命周期：成功时调用方拥有 buffer 所有权，MUST 调用 Dispose。
    /// </remarks>
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta() {
        int tailMetaLen = TailMetaLength;

        // 1. TailMetaLength == 0：返回无 buffer 的 RbfPooledTailMeta
        if (tailMetaLen == 0) {
            return AteliaResult<RbfPooledTailMeta>.Success(
                new RbfPooledTailMeta(Ticket, Tag, IsTombstone)
            );
        }

        // 2. 从 ArrayPool 租 buffer（只租 TailMetaLength 大小）
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(tailMetaLen);

        try {
            // 3. 计算 TailMeta 偏移（结构性验证已在构造时完成）
            long tailMetaOffset = Ticket.Offset + FrameLayout.PayloadOffset + PayloadLength;

            // 4. 读取 TailMeta 数据（限定 Span 长度）
            var tailMetaBuffer = rentedBuffer.AsSpan(0, tailMetaLen);
            int tailMetaBytesRead = RandomAccess.Read(File, tailMetaBuffer, tailMetaOffset);

            // 5. I/O 级校验：short read
            if (tailMetaBytesRead < tailMetaLen) {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                return AteliaResult<RbfPooledTailMeta>.Failure(
                    new RbfArgumentError(
                        $"Short read for TailMeta: expected {tailMetaLen} bytes, got {tailMetaBytesRead}.",
                        RecoveryHint: "The file may be truncated or info is stale."
                    )
                );
            }

            // 6. 成功：构造 RbfPooledTailMeta
            return AteliaResult<RbfPooledTailMeta>.Success(
                new RbfPooledTailMeta(rentedBuffer, Ticket, Tag, tailMetaLen, IsTombstone)
            );
        }
        catch {
            // 异常路径：归还 buffer 避免泄漏
            ArrayPool<byte>.Shared.Return(rentedBuffer);
            throw;
        }
    }

    /// <summary>读取完整帧到调用方提供的 buffer 中。</summary>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= Ticket.Length。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 委托到 <see cref="RbfReadImpl.ReadFrame"/>，执行完整 framing + CRC 校验。
    /// </remarks>
    public AteliaResult<RbfFrame> ReadFrame(Span<byte> buffer) {
        return RbfReadImpl.ReadFrame(File, in this, buffer);
    }

    /// <summary>读取完整帧（自动租用 buffer）。</summary>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 委托到 <see cref="RbfReadImpl.ReadPooledFrame"/>，执行完整 framing + CRC 校验。
    /// </remarks>
    public AteliaResult<RbfPooledFrame> ReadPooledFrame() {
        return RbfReadImpl.ReadPooledFrame(File, in this);
    }

    #endregion

    #region Equality（用于测试和比较）

    /// <inheritdoc/>
    public bool Equals(RbfFrameInfo other) =>
        Ticket.Equals(other.Ticket) &&
        Tag == other.Tag &&
        PayloadLength == other.PayloadLength &&
        TailMetaLength == other.TailMetaLength &&
        IsTombstone == other.IsTombstone;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RbfFrameInfo other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Ticket, Tag, PayloadLength, TailMetaLength, IsTombstone);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(RbfFrameInfo left, RbfFrameInfo right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(RbfFrameInfo left, RbfFrameInfo right) => !left.Equals(right);

    #endregion
}
