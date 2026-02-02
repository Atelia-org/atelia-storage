using System.Buffers.Binary;
using System.Diagnostics;
using Atelia.Data;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal;

/// <summary>File 状态机状态。</summary>
internal enum FileState {
    /// <summary>空闲状态，可接受 Append 或 BeginAppend。</summary>
    Idle,
    /// <summary>正在构建帧，Builder 活跃中。</summary>
    Building
}

/// <summary><see cref="IRbfFile"/> 的内部实现类。</summary>
/// <remarks>
/// 职责边界：
/// - RbfFileImpl = Facade：资源管理 + TailOffset 状态 + 参数校验/并发约束 + Builder 提交/取消
/// - 具体读写/扫描算法下沉到 <c>RbfRawOps</c>（后续阶段实现）
/// 状态机模型（B 变体）：
/// - Idle → Building（BeginAppend）
/// - Building → Idle（CommitFromBuilder / AbortBuilder）
/// - Epoch Token 防止旧 Builder 误用
/// </remarks>
internal sealed class RbfFileImpl : IRbfFile {
    private readonly SafeFileHandle _handle;
    private long _tailOffset;
    private bool _disposed;

    // Builder 写入资源（B 变体：由 File 统一持有）
    private readonly RandomAccessByteSink _builderSink;
    private readonly SinkReservableWriter _builderWriter;
    private int _builderHeadLenReservationToken;
    private long _builderFrameStart;

    private enum BuilderCloseReason {
        None,
        Committed,
        Aborted
    }
    private BuilderCloseReason _builderLastClose = BuilderCloseReason.None;

    // 状态机字段
    private FileState _fileState = FileState.Idle;
    private uint _builderEpoch;  // epoch token，每次 BeginAppend 递增

    /// <summary>初始化 <see cref="RbfFileImpl"/> 实例。</summary>
    /// <param name="handle">已打开的文件句柄（所有权转移给此实例）。</param>
    /// <param name="tailOffset">初始 TailOffset（文件逻辑长度）。</param>
    internal RbfFileImpl(SafeFileHandle handle, long tailOffset) {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _tailOffset = tailOffset;

        _builderSink = new RandomAccessByteSink(_handle, tailOffset);
        _builderWriter = new SinkReservableWriter(_builderSink);
    }

    /// <inheritdoc />
    public long TailOffset => _tailOffset;

