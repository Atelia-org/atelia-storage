using Atelia.Data;
using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 原始操作集。</summary>
partial class RbfReadImpl {
    /// <summary>读取指定位置之前的帧元信息（只读 TrailerCodeword，不校验 PayloadCrc）。</summary>
    /// <param name="file">RBF 文件句柄。</param>
    /// <param name="fenceEndOffset">帧尾 Fence 的 EndOffsetExclusive。</param>
    /// <returns>成功时返回 RbfFrameInfo（已绑定 file 句柄），失败时返回错误。</returns>
    /// <remarks>
    /// 唯一入口：这是创建 RbfFrameInfo 的内部验证路径之一，完成所有结构性验证。
    /// 规范引用：@[A-READ-TRAILER-BEFORE]
    /// </remarks>
    internal static AteliaResult<RbfFrameInfo> ReadTrailerBefore(
        RandomAccessReader reader,
        long fenceEndOffset
    ) {
        reader.EnsureUsable();
        int trailerAndFenceSize = TrailerCodewordHelper.Size + RbfLayout.GetTailKeySize(reader.Profile) + RbfLayout.FenceSize;

        // 1. 边界检查：fenceEndOffset 必须 >= MinFirstFrameFenceEnd
        int minimumFenceEnd = RbfLayout.HeaderOnlyLength + RbfLayout.GetMinFrameLength(reader.Profile) + RbfLayout.FenceSize;
        if (fenceEndOffset < minimumFenceEnd || (fenceEndOffset & RbfLayout.AlignmentMask) != 0) {
            return new RbfFramingError(
                $"No frame before offset {fenceEndOffset}: minimum required is {minimumFenceEnd}, with 4B alignment.",
                RecoveryHint: "The offset may be at or before the first frame."
            );
        }

        // 2. 一次读取 Trailer、可选 raw Key 和 Fence（RBF1 20B / RBF3 24B）。
        Span<byte> buffer = stackalloc byte[trailerAndFenceSize];
        long readOffset = fenceEndOffset - trailerAndFenceSize;
        int bytesRead = reader.Read(buffer, readOffset);

        if (bytesRead < trailerAndFenceSize) {
            return new RbfFramingError(
                $"Short read: expected {trailerAndFenceSize} bytes, got {bytesRead}.",
                RecoveryHint: "The file may be truncated."
            );
        }

        // 3. 验证 Fence（末尾 4 字节）
        if (!buffer[^RbfLayout.FenceSize..].SequenceEqual(RbfLayout.GetFence(reader.Profile))) {
            return new RbfFramingError(
                "Expected profile Fence not found.",
                RecoveryHint: "The frame boundary marker is missing or corrupted."
            );
        }

        // 4. 验证并解析 TrailerCodeword（前 16 字节，CRC + reserved bits）
        var trailerResult = ParseTailBlock(reader.Profile, buffer[..^RbfLayout.FenceSize], out uint key);
        if (!trailerResult.IsSuccess) { return trailerResult.Error!; }

        var trailer = trailerResult.Value;

        // TailLen is a validated byte length, normalized only after the original CRC.
        // 8. 计算并验证 frameStart
        // frameStart = fenceEndOffset - FenceSize - TailLen
        long frameStart = fenceEndOffset - RbfLayout.FenceSize - trailer.TailLen;
        if (frameStart < RbfLayout.HeaderOnlyLength || frameStart > SizedPtr.MaxOffset || (frameStart & RbfLayout.AlignmentMask) != 0) {
            return new RbfFramingError(
                $"Frame extends before HeaderFence: frameStart={frameStart}, HeaderOnlyLength={RbfLayout.HeaderOnlyLength}.",
                RecoveryHint: "The TailLen value is too large for this position."
            );
        }

        // 9. 计算 PayloadLength
        // PayloadLength = TailLen - FixedOverhead - TailMetaLen - PaddingLen
        // RBF3 fixed overhead additionally includes raw TailKey4.
        int payloadLen = (int)trailer.TailLen - RbfLayout.GetFixedOverhead(reader.Profile) - trailer.TailMetaLen - trailer.PaddingLen;

        // 10. 构造 RbfFrameInfo（绑定 file 句柄）
        var ticket = SizedPtr.Create(frameStart, (int)trailer.TailLen);
        return new RbfFrameInfo(
            reader: reader,
            ticket: ticket,
            tag: trailer.FrameTag,
            payloadLength: payloadLen,
            tailMetaLength: trailer.TailMetaLen,
            isTombstone: trailer.IsTombstone,
            escapeKey: key
        );
    }
}
