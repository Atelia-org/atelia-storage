namespace Atelia.VersionStore;

/// <summary>自有 GC buffer 的已检查快照结果，可跨调用及 owner 生命周期保存。</summary>
public sealed class RefBytesSnapshot {
    private readonly byte[] _bytes;

    // 调用方先完成整帧与发布资格，复制实际 payload 并归还 pooled 帧，再移交独占 GC 数组。
    // ownedBytes 不得仍与用户输入、pooled/scratch buffer 或其他可写接口共享；此构造不再次复制或校验。
    internal RefBytesSnapshot(RefRevision revision, long recordedAtUnixMs, byte[] ownedBytes) {
        Revision = revision;
        RecordedAtUnixMs = recordedAtUnixMs;
        _bytes = ownedBytes;
    }

    /// <summary>本快照的实际 ref 与完整位置值。</summary>
    public RefRevision Revision { get; }

    /// <summary>记录保存的有符号 UTC Unix 毫秒值，不保证单调。</summary>
    public long RecordedAtUnixMs { get; }

    /// <summary>不包含 TailMeta 的自有 root-payload bytes，只读访问无需 Dispose。</summary>
    public ReadOnlySpan<byte> Payload => _bytes;
}
