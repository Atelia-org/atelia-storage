namespace Atelia.HeadStore;

/// <summary>自有 GC buffer 的已检查Entry结果，可跨调用及 owner 生命周期保存。</summary>
public sealed class HeadEntry {
    private readonly byte[] _bytes;

    // 调用方先完成整帧与发布资格，复制实际 payload 并归还 pooled 帧，再移交独占 GC 数组。
    // ownedBytes 不得仍与用户输入、pooled/scratch buffer 或其他可写接口共享；此构造不再次复制或校验。
    internal HeadEntry(HeadEntryId entryId, long recordedAtUnixMs, byte[] ownedBytes) {
        Id = entryId;
        RecordedAtUnixMs = recordedAtUnixMs;
        _bytes = ownedBytes;
    }

    /// <summary>本Entry的实际 head 与完整位置值。</summary>
    public HeadEntryId Id { get; }

    /// <summary>记录保存的有符号 UTC Unix 毫秒值，不保证单调。</summary>
    public long RecordedAtUnixMs { get; }

    /// <summary>不包含 TailMeta 的自有 HeadEntry payload bytes，只读访问无需 Dispose。</summary>
    public ReadOnlySpan<byte> Payload => _bytes;
}
