using System.Buffers;
using System.Runtime.InteropServices;
using Atelia.Data;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal;

/// <summary>
/// <see cref="IRbfFile"/> 的内部实现类。
/// </summary>
/// <remarks>
/// <para>职责边界：</para>
/// <para>- <b>RbfFileImpl = Facade</b>：资源管理 + TailOffset 状态 + 参数校验/并发约束</para>
/// <para>- 具体读写/扫描算法下沉到 <c>RbfRawOps</c>（后续阶段实现）</para>
/// </remarks>
internal sealed class RbfFileImpl : IRbfFile {
    private readonly SafeFileHandle _handle;
    private long _tailOffset;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="RbfFileImpl"/> 实例。
    /// </summary>
    /// <param name="handle">已打开的文件句柄（所有权转移给此实例）。</param>
    /// <param name="tailOffset">初始 TailOffset（文件逻辑长度）。</param>
    internal RbfFileImpl(SafeFileHandle handle, long tailOffset) {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _tailOffset = tailOffset;
    }

    /// <inheritdoc />
    public long TailOffset => _tailOffset;

    /// <inheritdoc />
    public SizedPtr Append(uint tag, ReadOnlySpan<byte> payload) {
        // 1. 计算 HeadLen，@[S-RBF-SIZEDPTR-WIRE-MAPPING]
        int headLen = RbfRawOps.ComputeHeadLen(payload.Length);

        // 2. 分配 buffer（stackalloc 小帧，ArrayPool 大帧）
        const int MaxStackAllocSize = 512;
        byte[]? rentedBuffer = null;
        Span<byte> buffer = headLen <= MaxStackAllocSize
            ? stackalloc byte[headLen]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(headLen)).AsSpan(0, headLen);

        try {
            // 3. 序列化帧
            RbfRawOps.SerializeFrame(buffer, tag, payload);

            // 4. 构造 SizedPtr（在写入前记录，因为 _tailOffset 会变）
            //    @[S-RBF-SIZEDPTR-WIRE-MAPPING]: Offset = FrameBytes 起点, Length = HeadLen
            var frameOffset = _tailOffset;
            var ptr = SizedPtr.Create((ulong)frameOffset, (uint)headLen);

            // 5. 写入 FrameBytes
            RandomAccess.Write(_handle, buffer, frameOffset);

            // 6. 写入 Fence，@[F-FENCE-IS-SEPARATOR-NOT-FRAME]
            RandomAccess.Write(_handle, RbfConstants.Fence, frameOffset + headLen);

            // 7. 更新 TailOffset
            _tailOffset = frameOffset + headLen + RbfConstants.FenceLength;

            return ptr;
        } finally {
            // 8. 归还租用的 buffer
            if (rentedBuffer != null) {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    /// <inheritdoc />
    public RbfFrameBuilder BeginAppend(uint tag) {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr) {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public void DurableFlush() {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public void Truncate(long newLengthBytes) {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (!_disposed) {
            _handle.Dispose();
            _disposed = true;
        }
    }
}
