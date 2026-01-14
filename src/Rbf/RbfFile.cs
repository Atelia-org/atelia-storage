using Atelia.Rbf.Internal;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>
/// RBF 文件静态工厂类。
/// </summary>
public static class RbfFile {
    /// <summary>
    /// 创建新的 RBF 文件（FailIfExists）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>RBF 文件对象。</returns>
    /// <remarks>
    /// 规范引用：@[F-FILE-STARTS-WITH-GENESIS-FENCE] - 新文件仅含 Genesis Fence。
    /// </remarks>
    public static IRbfFile CreateNew(string path) {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);

        try {
            // 写入 Genesis Fence
            RandomAccess.Write(handle, RbfConstants.Fence, 0);
            return new RbfFileImpl(handle, RbfConstants.GenesisLength);
        }
        catch {
            // 失败路径：确保句柄关闭
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 打开已有的 RBF 文件（验证 Genesis）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>RBF 文件对象。</returns>
    /// <exception cref="InvalidDataException">Genesis Fence 验证失败、文件过短或长度非 4B 对齐。</exception>
    /// <remarks>
    /// 规范引用：
    /// - @[F-FILE-STARTS-WITH-GENESIS-FENCE] - 文件 MUST 以 Genesis Fence 开头。
    /// - @[S-RBF-DECISION-4B-ALIGNMENT-ROOT] - 文件长度 MUST 4B 对齐。
    /// </remarks>
    public static IRbfFile OpenExisting(string path) {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        try {
            // 获取文件长度
            long fileLength = RandomAccess.GetLength(handle);

            // 边界条件：文件过短
            if (fileLength < RbfConstants.FenceLength) {
                throw new InvalidDataException("Invalid RBF file: file too short for Genesis Fence");
            }

            // 4B 对齐校验（根不变量）
            if (fileLength % 4 != 0) {
                throw new InvalidDataException("Invalid RBF file: length is not 4-byte aligned");
            }

            // 读取前 4 字节并验证
            Span<byte> buffer = stackalloc byte[RbfConstants.FenceLength];
            int bytesRead = RandomAccess.Read(handle, buffer, 0);

            if (bytesRead < RbfConstants.FenceLength ||
                !buffer.SequenceEqual(RbfConstants.Fence)) {
                throw new InvalidDataException("Invalid RBF file: Genesis Fence mismatch");
            }

            return new RbfFileImpl(handle, fileLength);
        }
        catch {
            // 失败路径：确保句柄关闭
            handle.Dispose();
            throw;
        }
    }
}