    private void EnsureIdleForRead() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fileState != FileState.Idle) { throw new InvalidOperationException("Cannot read while a builder is active. Dispose the builder first."); }
    }

    /// <inheritdoc />
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta) {
        // 生命周期检查：Dispose 入口检查
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_fileState != FileState.Idle) { throw new InvalidOperationException("Cannot call Append while a builder is active. Dispose the builder first."); }
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
        if (_disposed) { throw new ObjectDisposedException(nameof(RbfFileImpl)); }
        if (_fileState != FileState.Idle) { throw new InvalidOperationException("A builder is already active. Dispose it before calling BeginAppend again."); }

        long tailOffset = _tailOffset;

        // 检查 TailOffset 4B 对齐
        if ((tailOffset & 0x3) != 0) { throw new InvalidOperationException($"TailOffset ({tailOffset}) is not 4-byte aligned."); }

        // 检查 MaxFileOffset
        if (tailOffset >= SizedPtr.MaxOffset) { throw new InvalidOperationException($"TailOffset ({tailOffset}) has reached MaxFileOffset ({SizedPtr.MaxOffset})."); }

        // 递增 epoch，状态切换为 Building
        _builderEpoch++;
        _fileState = FileState.Building;
        _builderLastClose = BuilderCloseReason.None;
        _builderFrameStart = tailOffset;

        // 重置 writer/sink 并预留 HeadLen
        _builderSink.Reset(tailOffset);
        _builderWriter.Reset();
        _ = _builderWriter.ReserveSpan(FrameLayout.HeadLenSize, out _builderHeadLenReservationToken, tag: "HeadLen");

        return new RbfFrameBuilder(
            owner: this,
            epoch: _builderEpoch
        );
    }

    /// <summary>由 Builder 调用：获取 Payload 写入器（epoch/state 校验）。</summary>
    internal SinkReservableWriter GetPayloadWriter(uint epoch) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (epoch != _builderEpoch) {
            throw new InvalidOperationException(
                $"Stale builder epoch: expected {_builderEpoch}, got {epoch}."
            );
        }
        if (_fileState != FileState.Building) { throw new InvalidOperationException("Builder is not active."); }
        return _builderWriter;
    }

    /// <summary>由 Builder 调用：提交帧（状态机 + 写入逻辑收敛到 File）。</summary>
    internal AteliaResult<SizedPtr> CommitFromBuilder(uint epoch, uint tag, int tailMetaLength) {
        // 1. 生命周期检查（方案 D：状态违规返回 Failure）
        if (_disposed) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfStateError(
                    "File has been disposed.",
                    RecoveryHint: "Create a new file facade before writing."
                )
            );
        }
        if (epoch != _builderEpoch) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfStateError(
                    $"Stale builder epoch: expected {_builderEpoch}, got {epoch}.",
                    RecoveryHint: "Discard the old builder reference and call BeginAppend again."
                )
            );
        }
        if (_fileState != FileState.Building) {
            if (_builderLastClose == BuilderCloseReason.Committed) {
                return AteliaResult<SizedPtr>.Failure(
                    new RbfStateError(
                        "EndAppend has already been called.",
                        RecoveryHint: "Each builder can only commit once. Create a new builder for subsequent frames."
                    )
                );
            }
            if (_builderLastClose == BuilderCloseReason.Aborted) {
                return AteliaResult<SizedPtr>.Failure(
                    new RbfStateError(
                        "Builder has been disposed.",
                        RecoveryHint: "Cannot call EndAppend on a disposed builder."
                    )
                );
            }
            return AteliaResult<SizedPtr>.Failure(
                new RbfStateError(
                    "No active builder to commit.",
                    RecoveryHint: "Call BeginAppend before EndAppend."
                )
            );
        }

        // 2. 前置条件：只剩 HeadLen reservation（_builderWriter.PendingReservationCount == 1）
        if (_builderWriter.PendingReservationCount != 1) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfStateError(
                    $"All reservations must be committed before EndAppend. Pending: {_builderWriter.PendingReservationCount}, expected: 1 (HeadLen only).",
                    RecoveryHint: "Commit or cancel all payload reservations before calling EndAppend."
                )
            );
        }

        // 3. 获取 payloadAndMetaLength（WrittenLength - HeadLenSize，因为 HeadLen 仍为 pending）
        long payloadAndMetaLength = _builderWriter.WrittenLength - FrameLayout.HeadLenSize;

        // 3a. 资源上限校验 (Decision 7.F) - 方案 D：返回 Failure
        if (payloadAndMetaLength > FrameLayout.MaxPayloadAndMetaLength) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"Payload + TailMeta length ({payloadAndMetaLength}) exceeds maximum ({FrameLayout.MaxPayloadAndMetaLength}).",
                    RecoveryHint: "Reduce payload size or split into multiple frames."
                )
            );
        }

        // 验证 tailMetaLength 约束 - 方案 D：参数违规返回 Failure
        if (tailMetaLength < 0) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"tailMetaLength ({tailMetaLength}) must be non-negative."
                )
            );
        }
        if (tailMetaLength > payloadAndMetaLength) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"tailMetaLength ({tailMetaLength}) exceeds payloadAndMetaLength ({payloadAndMetaLength})."
                )
            );
        }
        if (tailMetaLength > FrameLayout.MaxTailMetaLength) {
            return AteliaResult<SizedPtr>.Failure(
                new RbfArgumentError(
                    $"tailMetaLength ({tailMetaLength}) exceeds MaxTailMetaLength ({FrameLayout.MaxTailMetaLength})."
                )
            );
        }

        // 4. 计算 FrameLayout
        int payloadLength = (int)payloadAndMetaLength - tailMetaLength;
        var layout = new FrameLayout(payloadLength, tailMetaLength);

        // 4a. MaxFileOffset 校验（方案 A + D：统一委托给 RbfFrameWriteCore）
        var endOffsetError = RbfFrameWriteCore.ValidateEndOffset(_builderFrameStart, layout.FrameLength);
        if (endOffsetError is not null) { return AteliaResult<SizedPtr>.Failure(endOffsetError); }

        // 5. 写入 Padding（通过 _builderWriter，CRC 自动累积）
        if (layout.PaddingLength > 0) {
            var paddingSpan = _builderWriter.GetSpan(layout.PaddingLength);
            paddingSpan[..layout.PaddingLength].Clear();
            _builderWriter.Advance(layout.PaddingLength);
        }

        // 6. 获取 PayloadCrc：从 HeadLen reservation 末尾到当前写入末尾（覆盖 Payload + TailMeta + Padding）
        uint payloadCrc = _builderWriter.GetCrcSinceReservationEnd(_builderHeadLenReservationToken);

        // 7. 构建 Tail buffer（PayloadCrc + Trailer + Fence），写入 _builderWriter
        var tailSpan = _builderWriter.GetSpan(RbfFrameWriteCore.TailSize);
        RbfFrameWriteCore.WriteTail(tailSpan, in layout, tag, isTombstone: false, payloadCrc);
        _builderWriter.Advance(RbfFrameWriteCore.TailSize);

        // 8. 回填 HeadLen（FrameLength，不含 Fence）
        if (!_builderWriter.TryGetReservedSpan(_builderHeadLenReservationToken, out var headLenSpan)) { throw new InvalidOperationException("HeadLen reservation not found (internal error)."); }
        BinaryPrimitives.WriteUInt32LittleEndian(headLenSpan, (uint)layout.FrameLength);

        // 9. 调用 Commit（触发 flush 全部数据到磁盘）
        _builderWriter.Commit(_builderHeadLenReservationToken);

        // 10. 更新 TailOffset 并切换状态
        long endOffset = _builderFrameStart + layout.FrameLength + RbfLayout.FenceSize;
        Debug.Assert(endOffset >= _tailOffset, "Commit endOffset must not be less than current TailOffset.");
        _tailOffset = endOffset;
        _fileState = FileState.Idle;
        _builderLastClose = BuilderCloseReason.Committed;

        // 11. 返回 SizedPtr（方案 D：包装为 Success）
        return AteliaResult<SizedPtr>.Success(SizedPtr.Create(_builderFrameStart, layout.FrameLength));
    }

    /// <summary>由 Builder 调用：取消帧构建，切换状态为 Idle（不更新 TailOffset）。</summary>
    /// <param name="epoch">Builder 持有的 epoch token（必须匹配当前 epoch）。</param>
    /// <exception cref="InvalidOperationException">epoch 不匹配（旧 Builder 误用）。</exception>
    internal void AbortBuilder(uint epoch) {
        if (epoch != _builderEpoch || _disposed) { return; }
        if (_fileState != FileState.Building) {
            // Abort 在非 Building 状态下静默忽略（幂等）
            return;
        }
        _builderWriter.Reset();
        _fileState = FileState.Idle;
        _builderLastClose = BuilderCloseReason.Aborted;
    }

    /// <inheritdoc />
    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) {
        EnsureIdleForRead();
        return RbfReadImpl.ReadPooledFrame(_handle, ptr);
    }

    /// <inheritdoc />
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) {
        EnsureIdleForRead();
        return RbfReadImpl.ReadFrame(_handle, ptr, buffer);
    }

    /// <inheritdoc />
    public RbfReverseSequence ScanReverse(bool showTombstone = false) {
        EnsureIdleForRead();
        return new RbfReverseSequence(_handle, _tailOffset, showTombstone);
    }

    /// <inheritdoc />
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) {
        EnsureIdleForRead();
        return RbfReadImpl.ReadFrameInfo(_handle, ticket);
    }

    /// <inheritdoc />
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) {
        EnsureIdleForRead();
        return RbfReadImpl.ReadTailMeta(_handle, ticket, buffer);
    }

    /// <inheritdoc />
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) {
        EnsureIdleForRead();
        return RbfReadImpl.ReadPooledTailMeta(_handle, ticket);
    }

    /// <inheritdoc />
    public void DurableFlush() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RandomAccess.FlushToDisk(_handle);
    }

    /// <inheritdoc />
    public void Truncate(long newLengthBytes) {
        // 1. Disposed 检查
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 2. Active builder 检查
        if (_fileState != FileState.Idle) {
            throw new InvalidOperationException(
                "Cannot truncate while a builder is active. Dispose the builder first."
            );
        }

        // 3. 参数校验
        if (newLengthBytes < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(newLengthBytes), newLengthBytes,
                "newLengthBytes must be non-negative."
            );
        }

        if ((newLengthBytes & 0x3) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(newLengthBytes), newLengthBytes,
                "newLengthBytes must be 4-byte aligned."
            );
        }

        // 4. 执行截断
        RandomAccess.SetLength(_handle, newLengthBytes);

        // 5. 更新 TailOffset
        _tailOffset = newLengthBytes;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (!_disposed) {
            _builderWriter.Dispose();

            _handle.Dispose();
            _disposed = true;
        }
    }
}
