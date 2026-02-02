using Atelia;
using Atelia.Data;
using Atelia.Rbf.Internal;

namespace Atelia.Rbf;

/// <summary>帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。</summary>
/// <remarks>
/// 生命周期：调用方 MUST 调用 <see cref="EndAppend"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。
/// Auto-Abort（Optimistic Clean Abort）：若未 EndAppend 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
/// 类型选择：采用 readonly struct 作为一次性值对象，避免 Builder 复用带来的语义混淆。
/// B 变体架构：Builder 为薄 Facade，提交/状态机收敛到 RbfFileImpl。
/// </remarks>
public readonly struct RbfFrameBuilder : IDisposable {
    // B 变体：持有 owner 引用和 epoch token
    private readonly RbfFileImpl? _owner;
    private readonly uint _epoch;

    /// <summary>创建 RbfFrameBuilder 实例。</summary>
    /// <param name="owner">所属的 RbfFileImpl（B 变体：提交/取消调用 owner）。</param>
    /// <param name="epoch">epoch token（防止旧 Builder 误用）。</param>
    internal RbfFrameBuilder(RbfFileImpl owner, uint epoch) {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _epoch = epoch;
    }

    /// <summary>Payload 写入器（带 epoch 保护）。</summary>
    /// <remarks>
    /// 该写入器实现 <see cref="System.Buffers.IBufferWriter{T}"/>，因此可用于绝大多数序列化场景。
    /// 此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。
    /// 接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。
    /// 注意：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
    /// 生命周期修复（方案 A）：commit/dispose 后拒绝写入。
    /// </remarks>
    /// <exception cref="InvalidOperationException">Builder 已 commit 或 dispose。</exception>
    public RbfPayloadWriter PayloadAndMeta {
        get {
            var owner = _owner ?? throw new InvalidOperationException("Builder is not initialized.");
            return new RbfPayloadWriter(owner, _epoch);
        }
    }

    /// <summary>提交帧。回填 header/CRC，返回帧位置和长度。</summary>
    /// <param name="tag">帧标签。</param>
    /// <param name="tailMetaLength">TailMeta 长度（位于 Payload 末尾的元数据长度，默认 0）。</param>
    /// <returns>
    /// 成功时返回写入的帧位置和长度（SizedPtr）；
    /// 失败时返回 <see cref="RbfStateError"/>（状态违规）或 <see cref="RbfArgumentError"/>（参数违规）。
    /// </returns>
    /// <remarks>
    /// B 变体：提交逻辑由 File 执行，Builder 仅转发到 owner.CommitFromBuilder。
    /// 方案 D：状态/参数违规返回 Failure，I/O 异常保持抛出。
    /// </remarks>
    public AteliaResult<SizedPtr> EndAppend(uint tag, int tailMetaLength = 0) {
        var owner = _owner ?? throw new InvalidOperationException("Builder is not initialized.");
        return owner.CommitFromBuilder(_epoch, tag, tailMetaLength);
    }

    /// <summary>释放构建器。若未 EndAppend，自动执行 Auto-Abort。</summary>
    /// <remarks>
    /// Auto-Abort 分支约束：<see cref="Dispose"/> 在 Auto-Abort 分支 MUST NOT 抛出异常
    /// （除非出现不可恢复的不变量破坏），并且必须让 File Facade 回到可继续写状态。
    /// B 变体：通过 owner.AbortBuilder 切换状态。
    /// </remarks>
    public readonly void Dispose() {
        if (_owner is null) { return; }
        // B 变体：通知 owner 取消
        _owner.AbortBuilder(_epoch);
    }
}
