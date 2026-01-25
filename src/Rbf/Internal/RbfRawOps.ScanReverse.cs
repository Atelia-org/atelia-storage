using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 原始操作集。</summary>
internal static partial class RbfRawOps {
    /// <summary>创建逆向扫描序列。</summary>
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
}
