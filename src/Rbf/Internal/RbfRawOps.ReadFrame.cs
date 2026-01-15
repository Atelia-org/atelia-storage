using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 原始操作集。
/// </summary>
internal static partial class RbfRawOps {
    /// <summary>
    /// Buffer 阈值：小于此值使用 stackalloc，否则使用 ArrayPool。
    /// </summary>
    private const int MaxStackAllocSize = 4096;

    /// <summary>
    /// 最小帧长度 = HeadLen(4) + Tag(4) + Status(4) + TailLen(4) + CRC(4) = 20 字节。
    /// </summary>
    private const int MinFrameLength = RbfConstants.FrameFixedOverheadBytes + FrameStatusHelper.MaxStatusLength;

    /// <summary>
    /// 随机读取指定位置的帧。
    /// </summary>
    /// <param name="file">文件句柄（需具备 Read 权限）。</param>
    /// <param name="ptr">帧位置凭据。</param>
    /// <returns>读取结果（成功含帧，失败含错误码）。</returns>
    /// <remarks>使用 RandomAccess.Read 实现，无状态，并发安全。</remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, SizedPtr ptr) {
        // 1. 参数校验
        ulong offsetBytes = ptr.OffsetBytes;
        uint lengthBytes = ptr.LengthBytes;

