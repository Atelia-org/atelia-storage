using System.Collections;
using Xunit;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.ConditionalUpdatePublic;

public sealed class PublicConditionalUpdateInputTests {
    [Fact]
    public void OnlyActualEnumerationChoosesEmptySingleAndCuPathsWithoutReadingCollectionClaims() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        RefRevision single;
        IReadOnlyDictionary<RefId, RefRevision> group;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(store.PublishRefs(new EnumerationOnlyDictionary([]), out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            single = store.PublishRefs(new EnumerationOnlyDictionary([new(a.RefId, new byte[] { 3 })]), out outcome).Unwrap()[a.RefId];
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            group = store.PublishRefs(new EnumerationOnlyDictionary([
                new(b.RefId, new byte[] { 5 }), new(a.RefId, new byte[] { 4 })
            ]), out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(2, group.Count);
            Assert.NotEqual(single, group[a.RefId]);
            Assert.Equal(new byte[] { 4 }, store.ReadRef(a.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(new byte[] { 5 }, store.ReadRef(b.RefId).Unwrap().Payload.ToArray());
        }
        fixture.AssertWire(group[a.RefId], group, [4], long.MinValue, long.MaxValue);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("default")]
    [InlineData("foreign")]
    [InlineData("zero-root")]
    [InlineData("oversize-root")]
    [InlineData("missing")]
    public void InvalidActualInputIsNotAttemptedAndDoesNotAppendAnyMember(string kind) {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var other = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        RefRevision missing;
        RefRevision foreign;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
            missing = store.CreateRef(23, [3], out _).Unwrap();
        }
        using (var store = other.Create()) { foreign = store.CreateRef(7, [9], out _).Unwrap(); }
        File.Delete(fixture.RefPath(23));
        var entries = new List<KeyValuePair<RefId, ReadOnlyMemory<byte>>> {
            new(a.RefId, new byte[] { 4 }), new(b.RefId, new byte[] { 5 })
        };
        switch (kind) {
            case "empty": entries.Clear(); break;
            case "duplicate": entries.Add(new(a.RefId, new byte[] { 6 })); break;
            case "default": entries.Add(new(default, new byte[] { 6 })); break;
            case "foreign": entries.Add(new(foreign.RefId, new byte[] { 6 })); break;
            case "zero-root": entries.Add(new(missing.RefId, ReadOnlyMemory<byte>.Empty)); break;
            case "oversize-root": entries.Add(new(missing.RefId, new byte[Store.MaxRootPayloadLength + 1])); break;
            case "missing": entries.Add(new(missing.RefId, new byte[] { 6 })); break;
        }
        var before = fixture.Snapshot();
        using (var store = fixture.Open()) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(store.PublishRefs(new EnumerationOnlyDictionary(entries), out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(a, store.ReadRef(a.RefId).Unwrap().Revision);
            Assert.Equal(b, store.ReadRef(b.RefId).Unwrap().Revision);
        }
        fixture.AssertUnchanged(before);
        using var healthy = fixture.Open();
        healthy.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [7]), (b.RefId, [8])), out var accepted).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
    }

