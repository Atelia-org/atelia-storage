using System.Buffers;
using System.Text;
using Atelia.Data;
using Atelia.FrameStore;
using Atelia.Rbf;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Versions = Atelia.VersionStore.VersionStore;

string directory = Path.GetFullPath(args.Length == 1 ? args[0]
    : throw new ArgumentException("Expected a fresh VersionStore smoke directory."));
if (Directory.Exists(directory) || File.Exists(directory)) {
    throw new IOException("VersionStore smoke directory must be fresh.");
}
Directory.CreateDirectory(directory);
string dataPath = Path.Combine(directory, "data");
string versionsPath = Path.Combine(directory, "versions");
const string relatedTag = "selected/历史😀";
const string opaqueTag = "opaque/bytes";
byte[] opaqueInitialBytes = [0xFF, 0x00, 0xC0, 0x80, 0x01];
byte[] opaqueLatestBytes = [0x00, 0xFF, 0xFE, 0x41, 0x00, 0x42];
byte[] dataIdentity;
byte[] versionsIdentity;
RootSet initialRoots;
RootSet latestRoots;
RefRevision initial;
RefRevision selectedRevision;
RefRevision latest;
RefRevision opaqueInitial;
RefRevision opaqueLatest;
RefRevision reservedFork;
RefRevision automaticFork;
RefBytesSnapshot selected;
RootPayload retainedTag;
TagInfo retainedInfo;
FrameAddress afterDispose;
using (var data = DataStore.Create(dataPath, rotationThresholdBytes: 128)) {
    dataIdentity = data.StoreId.ToArray();
    using (var versions = Versions.Create(versionsPath, data, reservedRefIdUpperBound: 2)) {
        versionsIdentity = versions.VersionStoreId.ToArray();
        Require(!versions.IsReadOnly && versions.ReservedRefIdUpperBound == 2, "Unexpected Create mode or reserved range.");
        initialRoots = WriteRoots(data, generation: 0);
        // No application data flush precedes these calls: publication performs the data-first barrier.
        initial = versions.CreateRef(1, initialRoots.Payload, out var outcome).Unwrap();
        Confirmed(outcome);
        opaqueInitial = versions.CreateRef(opaqueInitialBytes, out outcome).Unwrap();
        Confirmed(outcome);
        Require(opaqueInitial.RefId.LocalId == 3, "Automatic allocation entered the reserved range.");
        selectedRevision = versions.PublishRef(initial.RefId, initialRoots.Payload, out outcome).Unwrap();
        Confirmed(outcome);
        Require(selectedRevision != initial, "Equal bytes must retain distinct publication revisions.");
        latestRoots = WriteRoots(data, generation: 1);
        latest = versions.PublishRef(initial.RefId, latestRoots.Payload, out outcome).Unwrap();
        Confirmed(outcome);

        // Select a nonhead revision through the bounded history API, then fork after its callback ends.
        int visited = 0;
        RefBytesSnapshot? candidate = null;
        HistoryEnd end = versions.ReadRefHistory(initial.RefId, new HistoryBudget(16, 128), snapshot => {
            visited++;
            if (visited == 2) { candidate = snapshot; return false; }
            return true;
        }).Unwrap();
        selected = candidate ?? throw new InvalidDataException("History did not deliver the selected old revision.");
        Require(end == HistoryEnd.VisitorStopped && visited == 2 && selected.Revision == selectedRevision,
            "History selected a different revision or failed to stop normally.");
        Require(selected.Payload.SequenceEqual(initialRoots.Payload), "The exact old point changed bytes.");
        reservedFork = versions.ForkRef(2, selected.Revision, out outcome).Unwrap();
        Confirmed(outcome);
        automaticFork = versions.ForkRef(selected.Revision, out outcome).Unwrap();
        Confirmed(outcome);
        Require(reservedFork.RefId.LocalId == 2 && automaticFork.RefId.LocalId == 4, "Fork allocation changed the reserved/automatic domains.");
        Require(versions.ReadRef(initial.RefId).Unwrap().Revision == latest, "Fork changed the source head.");
        VerifyForks(versions, selectedRevision, initial, reservedFork, automaticFork);
        VerifyForkHistory(versions, reservedFork, selectedRevision, initial, initialRoots.Payload);
        VerifyForkHistory(versions, automaticFork, selectedRevision, initial, initialRoots.Payload);

        Require(versions.CreateTag(relatedTag, selected.Payload, selected.Revision, out outcome).Unwrap(), "Related tag creation failed.");
        Confirmed(outcome);
        Require(versions.CreateTag(opaqueTag, opaqueInitialBytes, out outcome).Unwrap(), "Anonymous opaque tag creation failed.");
        Confirmed(outcome);
        opaqueLatest = versions.PublishRef(opaqueInitial.RefId, opaqueLatestBytes, out outcome).Unwrap();
        Confirmed(outcome);
        Require(versions.ReadRef(opaqueInitial.RefId).Unwrap().Payload.SequenceEqual(opaqueLatestBytes), "Opaque root bytes were interpreted or changed.");
        var duplicate = versions.CreateTag(relatedTag, latestRoots.Payload, out outcome);
        Require(duplicate.IsFailure && duplicate.Error!.ErrorCode == "VersionStore.DuplicateTagName"
            && outcome == PublicationOutcome.NotAttempted, "An immutable tag was overwritten or attempted publication.");
        retainedTag = versions.ResolveTag(relatedTag).Unwrap();
        retainedInfo = versions.ReadTagInfo(relatedTag).Unwrap();
        Require(retainedTag.Bytes.SequenceEqual(selected.Payload), "The related tag changed after newer publication.");
        VerifyTags(versions, relatedTag, opaqueTag, selectedRevision, initialRoots.Payload, opaqueInitialBytes);
        VerifyRoots(data, versions.ReadRef(initial.RefId).Unwrap().Payload, latestRoots);
        VerifyRoots(data, retainedTag.Bytes, initialRoots);
    }
    // The caller owns borrowed data. Closing VS leaves it usable for further work.
    data.EnsureUsable();
    afterDispose = data.Append(99, "after-versionstore-dispose"u8).Unwrap();
    data.ConfirmDurable();
}
Require(selected.Revision == selectedRevision && selected.Payload.SequenceEqual(retainedTag.Bytes),
    "Owned snapshot/tag bytes depend on either disposed owner.");
