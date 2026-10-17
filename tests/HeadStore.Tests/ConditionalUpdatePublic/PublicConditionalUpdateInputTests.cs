using System.Collections;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.ConditionalUpdatePublic;

public sealed class PublicConditionalUpdateInputTests {
    [Fact]
    public void OnlyActualEnumerationChoosesEmptySingleAndCuPathsWithoutReadingCollectionClaims() {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId a;
        HeadEntryId b;
        HeadEntryId single;
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(store.PublishHeads(new EnumerationOnlyDictionary([]), out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            single = store.PublishHeads(new EnumerationOnlyDictionary([new(a.HeadId, new byte[] { 3 })]), out outcome).Unwrap()[a.HeadId];
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            group = store.PublishHeads(new EnumerationOnlyDictionary([
                new(b.HeadId, new byte[] { 5 }), new(a.HeadId, new byte[] { 4 })
            ]), out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(2, group.Count);
            Assert.NotEqual(single, group[a.HeadId]);
            Assert.Equal(new byte[] { 4 }, store.ReadHead(a.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(new byte[] { 5 }, store.ReadHead(b.HeadId).Unwrap().Payload.ToArray());
        }
        fixture.AssertWire(group[a.HeadId], group, [4], long.MinValue, long.MaxValue);
    }

    [Fact]
    public void SingleMemberCapturesEffectiveSliceBeforeEnumeratorDisposeAndSurvivesBothLayersColdReopen() {
        using var fixture = new ConditionalUpdatePublicFixture();
        byte[] input = [0xa0, 1, 2, 3, 0xb0];
        byte[] expected = [1, 2, 3];
        HeadEntryId published;
        bool disposed = false;
        using (var store = fixture.Create()) {
            HeadEntryId initial = store.CreateHead(7, [0xff], out _).Unwrap();
            var changes = new EnumerationOnlyDictionary([
                new(initial.HeadId, input.AsMemory(1, 3))
            ], onDispose: () => {
                input.AsSpan().Fill(0xcc);
                disposed = true;
            });

            var accepted = store.PublishHeads(changes, out var outcome).Unwrap();

            Assert.True(disposed);
            Assert.All(input, value => Assert.Equal((byte)0xcc, value));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Single(accepted);
            published = accepted[initial.HeadId];
            Assert.NotEqual(initial, published);
            Assert.Equal(expected, store.ReadHead(initial.HeadId).Unwrap().Payload.ToArray());
        }
        fixture.Data.Dispose();
        using var data = DataStore.OpenReadOnly(fixture.DataRoot);
        using var cold = Store.OpenReadOnly(fixture.Root, data);
        HeadEntry entry = cold.ReadHead(published.HeadId).Unwrap();
        Assert.Equal(published, entry.Id);
        Assert.Equal(expected, entry.Payload.ToArray());
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
        HeadEntryId a;
        HeadEntryId b;
        HeadEntryId missing;
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
            missing = store.CreateHead(23, [3], out _).Unwrap();
        }
        File.Delete(fixture.HeadPath(23));
        var entries = new List<KeyValuePair<HeadId, ReadOnlyMemory<byte>>> {
            new(a.HeadId, new byte[] { 4 }), new(b.HeadId, new byte[] { 5 })
        };
        switch (kind) {
            case "empty": entries.Clear(); break;
            case "duplicate": entries.Add(new(a.HeadId, new byte[] { 6 })); break;
            case "default": entries.Add(new(default, new byte[] { 6 })); break;
            case "foreign": entries.Add(new(HeadIdTestExtensions.ForeignHeadId(fixture.Identity, 7), new byte[] { 6 })); break;
            case "zero-root": entries.Add(new(missing.HeadId, ReadOnlyMemory<byte>.Empty)); break;
            case "oversize-root": entries.Add(new(missing.HeadId, new byte[Store.MaxHeadEntryPayloadLength + 1])); break;
            case "missing": entries.Add(new(missing.HeadId, new byte[] { 6 })); break;
        }
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open()) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(store.PublishHeads(new EnumerationOnlyDictionary(entries), out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(a, store.ReadHead(a.HeadId).Unwrap().Id);
            Assert.Equal(b, store.ReadHead(b.HeadId).Unwrap().Id);
        }
        fixture.AssertUnchanged(before);
        using var healthy = fixture.Open();
        healthy.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [7]), (b.HeadId, [8])), out var accepted).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
    }

