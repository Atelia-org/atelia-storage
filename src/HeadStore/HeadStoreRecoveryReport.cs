using Atelia.Rbf;

namespace Atelia.HeadStore;

/// <summary>一次成功惰性打开已执行的物理修尾动作；不证明应用发布或此前调用成功。</summary>
/// <param name="FileKind">本 HeadStore 中的文件种类。</param>
/// <param name="LocalId">Head 的非零本地编号，或 TagBucket 的 0..1023 桶号。</param>
/// <param name="Recovery">成功 RBF open 返回的原物理恢复报告。</param>
public readonly record struct HeadStoreRecoveryReport(
    HeadStoreRecoveryFileKind FileKind,
    uint LocalId,
    RbfTailRecoveryReport Recovery);
