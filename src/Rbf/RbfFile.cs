using Atelia.Data;
using Atelia.Rbf.Internal;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>RBF 文件静态工厂类。</summary>
public static class RbfFile {
    /// <summary>
    /// 单帧中 Payload 与 TailMeta 的最大合计长度（不含 HeadLen、Padding、PayloadCrc、TrailerCodeword 与尾部 Fence）。
    /// </summary>
    /// <remarks>
    /// 该上限由 <see cref="SizedPtr.MaxLength"/> 减去 RBF 帧固定开销推导而来，
    /// 是 <see cref="IRbfFile.Append"/> 与 <see cref="RbfFrameBuilder.EndAppend"/> 的公开容量契约。
    /// </remarks>
    public const int MaxPayloadAndMetaLength = FrameLayout.MaxPayloadAndMetaLength;
    public const int MaxTailMetaLength = FrameLayout.MaxTailMetaLength;

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
            RandomAccess.Write(handle, RbfLayout.Fence, 0);
            return new RbfFileImpl(handle, RbfLayout.HeaderOnlyLength, cacheMode);
        }
        catch {
            // 失败路径：确保句柄关闭
            handle.Dispose();
            throw;
        }
    }

    /// <summary>打开已有的 RBF 文件（验证 HeaderFence）。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cacheMode">读缓存策略。默认 <see cref="RbfCacheMode.Slots16"/>（64KB）。</param>
    /// <returns>RBF 文件对象。</returns>
    /// <exception cref="InvalidDataException">HeaderFence 验证失败、文件过短或长度非 4B 对齐。</exception>
    /// <remarks>
    /// 规范引用：
    /// - @[F-FILE-STARTS-WITH-HEADER-FENCE] - 文件 MUST 以 HeaderFence 开头。
    /// - @[S-RBF-DECISION-4B-ALIGNMENT-ROOT] - 文件长度 MUST 4B 对齐。
    /// </remarks>
    public static IRbfFile OpenExisting(string path, RbfCacheMode cacheMode = RbfCacheMode.Slots16) {
        return OpenExistingCore(path, FileAccess.ReadWrite, FileShare.None, cacheMode);
    }

    /// <summary>
    /// 以共享只读方式打开已有的 RBF 文件。
    /// 用于读取已冻结的历史 segment，允许后续在同一卷内移动到 archive bucket。
    /// </summary>
    public static IRbfFile OpenReadOnlyExisting(string path, RbfCacheMode cacheMode = RbfCacheMode.Slots16) {
        return OpenExistingCore(path, FileAccess.Read, FileShare.Read | FileShare.Delete, cacheMode);
    }

    private static IRbfFile OpenExistingCore(
        string path,
        FileAccess access,
        FileShare share,
        RbfCacheMode cacheMode
    ) {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            access,
            share
        );

        try {
            // 获取文件长度
            long fileLength = RandomAccess.GetLength(handle);

            // 边界条件：文件过短
            if (fileLength < RbfLayout.FenceSize) { throw new InvalidDataException("Invalid RBF file: file too short for HeaderFence"); }

            // 4B 对齐校验（根不变量）
            if (fileLength % RbfLayout.Alignment != 0) { throw new InvalidDataException("Invalid RBF file: length is not 4-byte aligned"); }

            // 读取前 4 字节并验证
            Span<byte> buffer = stackalloc byte[RbfLayout.FenceSize];
            int bytesRead = RandomAccess.Read(handle, buffer, 0);

            if (bytesRead < RbfLayout.FenceSize || !buffer.SequenceEqual(RbfLayout.Fence)) { throw new InvalidDataException("Invalid RBF file: HeaderFence mismatch"); }

            return new RbfFileImpl(handle, fileLength, cacheMode);
        }
        catch {
            // 失败路径：确保句柄关闭
            handle.Dispose();
            throw;
        }
    }
}