    [Fact]
    public void IncomingComparerCannotCollapseDifferentHeadKeysOrConcealActualDuplicates() {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var store = fixture.Create();
        HeadEntryId a = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId b = store.CreateHead(19, [2], out _).Unwrap();
        // This comparer deliberately considers any two refs different, including an exact duplicate.
        var changes = new Dictionary<HeadId, ReadOnlyMemory<byte>>(new NeverEqualComparer()) {
            [a.HeadId] = new byte[] { 3 }, [b.HeadId] = new byte[] { 4 }, [a.HeadId] = new byte[] { 5 }
        };
        Assert.Equal(3, changes.Count);
        var rejected = store.PublishHeads(changes, out var outcome);
        Assert.True(rejected.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(a, store.ReadHead(a.HeadId).Unwrap().Id);
        Assert.Equal(b, store.ReadHead(b.HeadId).Unwrap().Id);
        changes = new Dictionary<HeadId, ReadOnlyMemory<byte>>(new NeverEqualComparer()) {
            [a.HeadId] = new byte[] { 6 }, [b.HeadId] = new byte[] { 7 }
        };
        var accepted = store.PublishHeads(changes, out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(2, accepted.Count);
        Assert.Equal(a.HeadId, accepted[a.HeadId].HeadId);
        Assert.Equal(b.HeadId, accepted[b.HeadId].HeadId);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("dispose")]
    public void InputEnumeratorFailurePrecedesOutputAndDoesNotFaultHealthyOwner(string point) {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId a;
        HeadEntryId b;
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
        }
        var before = fixture.CaptureFiles();
        var failure = new IOException("input-" + point);
        using (var store = fixture.Open()) {
            var outcome = PublicationOutcome.Confirmed;
            var changes = new EnumerationOnlyDictionary([
                new(a.HeadId, new byte[] { 3 }), new(b.HeadId, new byte[] { 4 })
            ], point, failure, throwOnGetEnumerator: point == "get");
            Assert.Same(failure, Assert.Throws<IOException>(() => { store.PublishHeads(changes, out outcome); }));
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(a, store.ReadHead(a.HeadId).Unwrap().Id);
            Assert.Equal(b, store.ReadHead(b.HeadId).Unwrap().Id);
        }
        fixture.AssertUnchanged(before);
        using var healthy = fixture.Open();
        healthy.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [7]), (b.HeadId, [8])), out var accepted).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
    }

    [Fact]
    public void EnumeratorScratchReusedOnNextMoveIsCopiedForEachActualEntry() {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var store = fixture.Create();
        HeadEntryId a = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId b = store.CreateHead(19, [2], out _).Unwrap();
        byte[] scratch = [0xa1, 0xa2, 0xa3];
        // A deliberately adversarial sequential enumerator recycles its scratch on MoveNext.
        // This checks immediate per-item capture, without promising concurrent-input entries.
        var changes = new EnumerationOnlyDictionary([
            new(a.HeadId, scratch.AsMemory()), new(b.HeadId, scratch.AsMemory())
        ], onDispose: () => scratch.AsSpan().Clear(), beforeMove: index => {
            if (index == 1) { scratch.AsSpan().Fill(0xb1); }
        });
        var accepted = store.PublishHeads(changes, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(2, accepted.Count);
        Assert.Equal(new byte[] { 0xa1, 0xa2, 0xa3 }, store.ReadHead(a.HeadId).Unwrap().Payload.ToArray());
        Assert.Equal(new byte[] { 0xb1, 0xb1, 0xb1 }, store.ReadHead(b.HeadId).Unwrap().Payload.ToArray());
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void AllEffectiveSlicesAreCapturedBeforeInputEnumeratorDisposeAndReturnedDictionaryHasOwnedReadonlyBacking() {
        using var fixture = new ConditionalUpdatePublicFixture();
        IReadOnlyDictionary<HeadId, HeadEntryId> saved;
        HeadEntryId a;
        HeadEntryId b;
        byte[] input = [0xa0, 1, 2, 3, 4, 5, 0xb0];
        var expectedA = input.AsSpan(1, 3).ToArray();
        var expectedB = input.AsSpan(3, 3).ToArray();
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
            var changes = new EnumerationOnlyDictionary([
                new(a.HeadId, input.AsMemory(1, 3)), new(b.HeadId, input.AsMemory(3, 3))
            ], onDispose: () => input.AsSpan().Fill(0xcc));
            saved = store.PublishHeads(changes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.All(input, value => Assert.Equal((byte)0xcc, value));
            Assert.Equal(expectedA, store.ReadHead(a.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(expectedB, store.ReadHead(b.HeadId).Unwrap().Payload.ToArray());
            AssertReadonlyDictionary(saved, a.HeadId);
            HeadEntryId firstA = saved[a.HeadId];
            HeadEntryId firstB = saved[b.HeadId];
            var later = store.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [9]), (b.HeadId, [10])), out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(firstA, later[a.HeadId]);
            Assert.Equal(firstA, saved[a.HeadId]);
            Assert.Equal(firstB, saved[b.HeadId]);
        }
        Assert.Equal(2, saved.Count);
        Assert.Equal(a.HeadId, saved[a.HeadId].HeadId);
        Assert.Equal(b.HeadId, saved[b.HeadId].HeadId);
    }

    [Fact]
    public void ReadOnlyHistoryTagScanAndDisposedAdmissionInitializeOutcomeBeforeInputAccess() {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId a;
        HeadEntryId b;
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
            store.CreateTag("guard", [a.HeadId], out _).Unwrap();
        }
        var hostile = new EnumerationOnlyDictionary([], throwOnGetEnumerator: true);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.ReadOnly", readOnly.PublishHeads(hostile, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        using var writer = fixture.Open();
        Assert.Equal(HeadHistoryEnd.VisitorStopped, writer.ReadHeadHistory(a.HeadId, ConditionalUpdatePublicFixture.Unlimited, _ => {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.HistoryActive", writer.PublishHeads(hostile, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        }).Unwrap());
        Assert.Equal(HeadSetTagScanEnd.VisitorStopped, writer.ScanTags(_ => {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.TagScanActive", writer.PublishHeads(hostile, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        }).Unwrap());
        writer.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [4]), (b.HeadId, [5])), out var accepted).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
        writer.Dispose();
        var disposedOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => { writer.PublishHeads(hostile, out disposedOutcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, disposedOutcome);
    }

    private static void AssertReadonlyDictionary(IReadOnlyDictionary<HeadId, HeadEntryId> dictionary, HeadId existing) {
        HeadEntryId original = dictionary[existing];
        if (dictionary is IDictionary<HeadId, HeadEntryId> mutable) {
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[existing] = default);
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            Assert.Throws<NotSupportedException>(() => mutable.Remove(existing));
        }
        if (dictionary is ICollection<KeyValuePair<HeadId, HeadEntryId>> entries) {
            Assert.True(entries.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => entries.Clear());
            Assert.Throws<NotSupportedException>(() => entries.Add(new(existing, default)));
        }
        if (dictionary is IDictionary untyped) {
            Assert.True(untyped.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => untyped[existing] = default(HeadEntryId));
            Assert.Throws<NotSupportedException>(() => untyped.Clear());
        }
        if (dictionary is ICollection collection) {
            Assert.False(collection.SyncRoot is IDictionary);
            Assert.False(collection.SyncRoot is KeyValuePair<HeadId, HeadEntryId>[]);
        }
        Assert.Equal(original, dictionary[existing]);
        Assert.Equal(dictionary.Count, dictionary.ToArray().Length);
        Assert.Equal(dictionary.Count, ((IEnumerable)dictionary).Cast<KeyValuePair<HeadId, HeadEntryId>>().Count());
        if (dictionary.Keys is ICollection<HeadId> keys) {
            Assert.True(keys.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => keys.Clear());
        }
        if (dictionary.Values is ICollection<HeadEntryId> values) {
            Assert.True(values.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => values.Clear());
        }
    }

    private sealed class NeverEqualComparer : IEqualityComparer<HeadId> {
        public bool Equals(HeadId x, HeadId y) => false;
        public int GetHashCode(HeadId obj) => 0;
    }

    private sealed class EnumerationOnlyDictionary(
        IReadOnlyList<KeyValuePair<HeadId, ReadOnlyMemory<byte>>> entries,
        string? failurePoint = null, Exception? failure = null, Action? onDispose = null,
        bool throwOnGetEnumerator = false, Action<int>? beforeMove = null) : IReadOnlyDictionary<HeadId, ReadOnlyMemory<byte>> {
        public int Count => throw new InvalidOperationException("Input Count must not be trusted.");
        public IEnumerable<HeadId> Keys => throw new InvalidOperationException("Only input enumeration is authoritative.");
        public IEnumerable<ReadOnlyMemory<byte>> Values => throw new InvalidOperationException("Only input enumeration is authoritative.");
        public ReadOnlyMemory<byte> this[HeadId key] => throw new InvalidOperationException("Only input enumeration is authoritative.");
        public bool ContainsKey(HeadId key) => throw new InvalidOperationException("Input comparer must not be trusted.");
        public bool TryGetValue(HeadId key, out ReadOnlyMemory<byte> value) => throw new InvalidOperationException("Input comparer must not be trusted.");
        public IEnumerator<KeyValuePair<HeadId, ReadOnlyMemory<byte>>> GetEnumerator() => throwOnGetEnumerator
            ? throw (failure ?? new InvalidOperationException("Admission must precede input access."))
            : new Enumerator(entries, failurePoint, failure, onDispose, beforeMove);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(
            IReadOnlyList<KeyValuePair<HeadId, ReadOnlyMemory<byte>>> entries,
            string? failurePoint, Exception? failure, Action? onDispose,
            Action<int>? beforeMove) : IEnumerator<KeyValuePair<HeadId, ReadOnlyMemory<byte>>> {
            private int _index = -1;
            public KeyValuePair<HeadId, ReadOnlyMemory<byte>> Current => failurePoint == "current" && _index == 1
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
