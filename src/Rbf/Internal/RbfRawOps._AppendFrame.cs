using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;
using System.Diagnostics;

namespace Atelia.Rbf.Internal;

partial class RbfRawOps {
    /// <summary>
    /// 把CRC计算和磁盘写入操作内聚在一起。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 约定：CRC 覆盖区间为 Tag(4) + Payload(N) + Status(1-4) + TailLen(4)。
    /// </para>
    /// <para>
    /// 当 <c>crcInitialize</c> 为 true 时，会跳过 HeadLen(4) 并初始化 CRC。
    /// 当 <c>crcFinalize</c> 为 true 时，会在写盘前回填 CRC 并推进 <paramref name="fileOffset"/>。
    /// </para>
    /// </remarks>
    private static void WriteWithCrc(SafeFileHandle handle, Span<byte> buffer, ref long fileOffset, ref uint crc, bool crcInitialize, bool crcFinalize){
        const int crcToTail = RbfConstants.CrcFieldLength+RbfConstants.FenceLength; // 跳过尾部的CRC和Fence
        var crcBuffer = buffer;
        if (crcInitialize){
            crc = Crc32CHelper.Init();
            crcBuffer = crcBuffer[RbfConstants.HeadLenFieldLength..]; // 跳过HeadLen
        }
        if (crcFinalize){
            crcBuffer = crcBuffer[..^crcToTail]; // 停在CrcField前
        }
        crc = Crc32CHelper.Update(crc,crcBuffer);
        if (crcFinalize){
            crc = Crc32CHelper.Finalize(crc);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[^crcToTail..], crc); // 填CRC
        }
        RandomAccess.Write(handle, buffer, fileOffset);
        fileOffset += buffer.Length;
    }

    /// <summary>
    /// 只读缓冲区版本（用于 payload 零拷贝写入）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 约定：该重载仅用于“中段 payload”写入；不会初始化或 finalize CRC，也不会回填 CRC。
    /// </para>
    /// </remarks>
    private static void WriteWithCrc(SafeFileHandle handle, ReadOnlySpan<byte> buffer, ref long fileOffset, ref uint crc){
        crc = Crc32CHelper.Update(crc, buffer);
        RandomAccess.Write(handle, buffer, fileOffset);
        fileOffset += buffer.Length;
    }

    // ========== 统一自适应写入（实验性）==========

    /// 统一缓冲区大小（4KB，复用于 header 和 trailer）。
    private const int UnifiedBufferShift = 12;
    private const int UnifiedBufferSize = 1<<UnifiedBufferShift;

    private static void FillTrailer(Span<byte> buffer, ref int bufferUsed, int statusLen, int frameLen){
        // Status
        FrameStatusHelper.FillStatus(buffer.Slice(bufferUsed, statusLen), isTombstone: false, statusLen);
        bufferUsed += statusLen;

        // TailLen
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[bufferUsed..], (uint)frameLen);
        bufferUsed += RbfConstants.TailLenFieldLength;

        // CRC hole（CRC 会由 WriteWithCrc 在最终写入前回填）
        bufferUsed += RbfConstants.CrcFieldLength;

        // Fence
        RbfConstants.Fence.CopyTo(buffer[bufferUsed..]);
        bufferUsed += RbfConstants.FenceLength;
    }

    /// <summary>
    /// 统一路径：自适应 1-3 次写入。
    /// </summary>
    /// <remarks>
    /// <para>使用固定 4KB buffer，复用于 header 填充和 trailer 填充。</para>
    /// <para>写入策略：</para>
    /// <list type="bullet">
    ///   <item>小 payload（整帧 + Fence ≤ 4075 B）：1 次写入</item>
    ///   <item>中 payload（≤ 8171 B）：2 次写入</item>
    ///   <item>大 payload（> 8171 B）：3 次写入</item>
    /// </list>
    /// <para>
    /// 边界计算：
    /// - FrameMinOverheadBytes = 4+4+1+4+4+4 = 21
    /// - 1 次写入条件：PayloadLen ≤ BufferSize - FrameMinOverheadBytes = 4075
    /// - 2 次写入条件：PayloadLen ≤ 2*BufferSize - FrameMinOverheadBytes = 8171
    /// - 头部容量 = BufferSize - HeaderLength = 4096 - 8 = 4088 bytes payload
    /// - 尾部容量 = BufferSize - MinTrailerLength = 4096 - 13 = 4083 bytes payload
    /// </para>
    /// <para>
    /// <b>注意</b>：多次 RandomAccess.Write 不保证原子性。
    /// 若应用依赖 crash-consistency，需在更上层实现屏障/fsync 语义。
    /// </para>
    /// </remarks>
    internal static SizedPtr _AppendFrame(
        SafeFileHandle file,
        long writeOffset,
        uint tag,
        ReadOnlySpan<byte> payload,
        out long nextTailOffset
    ) {
        int frameLen = ComputeFrameLen(payload.Length, out int statusLen);
        int totalWriteLen = checked(frameLen + RbfConstants.FenceLength);

        // 固定 4KB buffer，复用于 header 和 trailer
        Span<byte> buffer = stackalloc byte[UnifiedBufferSize];
        // 头部能容纳的 payload 长度
        const int HeadPayloadCap = UnifiedBufferSize - RbfConstants.PayloadFieldOffset;

        // 必要，文件游标
        nextTailOffset = writeOffset;
        // 必要，缓存游标。分别统计用于诊断
        int headWriteLen, tailWriteLen;

        // 诊断用，最终CRC
        uint crc = 0;
        // 诊断用，payload分配
        int headPayloadLen = Math.Min(payload.Length, HeadPayloadCap);
# if DEBUG
        int middlePayloadLen, tailPayloadLen;
# endif

        // 头部Header
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)frameLen); headWriteLen = RbfConstants.HeadLenFieldLength;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RbfConstants.HeadLenFieldLength..], tag); headWriteLen += RbfConstants.TagFieldLength;
        // 头部Payload
        payload[..headPayloadLen].CopyTo(buffer[RbfConstants.PayloadFieldOffset..]); headWriteLen += headPayloadLen;
        // 剩余 payload
        int remainingPayloadLen = payload.Length - headPayloadLen;

        if (totalWriteLen <= UnifiedBufferSize){
            Debug.Assert(remainingPayloadLen == 0);
            // 1次就能写完的情况
            // Header和Trailer直接写在一个buffer里
            FillTrailer(buffer, ref headWriteLen, statusLen, frameLen);
            // 是Header所以crcInitialize，又是Trailer所以crcFinalize
            WriteWithCrc(file, buffer[..headWriteLen], ref nextTailOffset, ref crc, crcInitialize: true, crcFinalize: true);
            
            middlePayloadLen = 0; tailPayloadLen = 0; // 诊断信息
            tailWriteLen = 0; // 初始化尾游标
        }else{
            Debug.Assert(0 <= remainingPayloadLen);
            // 只是Header所以只crcInitialize
            WriteWithCrc(file, buffer[..headWriteLen], ref nextTailOffset, ref crc, crcInitialize: true, crcFinalize: false);
            tailWriteLen = 0;// 初始化尾游标
            
            int trailerLen = statusLen + RbfConstants.TailLenFieldLength + RbfConstants.CrcFieldLength + RbfConstants.FenceLength;
            if(UnifiedBufferSize*2 < totalWriteLen){
                Debug.Assert(UnifiedBufferSize < remainingPayloadLen + trailerLen);
                // 2次写不完，无论在中段和尾段间如何分配剩余payload都不会减少写入次数
                // 为避免copy payload到buffer，把剩下的payload全都写到中段
                middlePayloadLen = remainingPayloadLen; tailPayloadLen = 0; // 诊断信息
                WriteWithCrc(file, payload.Slice(headPayloadLen, remainingPayloadLen), ref nextTailOffset, ref crc);  
            }else{
                Debug.Assert(remainingPayloadLen + trailerLen <= UnifiedBufferSize);
                middlePayloadLen = 0; tailPayloadLen = remainingPayloadLen; // 诊断信息
                if (0 < remainingPayloadLen){
                    // 剩余payload可以用尾buffer写完，全都填到尾buffer
                    middlePayloadLen = 0 ; 
                    payload.Slice(headPayloadLen, remainingPayloadLen).CopyTo(buffer);
                    tailWriteLen += remainingPayloadLen;
                }
            }

            // 填尾Buffer并落盘
            FillTrailer(buffer, ref tailWriteLen, statusLen, frameLen);
            WriteWithCrc(file, buffer[..tailWriteLen], ref nextTailOffset, ref crc, crcInitialize: false, crcFinalize: true);
        }

        Debug.Assert(headWriteLen + middlePayloadLen + tailWriteLen == totalWriteLen);
        Debug.Assert(headPayloadLen + middlePayloadLen + tailPayloadLen == payload.Length);
        return SizedPtr.Create((ulong)writeOffset, (uint)frameLen);
    }
}
