using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Runtime;
using Atelia.FrameStore.Internal.Storage;
using Atelia.Rbf;

namespace Atelia.FrameStore;

/// <summary>
/// 串行使用的多文件不可变帧存储。可写 owner 排斥其他 owner，只读 owner 之间可以共享。
/// </summary>
/// <remarks>
/// 调用方不得在 owner 存活期间从外部改写、替换或移动受管文件。
/// 完成帧不等于耐久确认；调用 <see cref="ConfirmDurable"/> 确认此前完成的必要输出。
/// </remarks>
public sealed class FrameStore : IDisposable {
    private readonly FrameStoreCore _core;
    private readonly byte[] _storeId;

    internal FrameStore(FrameStoreCore core, StoreIdentity identity, bool readOnly,
        IReadOnlyDictionary<uint, RbfTailRecoveryReport> recoveryReports) {
        _storeId = new byte[StoreIdentity.EncodedSize];
        if (!identity.TryWrite(_storeId)) { throw new ArgumentException("A nonzero store identity is required.", nameof(identity)); }
        _core = core;
        IsReadOnly = readOnly;
        RecoveryReports = recoveryReports;
    }

    /// <summary>在不存在或合格空目录中创建 store；不覆盖、修复或续作已有初始化树。</summary>
    /// <param name="rootPath">由调用方选择的 store 根路径；父目录必须已经存在。</param>
    /// <param name="rotationThresholdBytes">完成后超过此值的文件停止分配；默认 64GiB。</param>
    public static FrameStore Create(string rootPath, long rotationThresholdBytes = RotationThreshold.DefaultBytes) =>
        FrameStoreFactory.Create(rootPath, rotationThresholdBytes);

    /// <summary>独占打开既有 store，恢复合格 active 的用户残尾并完成必要归档。</summary>
    public static FrameStore Open(string rootPath, long rotationThresholdBytes = RotationThreshold.DefaultBytes) =>
        FrameStoreFactory.Open(rootPath, rotationThresholdBytes, readOnly: false);

    /// <summary>共享只读打开既有 store；不恢复、清理私有残留或归档。</summary>
    public static FrameStore OpenReadOnly(string rootPath) =>
        FrameStoreFactory.Open(rootPath, RotationThreshold.DefaultBytes, readOnly: true);

    /// <summary>持久身份的 16 个 opaque bytes；路径移动不改变此身份。</summary>
    public ReadOnlySpan<byte> StoreId => _storeId;

    /// <summary>本 owner 是否只读。</summary>
    public bool IsReadOnly { get; }

    /// <summary>本次可写 Open 按文件编号记录的物理恢复报告；Create/只读打开为空。</summary>
    /// <remarks>报告不证明应用发布或此前调用成功，也不撤销已存在的完整帧。</remarks>
    public IReadOnlyDictionary<uint, RbfTailRecoveryReport> RecoveryReports { get; }

    /// <summary>同步追加完整 payload/meta；不消耗未归还 Builder 配额。</summary>
    public AteliaResult<FrameAddress> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) =>
        _core.Append(tag, payload, tailMeta);

    /// <summary>开始未知尺寸的单帧构建；完成或取消前独占一个 active 文件。</summary>
    public FrameBuilder BeginAppend() => _core.BeginAppend();

    /// <summary>声明最终 stored 长度并取得提前地址；该地址不证明帧已经完成。</summary>
    public FrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out FrameAddress address) =>
        _core.BeginAppend(payloadLength, tailMetaLength, out address);

    /// <summary>完整校验并读取一帧；成功结果独立拥有 buffer，由调用方释放。</summary>
    public AteliaResult<FrameRead> ReadFrame(FrameAddress address) => _core.ReadFrame(address);

    /// <summary>同步访问实际正式集合中的用户帧元信息，成功返回用户帧总数。</summary>
    /// <remarks>
    /// 检查文件 header 与用户帧结构，不校验用户 PayloadCRC。包含墓碑；文件顺序不代表业务顺序。
    /// 要求没有活跃 Builder；回调可随机读或 Dispose owner，不能追加、确认或递归扫描。
    /// 取消或回调异常会终止扫描；已交付前缀不表示全库成功。
    /// </remarks>
    public AteliaResult<long> Inventory(Action<FrameInfo> visitor, CancellationToken cancellationToken = default) =>
        _core.Inventory(visitor, cancellationToken);

    /// <summary>完整校验全部实际正式帧，逐文件回调，成功返回用户帧总数。</summary>
    /// <remarks>
    /// 包含墓碑；仅在文件正常 EOF 和必要 reader 关闭成功后回调。报告不证明业务引用闭包或发布。
    /// 准入、重入和取消规则与 Inventory 相同；最后一次回调关闭 owner 或取消也不能返回成功。
    /// </remarks>
    public AteliaResult<long> Audit(Action<FrameFileAudit> visitor, CancellationToken cancellationToken = default) =>
        _core.Audit(visitor, cancellationToken);

    /// <summary>同步确认本次调用前全部必要已完成输出；不完成或取消活跃 Builder。</summary>
    public void ConfirmDurable() => _core.ConfirmDurable();

    /// <summary>失效全部 Builder，单次关闭数据资源，最后释放 owner 锁；不隐式确认或归档。</summary>
    public void Dispose() => _core.Dispose();
}
