using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

namespace Atelia.Rbf.Internal;

/// <summary>RandomAccess → IByteSink 适配器</summary>
/// <remarks>
/// 职责边界：仅做 Push → RandomAccess.Write 转发 + offset 记账。
/// 设计简化：由于 <see cref="IByteSink"/> 是推式接口（调用者持有数据），
/// 无需持有 buffer、无需 ArrayPool 管理、无需三步舞（GetSpan/GetMemory/Advance）。
/// 串行：此适配器不提供并发保护；拥有它的 File 及派生对象对共享状态与 I/O 的访问须由调用方串行。单 Builder 限制本身不构成并发安全保证。
/// </remarks>
internal sealed class RandomAccessByteSink : IByteSink {
    private readonly SafeFileHandle _file;
    private readonly Action? _onWriteFailure;
    private long _writeOffset;

    /// <summary>创建 RandomAccess 写入适配器</summary>
    /// <param name="file">文件句柄（需具备 Write 权限）</param>
    /// <param name="startOffset">起始写入位置（byte offset）</param>
    /// <param name="onWriteFailure">原始输出异常时标记共享 writer fault。</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> 为 null</exception>
    public RandomAccessByteSink(SafeFileHandle file, long startOffset, Action? onWriteFailure = null) {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _writeOffset = startOffset;
        _onWriteFailure = onWriteFailure;
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
    /// 错误处理：I/O 异常直接抛出（符合 Infra Fault 策略）。
    /// </remarks>
    public void Push(ReadOnlySpan<byte> data) {
        if (data.IsEmpty) { return; }

        try { RbfWriteInstrumentation.Write(_file, data, _writeOffset); }
        catch {
            _onWriteFailure?.Invoke();
            throw;
        }
        _writeOffset += data.Length;
    }

    /// <summary>
    /// 重置写入偏移量，复用同一个 Sink 实例。
    /// </summary>
    /// <param name="writeOffset">新的起始写入偏移量</param>
    public void Reset(long writeOffset) {
        _writeOffset = writeOffset;
    }
}
