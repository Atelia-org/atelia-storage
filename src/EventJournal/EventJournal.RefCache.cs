namespace Atelia.EventJournal;

public sealed partial class EventJournal {
    private sealed record RefEntry(RefState State, RefMoveStore Store);
    private readonly Dictionary<RefId, LinkedListNode<RefEntry>> _refEntries = new();
    private readonly LinkedList<RefEntry> _refLru = new();

    internal int RetainedRefEntryCount => _refEntries.Count;
    internal RefMoveStore? PeekRefStore(RefId refId) =>
        _refEntries.TryGetValue(refId, out var node) ? node.Value.Store : null;

    private bool TryGetRefEntry(RefId refId, out RefEntry entry) {
        if (_refEntries.TryGetValue(refId, out var node)) {
            _refLru.Remove(node);
            _refLru.AddFirst(node);
            entry = node.Value;
            return true;
        }
        entry = null!;
        return false;
    }

    // A use owns only a newly opened store. Cached stores never carry a live writer lease.
    private RefStoreUse OpenRefStore(RefId refId) {
        if (TryGetRefEntry(refId, out var cached)) { return new RefStoreUse(cached.Store, owned: false); }
        return new RefStoreUse(_isReadOnly
            ? RefMoveStore.OpenReadOnlyExisting(_refObjectsPath, refId, _options.RefSegmentStoreOptions)
            : RefMoveStore.OpenExisting(_refObjectsPath, refId, _options.RefSegmentStoreOptions), owned: true);
    }

    private void RetainRef(RefState state, RefStoreUse use) {
        if (_options.RefStoreCacheCapacity == 0) { return; }
        if (_refEntries.TryGetValue(state.RefId, out var existing)) {
            existing.Value = new RefEntry(state, existing.Value.Store);
            _refLru.Remove(existing);
            _refLru.AddFirst(existing);
            return;
        }
        // Release the least recently used handle before installing its replacement.
        if (_refEntries.Count == _options.RefStoreCacheCapacity) {
            RemoveRefEntry(_refLru.Last!.Value.State.RefId);
        }
        var node = new LinkedListNode<RefEntry>(new RefEntry(state, use.Store));
        _refEntries.Add(state.RefId, node);
        _refLru.AddFirst(node);
        use.TransferOwnership();
    }

    private void RemoveRefEntry(RefId refId) {
        if (!_refEntries.Remove(refId, out var node)) { return; }
        _refLru.Remove(node);
        node.Value.Store.Dispose();
    }

    private sealed class RefStoreUse(RefMoveStore store, bool owned) : IDisposable {
        internal RefMoveStore Store { get; } = store;
        private bool _owned = owned;
        internal void TransferOwnership() => _owned = false;
        public void Dispose() {
            if (!_owned) { return; }
            _owned = false;
            Store.Dispose();
        }
    }
}
