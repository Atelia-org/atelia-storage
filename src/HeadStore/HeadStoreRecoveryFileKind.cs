namespace Atelia.HeadStore;

/// <summary>本 HeadStore 中实际执行物理修尾的文件种类。</summary>
public enum HeadStoreRecoveryFileKind {
    /// <summary>由非零本地 head 编号标识的正式 head 文件。</summary>
    Head,
    /// <summary>由 0..1023 本地桶号标识的正式 tag 桶。</summary>
    TagBucket
}