    [Fact]
    public void IncomingComparerCannotCollapseDifferentRefKeysOrConcealActualDuplicates() {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var store = fixture.Create();
        RefRevision a = store.CreateRef(7, [1], out _).Unwrap();
        RefRevision b = store.CreateRef(19, [2], out _).Unwrap();
        // This comparer deliberately considers any two refs different, including an exact duplicate.
        var changes = new Dictionary<RefId, ReadOnlyMemory<byte>>(new NeverEqualComparer()) {
            [a.RefId] = new byte[] { 3 }, [b.RefId] = new byte[] { 4 }, [a.RefId] = new byte[] { 5 }
        };
        Assert.Equal(3, changes.Count);
        var rejected = store.PublishRefs(changes, out var outcome);
        Assert.True(rejected.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(a, store.ReadRef(a.RefId).Unwrap().Revision);
        Assert.Equal(b, store.ReadRef(b.RefId).Unwrap().Revision);
        changes = new Dictionary<RefId, ReadOnlyMemory<byte>>(new NeverEqualComparer()) {
            [a.RefId] = new byte[] { 6 }, [b.RefId] = new byte[] { 7 }
        };
        var accepted = store.PublishRefs(changes, out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(2, accepted.Count);
        Assert.Equal(a.RefId, accepted[a.RefId].RefId);
        Assert.Equal(b.RefId, accepted[b.RefId].RefId);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("dispose")]
    public void InputEnumeratorFailurePrecedesOutputAndDoesNotFaultHealthyOwner(string point) {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
        }
        var before = fixture.Snapshot();
        var failure = new IOException("input-" + point);
        using (var store = fixture.Open()) {
            var outcome = PublicationOutcome.Confirmed;
            var changes = new EnumerationOnlyDictionary([
                new(a.RefId, new byte[] { 3 }), new(b.RefId, new byte[] { 4 })
            ], point, failure, throwOnGetEnumerator: point == "get");
            Assert.Same(failure, Assert.Throws<IOException>(() => { store.PublishRefs(changes, out outcome); }));
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(a, store.ReadRef(a.RefId).Unwrap().Revision);
            Assert.Equal(b, store.ReadRef(b.RefId).Unwrap().Revision);
        }
        fixture.AssertUnchanged(before);
        using var healthy = fixture.Open();
        healthy.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [7]), (b.RefId, [8])), out var accepted).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
    }

    [Fact]
    public void EnumeratorScratchReusedOnNextMoveIsCopiedForEachActualEntry() {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var store = fixture.Create();
        RefRevision a = store.CreateRef(7, [1], out _).Unwrap();
        RefRevision b = store.CreateRef(19, [2], out _).Unwrap();
        byte[] scratch = [0xa1, 0xa2, 0xa3];
        // A deliberately adversarial sequential enumerator recycles its scratch on MoveNext.
        // This checks immediate per-item capture, without promising concurrent-input snapshots.
        var changes = new EnumerationOnlyDictionary([
            new(a.RefId, scratch.AsMemory()), new(b.RefId, scratch.AsMemory())
        ], onDispose: () => scratch.AsSpan().Clear(), beforeMove: index => {
            if (index == 1) { scratch.AsSpan().Fill(0xb1); }
        });
        var accepted = store.PublishRefs(changes, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(2, accepted.Count);
        Assert.Equal(new byte[] { 0xa1, 0xa2, 0xa3 }, store.ReadRef(a.RefId).Unwrap().Payload.ToArray());
        Assert.Equal(new byte[] { 0xb1, 0xb1, 0xb1 }, store.ReadRef(b.RefId).Unwrap().Payload.ToArray());
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void AllEffectiveSlicesAreCapturedBeforeInputEnumeratorDisposeAndReturnedDictionaryHasOwnedReadonlyBacking() {
        using var fixture = new ConditionalUpdatePublicFixture();
        IReadOnlyDictionary<RefId, RefRevision> saved;
        RefRevision a;
        RefRevision b;
        byte[] input = [0xa0, 1, 2, 3, 4, 5, 0xb0];
        var expectedA = input.AsSpan(1, 3).ToArray();
        var expectedB = input.AsSpan(3, 3).ToArray();
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
            var changes = new EnumerationOnlyDictionary([
                new(a.RefId, input.AsMemory(1, 3)), new(b.RefId, input.AsMemory(3, 3))
            ], onDispose: () => input.AsSpan().Fill(0xcc));
            saved = store.PublishRefs(changes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.All(input, value => Assert.Equal((byte)0xcc, value));
            Assert.Equal(expectedA, store.ReadRef(a.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(expectedB, store.ReadRef(b.RefId).Unwrap().Payload.ToArray());
            AssertReadonlyDictionary(saved, a.RefId);
            RefRevision firstA = saved[a.RefId];
            RefRevision firstB = saved[b.RefId];
            var later = store.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [9]), (b.RefId, [10])), out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(firstA, later[a.RefId]);
            Assert.Equal(firstA, saved[a.RefId]);
            Assert.Equal(firstB, saved[b.RefId]);
        }
        Assert.Equal(2, saved.Count);
        Assert.Equal(a.RefId, saved[a.RefId].RefId);
        Assert.Equal(b.RefId, saved[b.RefId].RefId);
    }

    [Fact]
    public void ReadOnlyHistoryTagScanAndDisposedAdmissionInitializeOutcomeBeforeInputAccess() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
            store.CreateTag("guard", [3], out _).Unwrap();
        }
        var hostile = new EnumerationOnlyDictionary([], throwOnGetEnumerator: true);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.ReadOnly", readOnly.PublishRefs(hostile, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        using var writer = fixture.Open();
        Assert.Equal(HistoryEnd.VisitorStopped, writer.ReadRefHistory(a.RefId, ConditionalUpdatePublicFixture.Unlimited, _ => {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.HistoryActive", writer.PublishRefs(hostile, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        }).Unwrap());
        Assert.Equal(TagScanEnd.VisitorStopped, writer.ScanTags(_ => {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.TagScanActive", writer.PublishRefs(hostile, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        }).Unwrap());
        writer.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [4]), (b.RefId, [5])), out var accepted).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
        writer.Dispose();
        var disposedOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => { writer.PublishRefs(hostile, out disposedOutcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, disposedOutcome);
    }

    private static void AssertReadonlyDictionary(IReadOnlyDictionary<RefId, RefRevision> dictionary, RefId existing) {
        RefRevision original = dictionary[existing];
        if (dictionary is IDictionary<RefId, RefRevision> mutable) {
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[existing] = default);
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            Assert.Throws<NotSupportedException>(() => mutable.Remove(existing));
        }
        if (dictionary is ICollection<KeyValuePair<RefId, RefRevision>> entries) {
            Assert.True(entries.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => entries.Clear());
            Assert.Throws<NotSupportedException>(() => entries.Add(new(existing, default)));
        }
        if (dictionary is IDictionary untyped) {
            Assert.True(untyped.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => untyped[existing] = default(RefRevision));
            Assert.Throws<NotSupportedException>(() => untyped.Clear());
        }
        if (dictionary is ICollection collection) {
            Assert.False(collection.SyncRoot is IDictionary);
            Assert.False(collection.SyncRoot is KeyValuePair<RefId, RefRevision>[]);
        }
        Assert.Equal(original, dictionary[existing]);
        Assert.Equal(dictionary.Count, dictionary.ToArray().Length);
        Assert.Equal(dictionary.Count, ((IEnumerable)dictionary).Cast<KeyValuePair<RefId, RefRevision>>().Count());
        if (dictionary.Keys is ICollection<RefId> keys) {
            Assert.True(keys.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => keys.Clear());
        }
        if (dictionary.Values is ICollection<RefRevision> values) {
            Assert.True(values.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => values.Clear());
        }
    }

    private sealed class NeverEqualComparer : IEqualityComparer<RefId> {
        public bool Equals(RefId x, RefId y) => false;
        public int GetHashCode(RefId obj) => 0;
    }

    private sealed class EnumerationOnlyDictionary(
        IReadOnlyList<KeyValuePair<RefId, ReadOnlyMemory<byte>>> entries,
        string? failurePoint = null, Exception? failure = null, Action? onDispose = null,
        bool throwOnGetEnumerator = false, Action<int>? beforeMove = null) : IReadOnlyDictionary<RefId, ReadOnlyMemory<byte>> {
        public int Count => throw new InvalidOperationException("Input Count must not be trusted.");
        public IEnumerable<RefId> Keys => throw new InvalidOperationException("Only input enumeration is authoritative.");
        public IEnumerable<ReadOnlyMemory<byte>> Values => throw new InvalidOperationException("Only input enumeration is authoritative.");
        public ReadOnlyMemory<byte> this[RefId key] => throw new InvalidOperationException("Only input enumeration is authoritative.");
        public bool ContainsKey(RefId key) => throw new InvalidOperationException("Input comparer must not be trusted.");
        public bool TryGetValue(RefId key, out ReadOnlyMemory<byte> value) => throw new InvalidOperationException("Input comparer must not be trusted.");
        public IEnumerator<KeyValuePair<RefId, ReadOnlyMemory<byte>>> GetEnumerator() => throwOnGetEnumerator
            ? throw (failure ?? new InvalidOperationException("Admission must precede input access."))
            : new Enumerator(entries, failurePoint, failure, onDispose, beforeMove);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(
            IReadOnlyList<KeyValuePair<RefId, ReadOnlyMemory<byte>>> entries,
            string? failurePoint, Exception? failure, Action? onDispose,
            Action<int>? beforeMove) : IEnumerator<KeyValuePair<RefId, ReadOnlyMemory<byte>>> {
            private int _index = -1;
            public KeyValuePair<RefId, ReadOnlyMemory<byte>> Current => failurePoint == "current" && _index == 1
                ? throw failure! : entries[_index];
            object IEnumerator.Current => Current;
            public bool MoveNext() {
                if (failurePoint == "move" && _index == 0) { throw failure!; }
                beforeMove?.Invoke(_index + 1);
                return ++_index < entries.Count;
            }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() {
                onDispose?.Invoke();
                if (failurePoint == "dispose") { throw failure!; }
            }
        }
    }
}
