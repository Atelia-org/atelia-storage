using System.Collections;

namespace Atelia.HeadStore;

/// <summary>不可变的具名 Head 成员表；成员位置不随后续发布移动，可跨 owner 生命周期保存。</summary>
public sealed class HeadSetTag {
    // 调用方移交已校验的私有字典；移交后不得保留可写别名。
    internal HeadSetTag(string name, long recordedAtUnixMs, Dictionary<HeadId, HeadEntryId> ownedEntries) {
        Name = name;
        RecordedAtUnixMs = recordedAtUnixMs;
        Entries = new EntryDictionary(ownedEntries);
    }

    /// <summary>按 Ordinal 比较的永久名称。</summary>
    public string Name { get; }

    /// <summary>记录保存的有符号 UTC Unix 毫秒值，不保证单调。</summary>
    public long RecordedAtUnixMs { get; }

    /// <summary>所选 HeadId 到实际已接受 HeadEntryId 的固定映射；多个 Head 可引用同一 Entry，不承诺枚举顺序。</summary>
    /// <remarks>成员表通过完整记录校验，不表示每个成员当前仍健康；可按需调用 ReadEntry。</remarks>
    public IReadOnlyDictionary<HeadId, HeadEntryId> Entries { get; }

    // 仅实现只读接口；Keys/Values 不暴露 Dictionary 集合的 ICollection.SyncRoot。
    private sealed class EntryDictionary(Dictionary<HeadId, HeadEntryId> entries)
        : IReadOnlyDictionary<HeadId, HeadEntryId> {
        public int Count => entries.Count;
        public HeadEntryId this[HeadId key] => entries[key];
        public bool ContainsKey(HeadId key) => entries.ContainsKey(key);
        public bool TryGetValue(HeadId key, out HeadEntryId value) => entries.TryGetValue(key, out value);
        public IEnumerable<HeadId> Keys {
            get { foreach (var pair in entries) { yield return pair.Key; } }
        }
        public IEnumerable<HeadEntryId> Values {
            get { foreach (var pair in entries) { yield return pair.Value; } }
        }
        public IEnumerator<KeyValuePair<HeadId, HeadEntryId>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
