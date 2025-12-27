using System.Buffers;
using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>
/// RBF 帧写入器接口。负责将 payload 封装为 Frame 并追加到日志。
/// </summary>
/// <remarks>
/// <para><b>[A-RBF-FRAMER-INTERFACE]</b>: IRbfFramer 接口定义。</para>
/// <para><b>线程安全</b>：非线程安全，单生产者使用。</para>
/// <para><b>并发约束</b>：同一时刻最多 1 个 open RbfFrameBuilder。</para>
/// </remarks>
public interface IRbfFramer {
    /// <summary>
    /// 追加一个完整的帧（简单场景：payload 已就绪）。
    /// </summary>
    /// <param name="tag">帧类型标识符。</param>
    /// <param name="payload">帧负载（可为空）。</param>
    /// <returns>写入的帧起始地址。</returns>
    Address64 Append(FrameTag tag, ReadOnlySpan<byte> payload);

    /// <summary>
    /// 开始构建一个帧（高级场景：流式写入或需要 payload 内回填）。
    /// </summary>
    /// <param name="tag">帧类型标识符。</param>
    /// <returns>帧构建器（必须 Commit 或 Dispose）。</returns>
    /// <exception cref="InvalidOperationException">已有未完成的 Builder 时抛出。</exception>
    RbfFrameBuilder BeginFrame(FrameTag tag);

    /// <summary>
    /// 将 RBF 缓冲数据推送到底层 Writer/Stream。
    /// </summary>
    /// <remarks>
    /// <para><b>[S-RBF-FRAMER-NO-FSYNC]</b>: 本方法仅保证 RBF 层的缓冲被推送到下层，
    /// 不保证数据持久化到物理介质。</para>
    /// <para><b>上层责任</b>：如需 durable commit（如 StateJournal 的 data→meta 顺序），
    /// 由上层在其持有的底层句柄上执行 durable flush。</para>
    /// </remarks>
    void Flush();
}

/// <summary>
/// 帧构建器。支持流式写入 payload，完成后自动回填 header/CRC。
/// </summary>
/// <remarks>
/// <para><b>[A-RBF-FRAME-BUILDER]</b>: RbfFrameBuilder 结构定义。</para>
/// <para><b>生命周期</b>：必须调用 <see cref="Commit"/> 或 <see cref="Dispose"/>。</para>
/// <para><b>[S-RBF-BUILDER-AUTO-ABORT]</b>（Optimistic Clean Abort）：若未 Commit 就 Dispose，
/// 逻辑上该帧视为不存在。物理实现为写入 Tombstone 帧。</para>
/// </remarks>
public ref struct RbfFrameBuilder {
    private readonly RbfFramer _framer;
    private readonly long _frameStart;
    private readonly FrameTag _tag;
    private readonly bool _useChunkedWriter;
    private readonly ChunkedReservableWriter? _chunkedWriter;
    private readonly int _headLenReservationToken;
    private readonly Span<byte> _headLenSpan;
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// 创建帧构建器。
    /// </summary>
    internal RbfFrameBuilder(RbfFramer framer, long frameStart, FrameTag tag) {
        _framer = framer;
        _frameStart = frameStart;
        _tag = tag;
        _useChunkedWriter = false;
        _chunkedWriter = null;
        _headLenReservationToken = 0;
        _headLenSpan = default;
        _committed = false;
        _disposed = false;
    }

    internal RbfFrameBuilder(
        RbfFramer framer,
        long frameStart,
        FrameTag tag,
        ChunkedReservableWriter chunkedWriter,
        int headLenReservationToken,
        Span<byte> headLenSpan
    ) {
        _framer = framer;
        _frameStart = frameStart;
        _tag = tag;
        _useChunkedWriter = true;
        _chunkedWriter = chunkedWriter;
        _headLenReservationToken = headLenReservationToken;
        _headLenSpan = headLenSpan;
        _committed = false;
        _disposed = false;
    }

    /// <summary>
    /// Payload 写入器（标准接口，满足大多数序列化需求）。
    /// </summary>
    public IBufferWriter<byte> Payload => _useChunkedWriter ? _chunkedWriter! : throw new InvalidOperationException("Non-streaming builder is not supported.");

    /// <summary>
    /// 可预留的 Payload 写入器（可选，供需要 payload 内回填的 codec 使用）。
    /// </summary>
    /// <remarks>
    /// <para>若返回非 null，则 codec 可以在 payload 内进行预留回填。</para>
    /// <para><b>Auto-Abort 语义</b>：若底层支持丢弃未提交数据，Abort 可实现 Zero I/O；
    /// 否则实现可退化为写入 Tombstone 帧。</para>
    /// </remarks>
    public IReservableBufferWriter? ReservablePayload => _useChunkedWriter ? _chunkedWriter : null;

    /// <summary>
    /// 提交帧。回填 header/CRC，返回帧起始地址。
    /// </summary>
    /// <returns>写入的帧起始地址。</returns>
    /// <exception cref="InvalidOperationException">重复调用 Commit。</exception>
    /// <exception cref="ObjectDisposedException">已 Dispose 后调用。</exception>
    public Address64 Commit() {
        ObjectDisposedException.ThrowIf(_disposed, nameof(RbfFrameBuilder));
        if (_committed) { throw new InvalidOperationException("Frame has already been committed."); }

        _committed = true;
        if (_useChunkedWriter) { return _framer.CommitFrameStreaming(_frameStart, _tag, _chunkedWriter!, _headLenReservationToken, _headLenSpan, FrameStatus.CreateValid(1)); }

        return _framer.CommitFrame(_frameStart, _tag, FrameStatus.CreateValid(1)); // StatusLen will be recalculated
    }

    /// <summary>
    /// 释放构建器。若未 Commit，自动执行 Auto-Abort（写入 Tombstone）。
    /// </summary>
    /// <remarks>
    /// <para><b>[S-RBF-BUILDER-AUTO-ABORT]</b>: 未 Commit 时写入 Tombstone 帧。</para>
    /// </remarks>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;

        if (!_committed) {
            if (_useChunkedWriter) {
                // Zero-I/O abort: drop buffered data without writing to underlying stream.
                _framer.AbortFrameStreaming(_chunkedWriter!);
            }
            else {
                // Fallback Auto-Abort: 写入 Tombstone 帧
                _framer.CommitFrame(_frameStart, _tag, FrameStatus.CreateTombstone(1)); // StatusLen will be recalculated
            }
        }

        _framer.EndBuilder();
    }
}
