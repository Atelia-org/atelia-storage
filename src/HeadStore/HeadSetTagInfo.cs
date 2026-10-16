namespace Atelia.HeadStore;

/// <summary>经独立元信息校验的自有 tag 信息，不证明成员表或成员内容健康。</summary>
public sealed class HeadSetTagInfo {
    internal HeadSetTagInfo(string name, long recordedAtUnixMs, int entryCount) {
        Name = name;
        RecordedAtUnixMs = recordedAtUnixMs;
        EntryCount = entryCount;
    }

    /// <summary>按 Ordinal 比较的名称。</summary>
    public string Name { get; }

    /// <summary>记录保存的有符号 UTC Unix 毫秒值，不保证单调。</summary>
    public long RecordedAtUnixMs { get; }

    /// <summary>独立元信息声明的成员数量；不表示已经遍历成员表。</summary>
    public int EntryCount { get; }
}
