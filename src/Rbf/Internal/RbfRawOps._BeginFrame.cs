using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RBF 原始操作集。</summary>
internal static partial class RbfRawOps {
    /// <summary>开始构建一个帧（Complex Path）。</summary>
    /// <remarks>
    /// <para><b>[Internal]</b>：仅限程序集内调用。</para>
    /// <para>返回的 Builder 内部持有 file 引用和 writeOffset。</para>
    /// </remarks>
    internal static RbfFrameBuilder _BeginFrame(SafeFileHandle file, long writeOffset) {
        throw new NotImplementedException();
    }
}
