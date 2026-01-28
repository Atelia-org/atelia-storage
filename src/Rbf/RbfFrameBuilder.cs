using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。</summary>
/// <remarks>
/// 生命周期：调用方 MUST 调用 <see cref="EndAppend"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。
/// Auto-Abort（Optimistic Clean Abort）：若未 EndAppend 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
/// IDisposable 声明：显式实现接口用于类型系统表达"需要释放"的语义，与 using 语句的 duck-typed 机制互补。
/// </remarks>
public ref struct RbfFrameBuilder : IDisposable {
    private bool _disposed;
    private bool _committed;

    /// <summary>Payload 写入器。</summary>
    /// <remarks>
    /// 该写入器实现 <see cref="System.Buffers.IBufferWriter{T}"/>，因此可用于绝大多数序列化场景。
    /// 此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。
    /// 接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。
    /// 注意：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
    /// </remarks>
    public IReservableBufferWriter PayloadAndMeta {
        get {
            throw new NotImplementedException();
        }
    }

    /// <summary>提交帧。回填 header/CRC，返回帧位置和长度。</summary>
    /// <returns>写入的帧位置和长度</returns>
    /// <exception cref="InvalidOperationException">重复调用 EndAppend</exception>
    public SizedPtr EndAppend(uint tag, int tailMetaLength = 0) {
        if (_committed) { throw new InvalidOperationException("EndAppend has already been called."); }
        // TODO: 实现提交逻辑后，在成功路径末尾设置 _committed = true
        throw new NotImplementedException();
    }

    /// <summary>释放构建器。若未 EndAppend，自动执行 Auto-Abort。</summary>
    /// <remarks>
    /// Auto-Abort 分支约束：<see cref="Dispose"/> 在 Auto-Abort 分支 MUST NOT 抛出异常
    /// （除非出现不可恢复的不变量破坏），并且必须让 File Facade 回到可继续写状态。
    /// </remarks>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;

        if (!_committed) {
            // Auto-Abort: 逻辑上该帧视为不存在
            // TODO: 实现 Auto-Abort 逻辑
        }
    }
}