VerifyRelated(retainedInfo, relatedTag, selectedRevision);
Require(RootMapCodec.Decode(retainedTag.Bytes).Unwrap()["工作区/😀"] == initialRoots.Roots["工作区/😀"],
    "Owned decoded addresses changed after Dispose.");

VersionStoreAudit audit;
long dataFrames;
using (var data = DataStore.OpenReadOnly(dataPath)) {
    using (var versions = Versions.OpenReadOnly(versionsPath, data)) {
        Require(data.IsReadOnly && versions.IsReadOnly, "Cold verification must use read-only owners.");
        Require(data.StoreId.SequenceEqual(dataIdentity) && versions.VersionStoreId.SequenceEqual(versionsIdentity),
            "Cold reopen changed a persisted identity.");
        Require(versions.ListRefs().Unwrap().Select(id => id.LocalId).Order().SequenceEqual(new uint[] { 1, 2, 3, 4 }),
            "Cold discovery changed the formal ref set.");
        RefBytesSnapshot current = versions.ReadRef(initial.RefId).Unwrap();
        Require(current.Revision == latest, "Cold ReadRef returned a different current revision.");
        VerifyRoots(data, current.Payload, latestRoots);
        List<RefBytesSnapshot> history = History(versions, initial.RefId);
        Require(history.Select(snapshot => snapshot.Revision).SequenceEqual(new[] { latest, selectedRevision, initial }),
            "Cold history lost or merged exact revisions.");
        VerifyRoots(data, history[1].Payload, initialRoots);
        VerifyForks(versions, selectedRevision, initial, reservedFork, automaticFork);
        VerifyForkHistory(versions, reservedFork, selectedRevision, initial, initialRoots.Payload);
        VerifyForkHistory(versions, automaticFork, selectedRevision, initial, initialRoots.Payload);
        VerifyRoots(data, versions.ReadRef(reservedFork.RefId).Unwrap().Payload, initialRoots);
        VerifyRoots(data, versions.ReadRef(automaticFork.RefId).Unwrap().Payload, initialRoots);
        RefBytesSnapshot opaque = versions.ReadRef(opaqueInitial.RefId).Unwrap();
        Require(opaque.Revision == opaqueLatest && opaque.Payload.SequenceEqual(opaqueLatestBytes), "Cold opaque root bytes changed.");
        Require(History(versions, opaqueInitial.RefId).Select(snapshot => snapshot.Revision)
            .SequenceEqual(new[] { opaqueLatest, opaqueInitial }), "Cold opaque history changed.");
        VerifyTags(versions, relatedTag, opaqueTag, selectedRevision, initialRoots.Payload, opaqueInitialBytes);
        VerifyRoots(data, versions.ResolveTag(relatedTag).Unwrap().Bytes, initialRoots);
        audit = versions.Audit().Unwrap();
        Require(audit.RefCount == 4 && audit.SnapshotCount == 7 && audit.ForkCount == 2 && audit.TagCount == 2
            && audit.TagBucketCount is >= 1 and <= 2, "Complete read-only Audit returned unexpected physical totals.");
    }
    data.EnsureUsable();
    using var extra = data.ReadFrame(afterDispose).Unwrap();
    Require(extra.Tag == 99 && extra.PayloadAndMeta.SequenceEqual("after-versionstore-dispose"u8),
        "Borrowed data stopped working after VersionStore Dispose.");
    // VS Audit does not traverse data: explicitly read all referenced roots above and audit data here.
    dataFrames = data.Audit(_ => { }).Unwrap();
    Require(dataFrames == 5, "Data Audit did not cover the actual data frames.");
}
VersionStoreAudit batchAudit = VerifyBatchPublication(directory);
VerifyReservableWriters();
Console.WriteLine($"VersionStore public package smoke passed: {audit.RefCount} refs, {audit.SnapshotCount} snapshots, {audit.ForkCount} forks, {audit.TagCount} tags, {dataFrames} data frames; RootMap/opaque publish, exact history, immutable tags and cold read-only Audit.");
Console.WriteLine($"VersionStore package increments passed: {batchAudit.RefCount} isolated refs, {batchAudit.SnapshotCount} snapshots; single/CU PublishRefs, exact ReadRevision, both-layer cold reopen, EnsureUsable, healthy RecoveryReports and transitive Data borrow/cancel.");
Console.WriteLine($"Retained smoke files: {directory}");

