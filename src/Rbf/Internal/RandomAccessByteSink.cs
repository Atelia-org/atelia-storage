using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>
/// RandomAccess → IByteSink 适配器
/// </summary>
/// <remarks>
/// <para><b>职责边界</b>：仅做 Push → RandomAccess.Write 转发 + offset 记账。</para>
/// <para>
/// <b>设计简化</b>：由于 <see cref="IByteSink"/> 是推式接口（调用者持有数据），
/// 无需持有 buffer、无需 ArrayPool 管理、无需三步舞（GetSpan/GetMemory/Advance）。
/// </para>
/// <para>
/// <b>并发</b>：非线程安全，依赖 <c>[S-RBF-BUILDER-SINGLE-OPEN]</c> 契约
/// （同一时刻只有一个活跃 Builder）。
/// </para>
/// </remarks>
internal sealed class RandomAccessByteSink : IByteSink {
    private readonly SafeFileHandle _file;
    private long _writeOffset;

    /// <summary>
    /// 创建 RandomAccess 写入适配器
    /// </summary>
    /// <param name="file">文件句柄（需具备 Write 权限）</param>
    /// <param name="startOffset">起始写入位置（byte offset）</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> 为 null</exception>
    public RandomAccessByteSink(SafeFileHandle file, long startOffset) {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _writeOffset = startOffset;
    }

    /// <summary>当前写入位置（byte offset）</summary>
    /// <remarks>
    /// Builder 层可用于计算已写入字节数（CurrentOffset - StartOffset）
    /// 以及最终 HeadLen 回填。
    /// </remarks>
    public long CurrentOffset => _writeOffset;

    /// <summary>推送数据到文件</summary>
    /// <remarks>
    /// 调用 <see cref="RandomAccess.Write(SafeFileHandle, ReadOnlySpan{byte}, long)"/>
    /// 写入数据并推进 offset。
    /// 
    /// <para><b>错误处理</b>：I/O 异常直接抛出（符合 Infra Fault 策略）。</para>
    /// </remarks>
    public void Push(ReadOnlySpan<byte> data) {
        if (data.IsEmpty) { return; }

        RandomAccess.Write(_file, data, _writeOffset);
        _writeOffset += data.Length;
    }
}
