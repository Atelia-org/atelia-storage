using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf;

/// <summary>帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。</summary>
/// <remarks>
/// 生命周期：调用方 MUST 调用 <see cref="EndAppend"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。
/// Auto-Abort（Optimistic Clean Abort）：若未 EndAppend 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
/// 类型选择：采用 sealed class 而非 ref struct，因为内部组件（SinkReservableWriter 等）
/// 本就是堆分配，ref struct 外壳无实际收益；sealed class 更简单且支持未来 Reset 复用优化。
/// </remarks>
public sealed class RbfFrameBuilder : IDisposable {
    private long _frameStart;
    private readonly RandomAccessByteSink _sink;
    private readonly SinkReservableWriter _writer;
    private int _headLenReservationToken;
    private Action<long> _onCommitCallback;
    private Action _clearBuilderFlag;
    private bool _committed;
    private bool _disposed;

    /// <summary>创建 RbfFrameBuilder 实例。</summary>
    /// <param name="handle">文件句柄（需具备 Write 权限）。</param>
    /// <param name="frameStart">帧起始位置（byte offset）。</param>
    /// <param name="onCommitCallback">提交成功时的回调，参数为帧结束位置（含 Fence）。</param>
    /// <param name="clearBuilderFlag">Dispose 时清除 active builder 标志的回调。</param>
    internal RbfFrameBuilder(
        SafeFileHandle handle,
        long frameStart,
        Action<long> onCommitCallback,
        Action clearBuilderFlag
    ) {
        _frameStart = frameStart;
        _onCommitCallback = onCommitCallback ?? throw new ArgumentNullException(nameof(onCommitCallback));
        _clearBuilderFlag = clearBuilderFlag ?? throw new ArgumentNullException(nameof(clearBuilderFlag));

        // 1. 创建 RbfWriteSink：crcSkipBytes = HeadLenSize（4），跳过 HeadLen 不参与 CRC
        _sink = new RandomAccessByteSink(handle, frameStart);

        // 2. 创建 SinkReservableWriter
        _writer = new SinkReservableWriter(_sink);

        // 3. 预留 HeadLen（阻塞 flush，支持 Zero-IO 取消）
        _ = _writer.ReserveSpan(FrameLayout.HeadLenSize, out _headLenReservationToken, tag: "HeadLen");
    }

    /// <summary>Payload 写入器。</summary>
    /// <remarks>
    /// 该写入器实现 <see cref="System.Buffers.IBufferWriter{T}"/>，因此可用于绝大多数序列化场景。
    /// 此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。
    /// 接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。
    /// 注意：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
    /// </remarks>
    public IReservableBufferWriter PayloadAndMeta => _writer;