static VersionStoreAudit VerifyBatchPublication(string directory) {
    string dataPath = Path.Combine(directory, "batch-data");
    string versionsPath = Path.Combine(directory, "batch-versions");
    byte[] initialABytes = [0xa0];
    byte[] initialBBytes = [0xb0];
    byte[] singleBytes = [0xa1, 0x00, 0xff];
    byte[] cuABytes = [0xa2, 0x80];
    byte[] cuBBytes = [0xb2, 0x00, 0x7f, 0xff];
    byte[] latestABytes = [0xa3, 0xfe];
    var expected = new Dictionary<RefRevision, byte[]>();
    RefRevision single;
    RefRevision cuA;
    RefRevision cuB;
    RefRevision latestA;
    IReadOnlyList<VersionStoreRecoveryReport> retainedReports;
    using (var data = DataStore.Create(dataPath))
    using (var versions = Versions.Create(versionsPath, data)) {
        versions.EnsureUsable();
        retainedReports = versions.RecoveryReports;
        Require(retainedReports.Count == 0, "Fresh VersionStore reported physical recovery.");
        RefRevision initialA = versions.CreateRef(initialABytes, out var outcome).Unwrap();
        Confirmed(outcome);
        RefRevision initialB = versions.CreateRef(initialBBytes, out outcome).Unwrap();
        Confirmed(outcome);

        byte[] singleInput = [0xf0, 0xa1, 0x00, 0xff, 0xf1];
        var singlePublication = versions.PublishRefs(new Dictionary<RefId, ReadOnlyMemory<byte>> {
            [initialA.RefId] = singleInput.AsMemory(1, 3)
        }, out outcome).Unwrap();
        Confirmed(outcome);
        Require(singlePublication.Count == 1, "Single-member PublishRefs returned a different member set.");
        single = singlePublication[initialA.RefId];
        singleInput.AsSpan().Fill(0xcc);

        var cuPublication = versions.PublishRefs(new Dictionary<RefId, ReadOnlyMemory<byte>> {
            [initialB.RefId] = cuBBytes,
            [initialA.RefId] = cuABytes
        }, out outcome).Unwrap();
        Confirmed(outcome);
        Require(cuPublication.Count == 2, "CU PublishRefs returned a partial or different member set.");
        cuA = cuPublication[initialA.RefId];
        cuB = cuPublication[initialB.RefId];
        Require(cuA != single && cuB != initialB, "CU reused an earlier publication revision.");
        Require(versions.ReadRef(cuA.RefId).Unwrap().Revision == cuA
            && versions.ReadRef(cuB.RefId).Unwrap().Revision == cuB, "CU did not install both member heads.");

        // Cover one CU member with a later ordinary Snapshot; the old complete group remains readable.
        var latestPublication = versions.PublishRefs(new Dictionary<RefId, ReadOnlyMemory<byte>> {
            [initialA.RefId] = latestABytes
        }, out outcome).Unwrap();
        Confirmed(outcome);
        Require(latestPublication.Count == 1, "The later single-member publication changed its member set.");
        latestA = latestPublication[initialA.RefId];
        Require(latestA != cuA, "A later single-member publication reused its CU revision.");
        expected.Add(initialA, initialABytes);
        expected.Add(initialB, initialBBytes);
        expected.Add(single, singleBytes);
        expected.Add(cuA, cuABytes);
        expected.Add(cuB, cuBBytes);
        expected.Add(latestA, latestABytes);
        VerifyBatchReads(versions, expected, latestA, cuB);
        Require(retainedReports.Count == 0, "Healthy batch publication unexpectedly recovered a tail.");
    }
    Require(retainedReports.Count == 0, "The saved recovery view became unreadable or changed after Dispose.");
    VerifyPublicationKind(versionsPath, single, 1);
    VerifyPublicationKind(versionsPath, cuA, 2);
    VerifyPublicationKind(versionsPath, cuB, 2);
    VerifyPublicationKind(versionsPath, latestA, 1);

    // Both layers are new owners on each reopen; none of these reads use the original cache.
    using (var data = DataStore.Open(dataPath))
    using (var versions = Versions.Open(versionsPath, data)) {
        VerifyBatchReads(versions, expected, latestA, cuB);
        Require(versions.RecoveryReports.Count == 0, "Healthy writable reopen unexpectedly repaired a ref.");
    }
    using (var data = DataStore.OpenReadOnly(dataPath))
    using (var versions = Versions.OpenReadOnly(versionsPath, data)) {
        VerifyBatchReads(versions, expected, latestA, cuB);
        VersionStoreAudit audit = versions.Audit().Unwrap();
        Require(audit.RefCount == 2 && audit.SnapshotCount == 6 && audit.ForkCount == 0
            && audit.TagCount == 0 && audit.TagBucketCount == 0, "Isolated batch Audit returned unexpected totals.");
        Require(versions.RecoveryReports.Count == 0, "Read-only batch verification reported a repair.");
        return audit;
    }
}