        if (offsetBytes % RbfConstants.FrameAlignment != 0) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfArgumentError(
                    $"Offset ({offsetBytes}) must be 4-byte aligned.",
                    RecoveryHint: "Ensure ptr comes from a valid Append() return value."
                )
            );
        }

        if (lengthBytes % RbfConstants.FrameAlignment != 0) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfArgumentError(
                    $"Length ({lengthBytes}) must be 4-byte aligned.",
                    RecoveryHint: "Ensure ptr comes from a valid Append() return value."
                )
            );
        }

        if (lengthBytes < MinFrameLength) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfArgumentError(
                    $"Length ({lengthBytes}) is less than minimum frame length ({MinFrameLength}).",
                    RecoveryHint: "Minimum valid frame size is 20 bytes (empty payload, 4-byte status)."
                )
            );
        }

        // 检查 Offset 可表示性（ulong → long 转换安全）
        if (offsetBytes > (ulong)long.MaxValue) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfArgumentError(
                    "Offset exceeds representable range.",
                    RecoveryHint: "Offset must be <= long.MaxValue for RandomAccess.Read."
                )
            );
        }

        // 检查 Length 可表示性（uint → int 转换安全）
        if (lengthBytes > int.MaxValue) {
            return AteliaResult<RbfFrame>.Failure(
                new RbfArgumentError(
                    "Length exceeds representable range.",
                    RecoveryHint: "Length must be <= int.MaxValue for buffer allocation."
                )
            );
        }

        // 2. 分配 buffer
        byte[]? rentedArray = null;
        Span<byte> buffer = lengthBytes <= MaxStackAllocSize
            ? stackalloc byte[(int)lengthBytes]
            : (rentedArray = ArrayPool<byte>.Shared.Rent((int)lengthBytes)).AsSpan(0, (int)lengthBytes);

        try {
            // 3. 读取整个 FrameBytes
            int bytesRead = RandomAccess.Read(file, buffer, (long)offsetBytes);
            if (bytesRead < lengthBytes) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfArgumentError(
                        $"Short read: expected {lengthBytes} bytes but got {bytesRead}.",
                        RecoveryHint: "The ptr may point beyond end of file or file was truncated."
                    )
                );
            }

            // 4. Framing 校验
            int headLen = (int)lengthBytes; // 期望的 HeadLen 值

            // 4.1 验证 HeadLen 字段
            uint headLenFromFile = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..RbfConstants.HeadLenFieldLength]);
            if (headLenFromFile != headLen) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfFramingError(
                        $"HeadLen mismatch: file has {headLenFromFile}, expected {headLen}.",
                        RecoveryHint: "The frame may be corrupted or ptr.Length is incorrect."
                    )
                );
            }

            // 4.2 从 statusByte 推导 StatusLen
            // statusByte 位于 buffer[headLen - StatusByteFromTailOffset]（TailLen(4) + CRC(4) + 最后一个 status 字节(1) = 9）
            int statusByteOffset = headLen - RbfConstants.StatusByteFromTailOffset;
            byte statusByte = buffer[statusByteOffset];

            if (!FrameStatusHelper.TryDecodeStatusByte(statusByte, out bool isTombstone, out int statusLen)) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfFramingError(
                        $"Invalid status byte 0x{statusByte:X2}: reserved bits are non-zero.",
                        RecoveryHint: "The frame status region is corrupted."
                    )
                );
            }

            // 4.3 计算并验证 payloadLen
            int payloadLen = headLen - RbfConstants.FrameFixedOverheadBytes - statusLen;
            if (payloadLen < 0) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfFramingError(
                        $"Invalid frame structure: computed payloadLen={payloadLen} is negative.",
                        RecoveryHint: "Frame structure is corrupted."
                    )
                );
            }

            // 验证 StatusLen 与 PayloadLen 的对齐一致性
            int expectedStatusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);
            if (expectedStatusLen != statusLen) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfFramingError(
                        $"StatusLen inconsistency: encoded={statusLen}, computed from payloadLen={expectedStatusLen}.",
                        RecoveryHint: "The status length field does not match the payload size."
                    )
                );
            }

            // 4.4 验证 FrameStatus 全字节同值
            int statusStartOffset = RbfConstants.PayloadFieldOffset + payloadLen;
            ReadOnlySpan<byte> statusRegion = buffer.Slice(statusStartOffset, statusLen);
            if (!FrameStatusHelper.ValidateStatusBytesConsistent(statusRegion)) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfFramingError(
                        "Status bytes are not consistent (all bytes should be identical).",
                        RecoveryHint: "The status region is corrupted."
                    )
                );
            }

            // 4.5 验证 TailLen
            int tailLenOffset = headLen - RbfConstants.TailSuffixLength; // TailLen(4) + CRC(4) = 8
            uint tailLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(tailLenOffset, RbfConstants.TailLenFieldLength));
            if (tailLen != headLen) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfFramingError(
                        $"TailLen mismatch: TailLen={tailLen}, HeadLen={headLen}.",
                        RecoveryHint: "The frame boundaries are corrupted."
                    )
                );
            }

            // 5. CRC 校验
            // CRC 覆盖范围：Tag(4) + Payload(N) + Status(1-4) + TailLen(4)
            // 即 buffer[TagFieldOffset..(headLen-CrcFieldLength)]
            int crcOffset = headLen - RbfConstants.CrcFieldLength;
            ReadOnlySpan<byte> crcInput = buffer.Slice(RbfConstants.TagFieldOffset, headLen - RbfConstants.TailSuffixLength); // 从 Tag 到 TailLen（含）
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(crcOffset, RbfConstants.CrcFieldLength));
            uint computedCrc = Crc32CHelper.Compute(crcInput);

            if (expectedCrc != computedCrc) {
                return AteliaResult<RbfFrame>.Failure(
                    new RbfCrcMismatchError(
                        $"CRC mismatch: expected 0x{expectedCrc:X8}, computed 0x{computedCrc:X8}.",
                        RecoveryHint: "The frame data is corrupted."
                    )
                );
            }

            // 6. 构造 RbfFrame 并返回
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(RbfConstants.TagFieldOffset, RbfConstants.TagFieldLength));

            // 重要：RbfFrame 是 ref struct，其 Payload 是 ReadOnlySpan<byte>。
            // 如果使用了 ArrayPool，buffer 在返回后会被归还，Payload 引用将失效！
            // 解决方案：复制 payload 到新数组。
            byte[] payloadArray;
            if (payloadLen > 0) {
                payloadArray = new byte[payloadLen];
                buffer.Slice(RbfConstants.PayloadFieldOffset, payloadLen).CopyTo(payloadArray);
            }
            else {
                payloadArray = [];
            }

            var frame = new RbfFrame {
                Ptr = ptr,
                Tag = tag,
                Payload = payloadArray,
                IsTombstone = isTombstone
            };

            return AteliaResult<RbfFrame>.Success(frame);
        }
        finally {
            // 归还 ArrayPool buffer
            if (rentedArray is not null) {
                ArrayPool<byte>.Shared.Return(rentedArray);
            }
        }
    }
}