    /// <summary>提交帧。回填 header/CRC，返回帧位置和长度。</summary>
    /// <param name="tag">帧标签。</param>
    /// <param name="tailMetaLength">TailMeta 长度（位于 Payload 末尾的元数据长度，默认 0）。</param>
    /// <returns>写入的帧位置和长度（SizedPtr）。</returns>
    /// <exception cref="InvalidOperationException">
    /// 重复调用 EndAppend，或存在未提交的 reservation（除 HeadLen 外），或参数不满足约束。
    /// </exception>
    public SizedPtr EndAppend(uint tag, int tailMetaLength = 0) {
        // 1. 验证前置条件
        if (_committed) { throw new InvalidOperationException("EndAppend has already been called."); }
        if (_disposed) { throw new ObjectDisposedException(nameof(RbfFrameBuilder)); }

        // 前置条件：只剩 HeadLen reservation（_writer.PendingReservationCount == 1）
        if (_writer.PendingReservationCount != 1) {
            throw new InvalidOperationException(
                $"All reservations must be committed before EndAppend. Pending: {_writer.PendingReservationCount}, expected: 1 (HeadLen only)."
            );
        }

        // 2. 获取 payloadAndMetaLength（WrittenLength - HeadLenSize，因为 HeadLen 仍为 pending）
        long payloadAndMetaLength = _writer.WrittenLength - FrameLayout.HeadLenSize;

        // 2a. 资源上限校验 (Decision 7.F)
        if (payloadAndMetaLength > FrameLayout.MaxPayloadAndMetaLength) {
            throw new InvalidOperationException(
                $"Payload + TailMeta length ({payloadAndMetaLength}) exceeds maximum ({FrameLayout.MaxPayloadAndMetaLength})."
            );
        }

        // 验证 tailMetaLength 约束
        if (tailMetaLength < 0) { throw new ArgumentOutOfRangeException(nameof(tailMetaLength), tailMetaLength, "tailMetaLength must be non-negative."); }
        if (tailMetaLength > payloadAndMetaLength) {
            throw new InvalidOperationException(
                $"tailMetaLength ({tailMetaLength}) exceeds payloadAndMetaLength ({payloadAndMetaLength})."
            );
        }
        if (tailMetaLength > FrameLayout.MaxTailMetaLength) {
            throw new InvalidOperationException(
                $"tailMetaLength ({tailMetaLength}) exceeds MaxTailMetaLength ({FrameLayout.MaxTailMetaLength})."
            );
        }

        // 3. 计算 FrameLayout
        int payloadLength = (int)payloadAndMetaLength - tailMetaLength;
        var layout = new FrameLayout(payloadLength, tailMetaLength);

        // 4. 写入 Padding（通过 _writer，CRC 自动累积）
        if (layout.PaddingLength > 0) {
            var paddingSpan = _writer.GetSpan(layout.PaddingLength);
            paddingSpan[..layout.PaddingLength].Clear(); // Fill with zeros
            _writer.Advance(layout.PaddingLength);
        }

        // 5. 获取 PayloadCrc：从 HeadLen reservation 末尾到当前写入末尾（覆盖 Payload + TailMeta + Padding）
        uint payloadCrc = _writer.GetCrcSinceReservationEnd(_headLenReservationToken);

        // 6. 构建 Tail buffer（PayloadCrc + Trailer + Fence），写入 _writer
        int tailLength = FrameLayout.PayloadCrcSize + FrameLayout.TrailerCodewordSize + RbfLayout.FenceSize;
        var tailSpan = _writer.GetSpan(tailLength);

        // 6a. 写入 PayloadCrc (4 bytes, LE)
        BinaryPrimitives.WriteUInt32LittleEndian(tailSpan, payloadCrc);

        // 6b. 写入 TrailerCodeword (16 bytes)
        layout.FillTrailer(tailSpan.Slice(FrameLayout.PayloadCrcSize, FrameLayout.TrailerCodewordSize), tag, isTombstone: false);

        // 6c. 写入 Fence (4 bytes, "RBF1")
        RbfLayout.Fence.CopyTo(tailSpan.Slice(FrameLayout.PayloadCrcSize + FrameLayout.TrailerCodewordSize, RbfLayout.FenceSize));

        _writer.Advance(tailLength);

        // 7. 回填 HeadLen（FrameLength，不含 Fence）
        if (!_writer.TryGetReservationSpan(_headLenReservationToken, out var headLenSpan)) { throw new InvalidOperationException("HeadLen reservation not found (internal error)."); }
        BinaryPrimitives.WriteUInt32LittleEndian(headLenSpan, (uint)layout.FrameLength);

        // 8. 调用 Commit（触发 flush 全部数据到磁盘）
        _writer.Commit(_headLenReservationToken);

        // 9. 调用 _onCommitCallback 通知更新 TailOffset
        // TailOffset = startOffset + frameLength + FenceSize
        long endOffset = _frameStart + layout.FrameLength + RbfLayout.FenceSize;
        _onCommitCallback(endOffset);

        // 设置 committed 标志
        _committed = true;

        // 10. 返回 SizedPtr
        return SizedPtr.Create(_frameStart, layout.FrameLength);
    }

    /// <summary>指示 Builder 是否可以被复用（已 Dispose 或已 Commit）。</summary>
    internal bool CanBeReused => _disposed || _committed;

    /// <summary>
    /// 重置 Builder 状态，准备写入新帧。
    /// </summary>
    /// <param name="frameStart">新帧的起始偏移量</param>
    /// <param name="onCommitCallback">提交回调（可复用已缓存的 delegate）</param>
    /// <param name="clearBuilderFlag">清除标记回调（可复用已缓存的 delegate）</param>
    /// <exception cref="InvalidOperationException">Builder 处于活跃状态（未 Dispose 且未 Commit）。</exception>
    internal void Reset(long frameStart, Action<long> onCommitCallback, Action clearBuilderFlag) {
        // 1. 验证状态
        if (!_disposed && !_committed) {
            throw new InvalidOperationException("Cannot reset an active builder. Dispose or commit first.");
        }

        // 2. 重置位置信息
        _frameStart = frameStart;
        _onCommitCallback = onCommitCallback;
        _clearBuilderFlag = clearBuilderFlag;

        // 3. 重置状态标记
        _committed = false;
        _disposed = false;

        // 4. 重置 Sink（使用 Phase 2 添加的方法）
        _sink.Reset(frameStart);

        // 5. 重置 Writer
        _writer.Reset();

        // 6. 重新预留 HeadLen
        _ = _writer.ReserveSpan(FrameLayout.HeadLenSize, out _headLenReservationToken, tag: "HeadLen");
    }

    /// <summary>释放构建器。若未 EndAppend，自动执行 Auto-Abort。</summary>
    /// <remarks>
    /// Auto-Abort 分支约束：<see cref="Dispose"/> 在 Auto-Abort 分支 MUST NOT 抛出异常
    /// （除非出现不可恢复的不变量破坏），并且必须让 File Facade 回到可继续写状态。
    /// 注意：为支持 per-file 复用，Dispose 不再销毁 _writer，仅重置状态。
    /// </remarks>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;

        if (!_committed) {
            // Auto-Abort: Zero I/O（HeadLen reservation 阻塞 flush，数据全在内存中）
            _writer.Reset();
        }

        // 清除 RbfFileImpl 的 active builder 标志
        _clearBuilderFlag?.Invoke();

        // 注意：不再 Dispose _writer，因为要复用
    }

    /// <summary>
    /// 彻底释放内部资源（仅供 RbfFileImpl.Dispose 调用）。
    /// </summary>
    internal void DisposeInternal() {
        if (!_disposed) { Dispose(); }
        _writer.Dispose();
    }
}