static void VerifyBatchReads(Versions versions, IReadOnlyDictionary<RefRevision, byte[]> expected,
    RefRevision latestA, RefRevision latestB) {
    versions.EnsureUsable();
    foreach (var entry in expected) {
        RefBytesSnapshot exact = versions.ReadRevision(entry.Key).Unwrap();
        Require(exact.Revision == entry.Key && exact.Payload.SequenceEqual(entry.Value),
            "ReadRevision changed an exact old or current publication.");
    }
    foreach (RefRevision revision in new[] { latestA, latestB }) {
        RefBytesSnapshot current = versions.ReadRef(revision.RefId).Unwrap();
        Require(current.Revision == revision && current.Payload.SequenceEqual(expected[revision]),
            "ReadRef disagrees with the expected current batch member.");
    }
}

static void VerifyPublicationKind(string versionsPath, RefRevision revision, uint expectedTag) {
    string refPath = Path.Combine(versionsPath, "refs", $"{revision.RefId.LocalId:x8}.rbf");
    using var file = RbfFile.OpenReadOnlyExisting(refPath, RbfCacheMode.Off);
    using var frame = file.ReadPooledFrame(revision.Ticket).Unwrap();
    Require(frame.Ticket == revision.Ticket && frame.Tag == expectedTag && frame.TailMetaLength == 8
        && !frame.IsTombstone, "PublishRefs chose a different physical Snapshot/CU record kind.");
}

