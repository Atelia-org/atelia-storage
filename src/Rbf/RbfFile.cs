using Atelia.Data;
using Atelia.Rbf.Internal;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>RBF 文件工厂与新写入尺寸计算。</summary>
public static class RbfFile {
    /// <summary>
    /// 单个新 RBF3 帧中 Payload 与 TailMeta 的最大合计长度（不含 HeadLen、Padding、PayloadCrc、TrailerCodeword、TailKey 与尾部 Fence）。
    /// </summary>
    /// <remarks>
    /// 该上限由 <see cref="SizedPtr.MaxLength"/> 减去 RBF3 帧固定开销推导而来，
    /// 是 <see cref="IRbfFile.Append"/> 与 <see cref="RbfFrameBuilder.EndAppend(uint, int)"/> 的公开容量契约。
    /// </remarks>
    public const int MaxPayloadAndMetaLength = FrameLayout.MaxPayloadAndMetaLength - sizeof(uint);
    public const int MaxTailMetaLength = FrameLayout.MaxTailMetaLength;

    /// <summary>计算新 RBF3 帧的精确物理尺寸，不进行 I/O 或预留文件位置。</summary>
    /// <param name="payloadLength">最终 stored payload 字节长度。</param>
    /// <param name="tailMetaLength">最终 TailMeta 字节长度。</param>
    /// <returns>
    /// 成功时返回不含尾 Fence 的帧长度与包含尾 Fence 的追加占用；
    /// 负长度、TailMeta 超限或合计超限时返回错误码 <c>Rbf.ArgumentError</c>。
    /// </returns>
    /// <remarks>只计算新 writer 使用的 RBF3 布局，不用于重算历史 RBF1 帧。</remarks>
    public static AteliaResult<RbfWriteSize> MeasureWriteSize(int payloadLength, int tailMetaLength = 0) {
        var layoutResult = FrameLayout.TryCreate(RbfProfile.Rbf3, payloadLength, tailMetaLength);
        if (layoutResult.IsFailure) { return layoutResult.Error!; }
        FrameLayout layout = layoutResult.Value;
        return new RbfWriteSize(layout.FrameLength, layout.FrameLength + RbfLayout.FenceSize);
    }

    /// <summary>在固定 TailMeta 长度下，计算追加预算可容纳的最大合法 payload 长度。</summary>
    /// <param name="byteBudget">本次追加的字节预算，包含尾 Fence，不含已有 Header 或前缀。</param>
    /// <param name="tailMetaLength">最终 TailMeta 字节长度。</param>
    /// <param name="payloadLength">成功时为最大 payload 长度；预算不足时为 0。</param>
    /// <returns>能容纳 payload 为 0 的帧时为 true，否则为 false；成功结果允许为 0。</returns>
    /// <exception cref="ArgumentOutOfRangeException">预算为负，或 TailMeta 长度不在合法范围内。</exception>
    /// <remarks>
    /// 只计算新 RBF3 写入，并受单帧容量上限约束。
    /// 预算不是 ticket 长度；不能从任意 ticket 的长度推断真实 payload 长度。
    /// </remarks>
    public static bool TryGetMaxPayloadLengthForAppendBudget(long byteBudget, int tailMetaLength, out int payloadLength) {
        return FrameLayout.TryGetMaxPayloadLengthForAppendBudget(RbfProfile.Rbf3, byteBudget, tailMetaLength, out payloadLength);
    }

    /// <summary>创建新的 RBF 文件（FailIfExists）。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cacheMode">读缓存策略。默认 <see cref="RbfCacheMode.Slots16"/>（64KB）。</param>
    /// <returns>RBF 文件对象。</returns>
    /// <remarks>
    /// 规范引用：@[F-FILE-STARTS-WITH-HEADER-FENCE] - 新文件仅含 HeaderFence。
    /// </remarks>
    public static IRbfFile CreateNew(string path, RbfCacheMode cacheMode = RbfCacheMode.Slots16) {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None
        );

