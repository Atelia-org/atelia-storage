namespace Atelia.VersionStore;

/// <summary>本 VersionStore 中实际执行物理修尾的文件种类。</summary>
public enum VersionStoreRecoveryFileKind {
    /// <summary>由非零本地 ref 编号标识的正式 ref 文件。</summary>
    Ref,
    /// <summary>由 0..1023 本地桶号标识的正式 tag 桶。</summary>
    TagBucket
}