static void VerifyReservableWriters() {
    foreach (bool useSink in new[] { false, true }) {
        var output = new ArrayBufferWriter<byte>();
        IReservableBufferWriter writer = CreateReservableWriter(useSink, output);
        using (var lifetime = (IDisposable)writer) {
            writer.ReserveSpan(4, out int token).Fill(0x11);
            Span<byte> borrowed = writer.GetMemory(4).Span;
            borrowed[..4].Fill(0x22);
            writer.Commit(token);
            Require(writer.Length == 4 && output.WrittenSpan.SequenceEqual(new byte[] { 0x11, 0x11, 0x11, 0x11 }),
                "Commit included bytes from an unadvanced ordinary borrow.");
            // Commit releases the reservation, while this independent ordinary borrow stays writable.
            borrowed[..4].Fill(0x33);
            writer.Advance(4);
            Require(writer.Length == 8 && output.WrittenSpan.SequenceEqual(new byte[] { 0x11, 0x11, 0x11, 0x11, 0x33, 0x33, 0x33, 0x33 }),
                "Commit invalidated the ordinary borrow before its matching Advance.");
        }

        var nestedOutput = new ArrayBufferWriter<byte>();
        IReservableBufferWriter inner = CreateReservableWriter(useSink, nestedOutput);
        using (var innerLifetime = (IDisposable)inner)
        using (var outer = new ChunkedReservableWriter(inner)) {
            outer.GetSpan(4)[..4].Fill(0x99);
            outer.Advance(0);
            Require(outer.Length == 0 && inner.Length == 0 && nestedOutput.WrittenCount == 0,
                "Nested passthrough cancellation published abandoned bytes.");
            // A strict inner writer must accept a new borrow after the outer layer cancels its old one.
            outer.GetMemory(4).Span[..4].Fill(0x45);
            outer.Advance(4);
            Require(outer.Length == 4 && inner.Length == 4
                && nestedOutput.WrittenSpan.SequenceEqual(new byte[] { 0x45, 0x45, 0x45, 0x45 }),
                "Nested Advance(0) left the inner writer with an outstanding borrow.");
        }

        foreach (bool disposeOuter in new[] { false, true }) {
            var lifecycleOutput = new ArrayBufferWriter<byte>();
            IReservableBufferWriter lifecycleInner = CreateReservableWriter(useSink, lifecycleOutput);
            using (var innerLifetime = (IDisposable)lifecycleInner)
            using (var outer = new ChunkedReservableWriter(lifecycleInner)) {
                lifecycleInner.GetSpan(2)[..2].Fill(0x21);
                lifecycleInner.Advance(2);
                outer.GetMemory(4).Span[..4].Fill(0x99);
                if (disposeOuter) { outer.Dispose(); }
                else { outer.Reset(); }
                // Ending the outer borrow must preserve the borrowed inner and its committed prefix.
                lifecycleInner.GetSpan(2)[..2].Fill(0x45);
                lifecycleInner.Advance(2);
                Require(lifecycleInner.Length == 4
                    && lifecycleOutput.WrittenSpan.SequenceEqual(new byte[] { 0x21, 0x21, 0x45, 0x45 }),
                    "Outer Reset/Dispose failed to cancel its borrow or altered the borrowed inner's output.");
            }
        }
    }
}

static IReservableBufferWriter CreateReservableWriter(bool useSink, ArrayBufferWriter<byte> output) => useSink
    ? new SinkReservableWriter(new CollectingSink(output))
    : new ChunkedReservableWriter(output);

static RootSet WriteRoots(DataStore data, int generation) {
    var roots = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
    foreach (string key in new[] { "entry", "工作区/😀" }) {
        roots.Add(key, data.Append(41, EncodeData(key, generation), [(byte)generation]).Unwrap());
    }
    return new(RootMapCodec.Encode(roots).Unwrap(), roots, generation);
}

static byte[] EncodeData(string key, int generation) => Encoding.UTF8.GetBytes($"{key}:{generation}");