        try {
            // 写入 HeaderFence
            RbfWriteInstrumentation.RegisterPath(handle, path);
            RbfWriteInstrumentation.Write(handle, RbfLayout.GetFence(RbfProfile.Rbf3), 0);
            return new RbfFileImpl(handle, RbfLayout.HeaderOnlyLength, cacheMode, profile: RbfProfile.Rbf3);
        }
        catch {
            // 失败路径：确保句柄关闭
            handle.Dispose();
            throw;
        }
    }

    /// <summary>独占打开已有 RBF 文件，默认修复唯一残缺尾帧。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cacheMode">读缓存策略。默认 <see cref="RbfCacheMode.Slots16"/>（64KB）。</param>
    /// <returns>RBF 文件对象。</returns>
    /// <param name="recovery">成功打开时报告本次物理尾帧恢复动作，不代表业务发布。</param>
    /// <exception cref="InvalidDataException">Header、主序列结构或已知 CRC 损坏；不修改损坏文件。</exception>
    public static IRbfFile OpenExisting(string path, out RbfTailRecoveryReport recovery, RbfCacheMode cacheMode = RbfCacheMode.Slots16) {
        return OpenExistingCore(path, FileAccess.ReadWrite, FileShare.None, cacheMode, out recovery);
    }

    /// <summary>
    /// 以共享只读方式打开已有的 RBF 文件。
    /// 用于读取已冻结的历史 segment，允许后续在同一卷内移动到 archive bucket。
    /// </summary>
    public static IRbfFile OpenReadOnlyExisting(string path, RbfCacheMode cacheMode = RbfCacheMode.Slots16) {
        return OpenExistingCore(path, FileAccess.Read, FileShare.Read | FileShare.Delete, cacheMode, out _);
    }

    /// <summary>Internal bound view only; caller supplies stable ownership and continuous physical prefix evidence.</summary>
    internal static IRbfFile OpenReadOnlyCandidate(string path, long eof, RbfCacheMode cacheMode = RbfCacheMode.Off,
        Action<int>? beforeRead = null, CancellationToken token = default) {
        if (eof < RbfLayout.HeaderOnlyLength || (eof & RbfLayout.AlignmentMask) != 0) {
            throw new ArgumentOutOfRangeException(nameof(eof), "Candidate EOF must include HeaderFence and be 4-byte aligned.");
        }
        token.ThrowIfCancellationRequested();
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try {
            RbfWriteInstrumentation.RegisterPath(handle, path);
            if (eof > RandomAccess.GetLength(handle)) { throw new ArgumentOutOfRangeException(nameof(eof), "Candidate EOF exceeds actual file length."); }
            Span<byte> fence = stackalloc byte[RbfLayout.FenceSize];
            using (var headerReader = new ReadCache.RandomAccessReader(handle, eof, beforeRead, token)) {
                if (headerReader.Read(fence, 0) != fence.Length) { throw new InvalidDataException("Incomplete candidate HeaderFence."); }
            }
            RbfProfile profile;
            if (fence.SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf1))) { profile = RbfProfile.Rbf1; }
            else if (fence.SequenceEqual(RbfLayout.GetFence(RbfProfile.Rbf3))) { profile = RbfProfile.Rbf3; }
            else { throw new InvalidDataException("Unknown candidate HeaderFence."); }
            using (var reader = new ReadCache.RandomAccessReader(handle, eof, beforeRead, token, profile)) {
                if (eof != RbfLayout.HeaderOnlyLength) {
                    var tail = RbfReadImpl.ReadTrailerBefore(reader, eof);
                    if (tail.IsFailure) { throw new InvalidDataException(tail.Error!.ToString()); }
                }
            }
            return new RbfFileImpl(handle, eof, cacheMode, readOnlyCandidate: true, beforeRead: beforeRead, cancellationToken: token, profile: profile);
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    private static IRbfFile OpenExistingCore(
        string path,
        FileAccess access,
        FileShare share,
        RbfCacheMode cacheMode,
        out RbfTailRecoveryReport recovery
    ) {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            access,
            share
        );

        try {
            RbfWriteInstrumentation.RegisterPath(handle, path);
            var report = RbfTailRecovery.Open(handle, writable: access == FileAccess.ReadWrite, out var profile);
            var file = new RbfFileImpl(handle, report.FinalLength, cacheMode, readOnly: access == FileAccess.Read, profile: profile);
            recovery = report;
            return file;
        }
        catch {
            // 失败路径：确保句柄关闭
            handle.Dispose();
            throw;
        }
    }
}
