using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 原始操作集。
/// </summary>
internal static class RbfRawOps {
    // 读路径 (Read Path)

    /// <summary>
    /// 随机读取指定位置的帧。
    /// </summary>
    /// <param name="file">文件句柄（需具备 Read 权限）。</param>
    /// <param name="ptr">帧位置凭据。</param>
    /// <returns>读取结果（成功含帧，失败含错误码）。</returns>
    /// <remarks>使用 RandomAccess.Read 实现，无状态，并发安全。</remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, SizedPtr ptr) {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 创建逆向扫描序列。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="scanOrigin">文件逻辑长度（扫描起点）。</param>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false。</param>
    /// <returns>逆向扫描序列结构。</returns>
    /// <remarks>
    /// <para>RawOps 层直接实现过滤逻辑，与 Facade 层 @[S-RBF-SCANREVERSE-TOMBSTONE-FILTER] 保持一致。</para>
    /// </remarks>
    public static RbfReverseSequence ScanReverse(SafeFileHandle file, long scanOrigin, bool showTombstone = false) {
        throw new NotImplementedException();
    }

    // 写路径 (Write Path)

    /// <summary>
    /// 开始构建一个帧（Complex Path）。
    /// </summary>
    /// <remarks>
    /// <para><b>[Internal]</b>：仅限程序集内调用。</para>
    /// <para>返回的 Builder 内部持有 file 引用和 writeOffset。</para>
    /// </remarks>
    internal static RbfFrameBuilder _BeginFrame(SafeFileHandle file, long writeOffset, uint tag) {
        throw new NotImplementedException();
    }
}