static void VerifyRoots(DataStore data, ReadOnlySpan<byte> payload, RootSet expected) {
    IReadOnlyDictionary<string, FrameAddress> roots = RootMapCodec.Decode(payload).Unwrap();
    Require(roots.Count == expected.Roots.Count, "RootMap changed its root count.");
    foreach (var entry in expected.Roots) {
        Require(roots.TryGetValue(entry.Key, out FrameAddress address) && address == entry.Value,
            "RootMap changed an Ordinal key or frame address.");
        using var frame = data.ReadFrame(address).Unwrap();
        byte[] body = EncodeData(entry.Key, expected.Generation).Concat(new[] { (byte)expected.Generation }).ToArray();
        Require(frame.Tag == 41 && frame.TailMetaLength == 1 && !frame.IsTombstone
            && frame.PayloadAndMeta.SequenceEqual(body), "A published root does not read the actual expected data bytes.");
    }
}

static List<RefBytesSnapshot> History(Versions versions, RefId refId) {
    var snapshots = new List<RefBytesSnapshot>();
    HistoryEnd end = versions.ReadRefHistory(refId, new HistoryBudget(16, 128), snapshot => {
        snapshots.Add(snapshot);
        return true;
    }).Unwrap();
    Require(end == HistoryEnd.Complete, "Bounded history did not reach a complete source prefix.");
    return snapshots;
}

static void VerifyForkHistory(Versions versions, RefRevision fork, RefRevision selected, RefRevision initial, byte[] payload) {
    List<RefBytesSnapshot> history = History(versions, fork.RefId);
    Require(history.Select(snapshot => snapshot.Revision).SequenceEqual(new[] { fork, selected, initial }),
        "Fork inherited a different source point or included later source history.");
    Require(history.All(snapshot => snapshot.Payload.SequenceEqual(payload)), "Fork history changed exact source bytes.");
}

static void VerifyForks(Versions versions, RefRevision selected, RefRevision initial, params RefRevision[] forks) {
    IReadOnlyList<ForkInfo> graph = versions.ListForks(4).Unwrap();
    Require(graph.Count == forks.Length, "ListForks changed the declaration count.");
    foreach (RefRevision fork in forks) {
        ForkInfo edge = graph.Single(info => info.ChildRefId == fork.RefId);
        Require(edge.SourceRefId == initial.RefId && edge.SourceSnapshotTicket == selected.Ticket,
            "ListForks changed the complete exact source coordinate.");
    }
}

static void VerifyTags(Versions versions, string relatedName, string opaqueName, RefRevision selected,
    byte[] relatedBytes, byte[] opaqueBytes) {
    Require(versions.ResolveTag(relatedName).Unwrap().Bytes.SequenceEqual(relatedBytes), "Related immutable tag bytes changed.");
    Require(versions.ResolveTag(opaqueName).Unwrap().Bytes.SequenceEqual(opaqueBytes), "Anonymous immutable tag bytes changed with its ref.");
    VerifyRelated(versions.ReadTagInfo(relatedName).Unwrap(), relatedName, selected);
    var scanned = new Dictionary<string, TagInfo>(StringComparer.Ordinal);
    TagScanEnd end = versions.ScanTags(info => {
        Require(scanned.TryAdd(info.Name, info), "ScanTags repeated a tag name.");
        return true;
    }).Unwrap();
    Require(end == TagScanEnd.Complete && scanned.Count == 2, "Tag metadata scan was incomplete.");
    VerifyRelated(scanned[relatedName], relatedName, selected);
    TagInfo anonymous = versions.ReadTagInfo(opaqueName).Unwrap();
    Require(anonymous.Name == opaqueName && anonymous.RelatedRefId is null && anonymous.RelatedTicket is null
        && scanned[opaqueName].RelatedRefId is null && scanned[opaqueName].RelatedTicket is null,
        "Anonymous tag acquired a related coordinate.");
}

static void VerifyRelated(TagInfo info, string name, RefRevision revision) {
    Require(info.Name == name && info.RelatedRefId == revision.RefId && info.RelatedTicket == revision.Ticket,
        "Tag metadata changed its optional related coordinate.");
}

static void Confirmed(PublicationOutcome outcome) {
    Require(outcome == PublicationOutcome.Confirmed, "Successful publication did not report Confirmed.");
}

static void Require(bool condition, string message) {
    if (!condition) { throw new InvalidDataException(message); }
}

internal sealed record RootSet(byte[] Payload, IReadOnlyDictionary<string, FrameAddress> Roots, int Generation);

internal sealed class CollectingSink(ArrayBufferWriter<byte> output) : IByteSink {
    public void Push(ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }
}
