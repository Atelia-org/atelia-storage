using Atelia.Rbf;

namespace Atelia.HeadStore.Internal.Runtime;

/// <summary>runtime backend 与 owner 共用的成功物理恢复动作台账。</summary>
internal sealed class HeadStoreRecoveryLog {
    private readonly List<HeadStoreRecoveryReport> _entries = [];

    internal HeadStoreRecoveryLog() {
        Reports = new RecoveryReportView(_entries);
    }

    internal IReadOnlyList<HeadStoreRecoveryReport> Reports { get; }

    // Prepare before a writable RBF factory can alter bytes. Single-driver callers
    // append at most one returned report before the next preparation.
    internal void PrepareAppend() => _entries.EnsureCapacity(checked(_entries.Count + 1));

    // The prepared List capacity makes this post-factory append allocation-free.
    internal void Record(HeadStoreRecoveryFileKind kind, uint localId, RbfTailRecoveryReport recovery) {
        if (recovery.Action != RbfTailRecoveryAction.None) {
            _entries.Add(new HeadStoreRecoveryReport(kind, localId, recovery));
        }
    }

    // Retained views own only values, without the log, backend, owner or resource delegates.
    // ReadOnlyCollection would expose its mutable backing through ICollection.SyncRoot.
    private sealed class RecoveryReportView(List<HeadStoreRecoveryReport> entries) : IReadOnlyList<HeadStoreRecoveryReport> {
        private readonly List<HeadStoreRecoveryReport> _entries = entries;

        public int Count => _entries.Count;
        public HeadStoreRecoveryReport this[int index] => _entries[index];

        public IEnumerator<HeadStoreRecoveryReport> GetEnumerator() {
            for (int index = 0; index < _entries.Count; index++) { yield return _entries[index]; }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
