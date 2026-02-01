using Atelia.Data;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal;

/// <summary><see cref="IRbfFile"/> 的内部实现类。</summary>
/// <remarks>
/// 职责边界：
/// - RbfFileImpl = Facade：资源管理 + TailOffset 状态 + 参数校验/并发约束
/// - 具体读写/扫描算法下沉到 <c>RbfRawOps</c>（后续阶段实现）
/// </remarks>
internal sealed class RbfFileImpl : IRbfFile {
    private readonly SafeFileHandle _handle;
    private long _tailOffset;
    private bool _disposed;
    private bool _hasActiveBuilder;

    /// <summary>初始化 <see cref="RbfFileImpl"/> 实例。</summary>
    /// <param name="handle">已打开的文件句柄（所有权转移给此实例）。</param>
    /// <param name="tailOffset">初始 TailOffset（文件逻辑长度）。</param>
    internal RbfFileImpl(SafeFileHandle handle, long tailOffset) {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _tailOffset = tailOffset;
    }

    /// <inheritdoc />
    public long TailOffset => _tailOffset;

    /// <inheritdoc />
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta) {
        if (_hasActiveBuilder) {
            throw new InvalidOperationException("Cannot call Append while a builder is active. Dispose the builder first.");
        }
        long tailOffset = _tailOffset;
        // 门面层只负责：持有句柄 + 维护 TailOffset。
        // 失败时 RbfAppendImpl 保证不修改 tailOffset
        AteliaResult<SizedPtr> result = RbfAppendImpl.Append(_handle, ref tailOffset, payload, tailMeta, tag);
        if (result.IsSuccess) {
            _tailOffset = tailOffset;
            return result.Value!;
        }
        return AteliaResult<SizedPtr>.Failure(result.Error!);
    }

    /// <inheritdoc />
    public RbfFrameBuilder BeginAppend() {
        if (_disposed) {
            throw new ObjectDisposedException(nameof(RbfFileImpl));
        }
        if (_hasActiveBuilder) {
            throw new InvalidOperationException("A builder is already active. Dispose it before calling BeginAppend again.");
        }

        long tailOffset = _tailOffset;

        // 检查 TailOffset 4B 对齐
        if ((tailOffset & 0x3) != 0) {
            throw new InvalidOperationException($"TailOffset ({tailOffset}) is not 4-byte aligned.");
        }

        // 检查 MaxFileOffset
        if (tailOffset >= SizedPtr.MaxOffset) {
            throw new InvalidOperationException($"TailOffset ({tailOffset}) has reached MaxFileOffset ({SizedPtr.MaxOffset}).");
        }

        // 创建 RbfFrameBuilder
        var builder = new RbfFrameBuilder(
            _handle,
            tailOffset,
            onCommitCallback: (endOffset) => _tailOffset = endOffset,
            clearBuilderFlag: () => _hasActiveBuilder = false
        );

        _hasActiveBuilder = true;
        return builder;
    }

    /// <inheritdoc />
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) {
        return RbfReadImpl.ReadPooledFrame(_handle, ptr);
    }

    /// <inheritdoc />
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) {
        return RbfReadImpl.ReadFrame(_handle, ptr, buffer);
    }

    /// <inheritdoc />
    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        return new RbfReverseSequence(_handle, _tailOffset, showTombstone);
    }

    /// <inheritdoc />
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) {
        return RbfReadImpl.ReadFrameInfo(_handle, ticket);
    }

    /// <inheritdoc />
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) {
        return RbfReadImpl.ReadTailMeta(_handle, ticket, buffer);
    }

    /// <inheritdoc />
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) {
        return RbfReadImpl.ReadPooledTailMeta(_handle, ticket);
    }

    /// <inheritdoc />
    public void DurableFlush() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RandomAccess.FlushToDisk(_handle);
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
