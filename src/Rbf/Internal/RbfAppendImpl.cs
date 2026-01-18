using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using System.Diagnostics;

namespace Atelia.Rbf.Internal;

internal static class RbfAppendImpl {
    /// <summary>
    /// 把CRC计算和磁盘写入操作内聚在一起。
    /// @[F-CRC32C-COVERAGE](atelia/docs/Rbf/rbf-format.md)：CRC 覆盖区间为 Tag(4) + Payload(N) + Status(1-4) + TailLen(4)。
    /// 当 <c>crcInitialize</c> 为 true 时，会跳过 HeadLen(4) 并初始化 CRC。
    /// 当 <c>crcFinalize</c> 为 true 时，会在写盘前回填 CRC 并推进 <paramref name="fileOffset"/>。
    /// </summary>
    private static void WriteWithCrc(SafeFileHandle handle, Span<byte> buffer, ref long fileOffset, ref uint crc, bool crcInitialize, bool crcFinalize) {
        const int bufferAfterCrcCoverage = FrameLayout.LengthAfterCrcCoverage + RbfLayout.FenceSize; // 跳过尾部的CRC和Fence
        var crcBuffer = buffer;
        if (crcInitialize) {
            crc = Crc32CHelper.Init();
            crcBuffer = crcBuffer[FrameLayout.CrcCoverageStart..]; // 跳过HeadLen
        }
        if (crcFinalize) {
            crcBuffer = crcBuffer[..^bufferAfterCrcCoverage]; // 停在CrcField前
        }
        crc = Crc32CHelper.Update(crc, crcBuffer);
        if (crcFinalize) {
            crc = Crc32CHelper.Finalize(crc);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[^bufferAfterCrcCoverage..], crc); // 填CRC
        }
        RandomAccess.Write(handle, buffer, fileOffset);
        fileOffset += buffer.Length;
    }

    // 只读缓冲区版本（用于 payload 零拷贝写入）。 约定：该重载仅用于“中段 payload”写入；不会初始化或 finalize CRC，也不会回填 CRC。
    private static void WriteWithCrc(SafeFileHandle handle, ReadOnlySpan<byte> buffer, ref long fileOffset, ref uint crc) {
        crc = Crc32CHelper.Update(crc, buffer);
        RandomAccess.Write(handle, buffer, fileOffset);
        fileOffset += buffer.Length;
    }

    /// 统一缓冲区大小（4KB，复用于 header 和 trailer）。
    private const int MaxBufferSizeShift = 12;
    private const int MaxBufferSize = 1 << MaxBufferSizeShift;

    public static int[] GetPayloadEdgeCase() {
        // 由UnifiedBufferSize是4的倍数且Rbf按4B对齐保证
        const int OneMax = MaxBufferSize - FrameLayout.MinOverheadLen;
        const int TwoMin = OneMax + 1;
        const int TwoMax = 2 * MaxBufferSize - FrameLayout.MinOverheadLen;
        const int ThreeMin = TwoMax + 1;
        return [OneMax, TwoMin, TwoMax, ThreeMin];
    }

    private static void FillTrailerAndFence(Span<byte> buffer, ref int bufferUsed, in FrameLayout layout) {
        layout.FillTrailer(buffer, ref bufferUsed);
        // Fence
        RbfLayout.Fence.CopyTo(buffer[bufferUsed..]);
        bufferUsed += RbfLayout.FenceSize;
    }

    /// <summary>
    /// 自适应 1-3 次写入。header填充和trailer填充复用同一个buffer。
    /// 注意：多次 RandomAccess.Write 不保证原子性。若应用依赖 crash-consistency，需在更上层实现屏障/fsync 语义。
    /// </summary>
    public static SizedPtr Append(
        SafeFileHandle file,
        long writeOffset,
        uint tag,
        ReadOnlySpan<byte> payload,
        out long nextTailOffset
    ) {
        FrameLayout layout = new FrameLayout(payload.Length);
        int totalWriteLen = checked(layout.FrameLength + RbfLayout.FenceSize);
        int actualBufferSize = Math.Min(totalWriteLen, MaxBufferSize);

        // write buffer，复用于 header 和 trailer
        Span<byte> buffer = stackalloc byte[actualBufferSize];
        // 头部能容纳的 payload 长度
        const int HeadPayloadCap = MaxBufferSize - FrameLayout.PayloadOffset;

        // 必要，文件游标
        nextTailOffset = writeOffset;
        // 必要，缓存游标。分别统计用于诊断
        int headWriteLen, tailWriteLen;

        // 诊断用，最终CRC
        uint crc = 0;
        // 诊断用，payload分配
        int headPayloadLen = Math.Min(payload.Length, HeadPayloadCap);
        int middlePayloadLen, tailPayloadLen;

        // 头部Header
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)layout.FrameLength);
        headWriteLen = FrameLayout.HeadLenSize;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[FrameLayout.HeadLenSize..], tag);
        headWriteLen += FrameLayout.TagSize;

        // 头部Payload
        payload[..headPayloadLen].CopyTo(buffer[FrameLayout.PayloadOffset..]);
        headWriteLen += headPayloadLen;

        // 剩余 payload
        int remainingPayloadLen = payload.Length - headPayloadLen;

        if (totalWriteLen <= MaxBufferSize) {
            Debug.Assert(remainingPayloadLen == 0);
            // 1次就能写完的情况
            // Header和Trailer直接写在一个buffer里
            FillTrailerAndFence(buffer, ref headWriteLen, in layout);
            // 是Header所以crcInitialize，又是Trailer所以crcFinalize
            WriteWithCrc(file, buffer[..headWriteLen], ref nextTailOffset, ref crc, crcInitialize: true, crcFinalize: true);

            middlePayloadLen = 0;
            tailPayloadLen = 0; // 诊断信息
            tailWriteLen = 0; // 初始化尾游标
        }
        else {
            Debug.Assert(0 <= remainingPayloadLen);
            // 只是Header所以只crcInitialize
            WriteWithCrc(file, buffer[..headWriteLen], ref nextTailOffset, ref crc, crcInitialize: true, crcFinalize: false);
            tailWriteLen = 0;// 初始化尾游标

            int trailerAndFenceLen = layout.TrailerLength + RbfLayout.FenceSize;
            if (MaxBufferSize * 2 < totalWriteLen) {
                Debug.Assert(MaxBufferSize < remainingPayloadLen + trailerAndFenceLen);
                // 2次写不完，无论在中段和尾段间如何分配剩余payload都不会减少写入次数
                // 为避免copy payload到buffer，把剩下的payload全都写到中段
                middlePayloadLen = remainingPayloadLen;
                tailPayloadLen = 0; // 诊断信息
                WriteWithCrc(file, payload.Slice(headPayloadLen, remainingPayloadLen), ref nextTailOffset, ref crc);
            }
            else {
                Debug.Assert(remainingPayloadLen + trailerAndFenceLen <= MaxBufferSize);
                middlePayloadLen = 0;
                tailPayloadLen = remainingPayloadLen; // 诊断信息
                if (0 < remainingPayloadLen) {
                    // 剩余payload可以用尾buffer写完，全都填到尾buffer
                    middlePayloadLen = 0;
                    payload.Slice(headPayloadLen, remainingPayloadLen).CopyTo(buffer);
                    tailWriteLen += remainingPayloadLen;
                }
            }

            // 填尾Buffer并落盘
            FillTrailerAndFence(buffer, ref tailWriteLen, in layout);
            WriteWithCrc(file, buffer[..tailWriteLen], ref nextTailOffset, ref crc, crcInitialize: false, crcFinalize: true);
        }

        Debug.Assert(headWriteLen + middlePayloadLen + tailWriteLen == totalWriteLen);
        Debug.Assert(headPayloadLen + middlePayloadLen + tailPayloadLen == payload.Length);
        return SizedPtr.Create(writeOffset, layout.FrameLength);
    }
}
