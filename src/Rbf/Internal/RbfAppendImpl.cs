using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using Atelia.Data.Hashing;
using System.Diagnostics;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 帧写入实现（v0.40 布局）。</summary>
/// <remarks>
/// v0.40 帧布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PAYLOAD-CRC-COVERAGE]: PayloadCrc 覆盖 Payload + TailMeta + Padding
/// - @[F-TRAILER-CRC-COVERAGE]: TrailerCrc 覆盖 FrameDescriptor + FrameTag + TailLen
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

    /// <summary>填充 TrailerCodeword 和 Fence（v0.40）。</summary>
    /// <param name="buffer">目标 buffer。</param>
    /// <param name="bufferOffset">当前已使用的字节数（会被更新）。</param>
    /// <param name="layout">帧布局。</param>
    /// <param name="tag">帧标签。</param>
    /// <param name="isTombstone">是否为墓碑帧。</param>
    private static int FillPaddingToFence(Span<byte> buffer, int bufferOffset, in FrameLayout layout, uint tag, bool isTombstone) {
        { // Padding
            var paddingLen = layout.PaddingLength;
            buffer.Slice(bufferOffset, paddingLen).Fill(0);
            bufferOffset += paddingLen;
        }

        // 给PayloadCrc流出空洞
        bufferOffset += RbfLayout.PayloadCrcSize;

        { // 填充 TrailerCodeword
            var trailerCodeword = buffer.Slice(bufferOffset, TrailerCodewordHelper.Size);
            layout.FillTrailer(trailerCodeword, tag, isTombstone);
            bufferOffset += TrailerCodewordHelper.Size;
        }

        // Fence
        RbfLayout.Fence.CopyTo(buffer[bufferOffset..]);
        bufferOffset += RbfLayout.FenceSize;

        return bufferOffset;
    }

    /// <summary>把CRC计算和磁盘写入操作内聚在一起。
    /// @[F-PAYLOAD-CRC-COVERAGE](atelia/docs/Rbf/rbf-format.md)。
    /// 当 <c>crcInitialize</c> 为 true 时，会跳过 HeadLen(4) 并初始化 CRC。
    /// 当 <c>crcFinalize</c> 为 true 时，会在写盘前回填 CRC 并推进 <paramref name="fileOffset"/>。</summary>
    private static void WriteWithCrc(SafeFileHandle handle, Span<byte> buffer, ref long fileOffset, ref uint crc, bool crcInitialize, bool crcFinalize) {
        const int bufferAfterCrcCoverage = FrameLayout.LengthAfterPayloadCrcCoverage + RbfLayout.FenceSize; // 跳过尾部的非PayloadCrc覆盖区域
        var crcBuffer = buffer;
        if (crcInitialize) {
            crc = RollingCrc.DefaultInitValue;
            crcBuffer = crcBuffer[FrameLayout.PayloadCrcCoverageStart..]; // 跳过开头的非PayloadCrc覆盖区域
        }
        if (crcFinalize) {
            crcBuffer = crcBuffer[..^bufferAfterCrcCoverage]; // 停在PayloadCrc前
        }
        crc = RollingCrc.CrcForward(crc, crcBuffer);
        if (crcFinalize) {
            crc ^= RollingCrc.DefaultFinalXor;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[^bufferAfterCrcCoverage..], crc); // 填CRC
        }
        RandomAccess.Write(handle, buffer, fileOffset);
        fileOffset += buffer.Length;
    }

    private static void WriteMidWithCrc(SafeFileHandle handle, in ReadOnlySpan<byte> buffer, ref long fileOffset, ref uint crc) {
        crc = RollingCrc.CrcForward(crc, buffer);
        RandomAccess.Write(handle, buffer, fileOffset);
        fileOffset += buffer.Length;
    }

    /// <summary>追加帧到文件。</summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="fileOffset">写入偏移（成功时会更新）。</param>
    /// <param name="payload">Payload 数据。</param>
    /// <param name="tailMeta">用户元数据。</param>
    /// <param name="tag">帧标签（移至 TrailerCodeword）。</param>
    /// <param name="isTombstone">是否为墓碑帧。仅用于测试或诊断。</param>
    /// <returns>成功时返回帧票据（SizedPtr），失败时返回错误。</returns>
    /// <remarks>
    /// 前置校验（写入前检查，失败不会产生任何 I/O）：
    /// - TailMeta 长度 ≤ MaxTailMetaLength (64KB)
    /// - Payload + TailMeta 长度 ≤ MaxPayloadAndMetaLength
    /// - fileOffset 满足 4B 对齐
    /// - fileOffset + frameLength + fenceLength ≤ MaxFileOffset
    /// 自适应 1-4 次写入（v0.40 布局）。header填充和trailer填充复用同一个buffer。
    /// 多次 RandomAccess.Write 不保证原子性。若应用依赖 crash-consistency，需在更上层实现屏障/fsync 语义。
    /// </remarks>
    public static AteliaResult<SizedPtr> Append(
        SafeFileHandle file,
        ref long fileOffset,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> tailMeta,
        uint tag,
        bool isTombstone = false
    ) {
        // === 前置校验（写入前检查，失败不产生 I/O）===

        // 1. TailMeta 长度检查
        if (tailMeta.Length > FrameLayout.MaxTailMetaLength) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"TailMeta length {tailMeta.Length} exceeds maximum {FrameLayout.MaxTailMetaLength}.",
                    RecoveryHint: "Split the metadata or reduce its size."
                )
            );
        }

        // 2. Payload + TailMeta 总长度检查（使用 long 避免 int 溢出）
        long payloadAndMetaLength = (long)payload.Length + tailMeta.Length;
        if (payloadAndMetaLength > FrameLayout.MaxPayloadAndMetaLength) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"Payload+TailMeta length {payloadAndMetaLength} exceeds maximum {FrameLayout.MaxPayloadAndMetaLength}.",
                    RecoveryHint: "Split the payload into multiple frames or compress the data."
                )
            );
        }

        // 3. fileOffset 4B 对齐检查
        if ((fileOffset & RbfLayout.AlignmentMask) != 0) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"File offset {fileOffset} is not 4-byte aligned.",
                    RecoveryHint: "The file may be in an invalid state. Consider truncation or recovery."
                )
            );
        }

        // 4. 检查 frameOffset 是否超出 SizedPtr 可表示范围
        if (fileOffset > MaxFileOffset) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"Frame offset exceeds maximum: {fileOffset} > {MaxFileOffset}.",
                    RecoveryHint: "Rollover to a new file or implement file splitting."
                )
            );
        }
        FrameLayout layout = new FrameLayout(payload.Length, tailMeta.Length);
        AteliaResult<SizedPtr> success = SizedPtr.Create(fileOffset, layout.FrameLength); // 隐式类型转换

        // === 校验通过，执行写入 ===
        int totalWriteLen = layout.FrameLength + RbfLayout.FenceSize; // 由SizedPtr.MaxLength保证
        bool isCopyAsMoreAsPossible = totalWriteLen <= 2 * MaxBufferSize;
        // int bufferSize = Math.Min(totalWriteLen, MaxBufferSize);
        const int bufferSize = MaxBufferSize;

        // write buffer，复用于 header 和 trailer
        Span<byte> buffer = stackalloc byte[bufferSize];
        uint payloadCrc = 0;

        // 优先RandomAccess.Write次数最小化，其次Span.CopyTo最小化。
        // 原则1，如果payload能整体放入HeadBuffer则放入，以避免一次Write(payload)。
        // 原则2，如果tailMeta能整体放入TailBuffer则放入，以避免一次Write(tailMeta)。
        // 原则3，如果`totalWriteLen <= 2*MaxBufferSize`则将payload和tailMeta紧密塞入2次Write(buffer)中。以避免单独Write(payload / tailMeta)。
        // 原则4，其余情况下避免CopyTo，以避免无收益的copy。

        int paddingToFenceLen = layout.PaddingLength + RbfLayout.PayloadCrcSize + RbfLayout.TrailerCodewordSize + RbfLayout.FenceSize;
        { // 先把HeadBuffer Write出去，获取腾挪空间
            // 头部能容纳的 payloadAndMeta 长度
            int remainHeadRoom = bufferSize - FrameLayout.PayloadOffset;

            // HeadLen
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)layout.FrameLength);
            int headOffset = FrameLayout.HeadLenSize;

            { // 关于payload与HeadBuffer
                int payloadInHead = 0;
                if (payload.Length <= remainHeadRoom) {
                    payloadInHead = payload.Length; // 应用原则1
                }
                else if (isCopyAsMoreAsPossible) {
                    payloadInHead = remainHeadRoom; // 应用原则3
                }
                if (payloadInHead > 0) {
                    payload[..payloadInHead].CopyTo(buffer[headOffset..]);
                    headOffset += payloadInHead;
                    remainHeadRoom -= payloadInHead;
                    payload = payload[payloadInHead..];
                }
            }

            { // 关于tailMeta与HeadBuffer
                int tailMetaInHead = isCopyAsMoreAsPossible ? Math.Min(tailMeta.Length, remainHeadRoom) : 0;
                if (tailMetaInHead > 0) {
                    tailMeta[..tailMetaInHead].CopyTo(buffer[headOffset..]);
                    headOffset += tailMetaInHead;
                    remainHeadRoom -= tailMetaInHead;
                    tailMeta = tailMeta[tailMetaInHead..];
                }
            }

            // 1次就能写完的情况
            // Header和Trailer直接写在一个buffer里
            if (totalWriteLen <= MaxBufferSize) {
                Debug.Assert((payload.Length + tailMeta.Length) == 0);
                Debug.Assert(paddingToFenceLen <= remainHeadRoom);
                headOffset = FillPaddingToFence(buffer, headOffset, in layout, tag, isTombstone);
                // 是Header所以crcInitialize，又是Trailer所以crcFinalize
                WriteWithCrc(file, buffer[..headOffset], ref fileOffset, ref payloadCrc, crcInitialize: true, crcFinalize: true);

                return success;
            }
            // 1次写不下，先把HeadBuffer落盘
            // 只是Header所以只crcInitialize
            WriteWithCrc(file, buffer[..headOffset], ref fileOffset, ref payloadCrc, crcInitialize: true, crcFinalize: false);
        } // Write了HeadBuffer

        int tailOffset = bufferSize - paddingToFenceLen;
        { // 下一步处理TailBuffer
            // 从后向前填TailBuffer
            FillPaddingToFence(buffer, tailOffset, in layout, tag, isTombstone);

            { // 关于tailMeta和TailBuffer
                int remainTailMetaLen = tailMeta.Length;
                if (0 < remainTailMetaLen && remainTailMetaLen <= tailOffset) {
                    tailOffset -= remainTailMetaLen;
                    tailMeta.CopyTo(buffer[tailOffset..]);
                    tailMeta = default;
                }
            }
            { // 关于payload和TailBuffer
                int remainPayloadLen = payload.Length;
                if (0 < remainPayloadLen && remainPayloadLen <= tailOffset) {
                    tailOffset -= remainPayloadLen;
                    payload.CopyTo(buffer[tailOffset..]);
                    payload = default;
                }
            }
        }

        // 复杂性都在前面的首尾Buffer，下面就很简单了，剩下的payload / tailMeta直接Write
        if (payload.Length > 0) {
            WriteMidWithCrc(file, in payload, ref fileOffset, ref payloadCrc);
        }
        if (tailMeta.Length > 0) {
            WriteMidWithCrc(file, in tailMeta, ref fileOffset, ref payloadCrc);
        }

        // 填尾Buffer并落盘
        WriteWithCrc(file, buffer[tailOffset..], ref fileOffset, ref payloadCrc, crcInitialize: false, crcFinalize: true);
        return success;
    }

    /// <summary>最大允许的写入后 EndOffset（等于 SizedPtr.MaxOffset）。</summary>
    /// <remarks>
    /// 校验条件是 `frameOffset + frameLength + fenceSize &lt;= MaxFileOffset`，
    /// 通过此校验可确保生成的 SizedPtr 有效（Offset + Length 不超出可表示范围）。
    /// </remarks>
    private const long MaxFileOffset = SizedPtr.MaxOffset;
}
