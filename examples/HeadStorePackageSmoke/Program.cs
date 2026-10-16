using System.Buffers;
using System.Text;
using Atelia.Data;
using Atelia.FrameStore;
using Atelia.Rbf;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

string directory = Path.GetFullPath(args.Length == 1 ? args[0]
    : throw new ArgumentException("Expected a fresh HeadStore smoke directory."));
if (Directory.Exists(directory) || File.Exists(directory)) {
    throw new IOException("HeadStore smoke directory must be fresh.");
}
Directory.CreateDirectory(directory);
string dataPath = Path.Combine(directory, "data");
string headsPath = Path.Combine(directory, "heads");
const string relatedTag = "selected/历史😀";
const string opaqueTag = "opaque/bytes";
byte[] opaqueInitialBytes = [0xFF, 0x00, 0xC0, 0x80, 0x01];
byte[] opaqueLatestBytes = [0x00, 0xFF, 0xFE, 0x41, 0x00, 0x42];
byte[] dataIdentity;
byte[] headsIdentity;
RootSet initialRoots;
RootSet latestRoots;
HeadEntryId initial;
HeadEntryId selectedEntryId;
HeadEntryId latest;
HeadEntryId opaqueInitial;
HeadEntryId opaqueLatest;
HeadEntryId reservedFork;
HeadEntryId automaticFork;
HeadEntry selected;
RootPayload retainedTag;
TagInfo retainedInfo;
FrameAddress afterDispose;
using (var data = DataStore.Create(dataPath, rotationThresholdBytes: 128)) {
    dataIdentity = data.StoreId.ToArray();
    using (var heads = Heads.Create(headsPath, data, reservedHeadIdUpperBound: 2)) {
        headsIdentity = heads.HeadStoreId.ToArray();
        Require(!heads.IsReadOnly && heads.ReservedHeadIdUpperBound == 2, "Unexpected Create mode or reserved range.");
        initialRoots = WriteRoots(data, generation: 0);
        // No application data flush precedes these calls: publication performs the data-first barrier.
        initial = heads.CreateHead(1, initialRoots.Payload, out var outcome).Unwrap();
        Confirmed(outcome);
        opaqueInitial = heads.CreateHead(opaqueInitialBytes, out outcome).Unwrap();
        Confirmed(outcome);
        Require(opaqueInitial.HeadId.LocalId == 3, "Automatic allocation entered the reserved range.");
        selectedEntryId = heads.PublishHead(initial.HeadId, initialRoots.Payload, out outcome).Unwrap();
        Confirmed(outcome);
        Require(selectedEntryId != initial, "Equal bytes must retain distinct publication entryIds.");
        latestRoots = WriteRoots(data, generation: 1);
        latest = heads.PublishHead(initial.HeadId, latestRoots.Payload, out outcome).Unwrap();
        Confirmed(outcome);

        // Select a nonhead entryId through the bounded history API, then fork after its callback ends.
        int visited = 0;
        HeadEntry? candidate = null;
        HeadHistoryEnd end = heads.ReadHeadHistory(initial.HeadId, new HeadHistoryBudget(16, 128), entry => {
            visited++;
            if (visited == 2) { candidate = entry; return false; }
            return true;
        }).Unwrap();
        selected = candidate ?? throw new InvalidDataException("History did not deliver the selected old entryId.");
        Require(end == HeadHistoryEnd.VisitorStopped && visited == 2 && selected.Id == selectedEntryId,
            "History selected a different entryId or failed to stop normally.");
        Require(selected.Payload.SequenceEqual(initialRoots.Payload), "The exact old point changed bytes.");
        reservedFork = heads.ForkHead(2, selected.Id, out outcome).Unwrap();
        Confirmed(outcome);
        automaticFork = heads.ForkHead(selected.Id, out outcome).Unwrap();
        Confirmed(outcome);
        Require(reservedFork.HeadId.LocalId == 2 && automaticFork.HeadId.LocalId == 4, "Fork allocation changed the reserved/automatic domains.");
        Require(heads.ReadHead(initial.HeadId).Unwrap().Id == latest, "Fork changed the source head.");
        VerifyForks(heads, selectedEntryId, initial, reservedFork, automaticFork);
        VerifyForkHistory(heads, reservedFork, selectedEntryId, initial, initialRoots.Payload);
        VerifyForkHistory(heads, automaticFork, selectedEntryId, initial, initialRoots.Payload);

        Require(heads.CreateTag(relatedTag, selected.Payload, selected.Id, out outcome).Unwrap(), "Related tag creation failed.");
        Confirmed(outcome);
        Require(heads.CreateTag(opaqueTag, opaqueInitialBytes, out outcome).Unwrap(), "Anonymous opaque tag creation failed.");
        Confirmed(outcome);
        opaqueLatest = heads.PublishHead(opaqueInitial.HeadId, opaqueLatestBytes, out outcome).Unwrap();
        Confirmed(outcome);
        Require(heads.ReadHead(opaqueInitial.HeadId).Unwrap().Payload.SequenceEqual(opaqueLatestBytes), "Opaque root bytes were interpreted or changed.");
        var duplicate = heads.CreateTag(relatedTag, latestRoots.Payload, out outcome);
        Require(duplicate.IsFailure && duplicate.Error!.ErrorCode == "HeadStore.DuplicateTagName"
            && outcome == PublicationOutcome.NotAttempted, "An immutable tag was overwritten or attempted publication.");
        retainedTag = heads.ResolveTag(relatedTag).Unwrap();
        retainedInfo = heads.ReadTagInfo(relatedTag).Unwrap();
        Require(retainedTag.Bytes.SequenceEqual(selected.Payload), "The related tag changed after newer publication.");
        VerifyTags(heads, relatedTag, opaqueTag, selectedEntryId, initialRoots.Payload, opaqueInitialBytes);
        VerifyRoots(data, heads.ReadHead(initial.HeadId).Unwrap().Payload, latestRoots);
        VerifyRoots(data, retainedTag.Bytes, initialRoots);
    }
    // The caller owns borrowed data. Closing VS leaves it usable for further work.
    data.EnsureUsable();
    afterDispose = data.Append(99, "after-versionstore-dispose"u8).Unwrap();
    data.ConfirmDurable();
}
Require(selected.Id == selectedEntryId && selected.Payload.SequenceEqual(retainedTag.Bytes),
    "Owned entry/tag bytes depend on either disposed owner.");
VerifyRelated(retainedInfo, relatedTag, selectedEntryId);
Require(NamedFrameMapCodec.Decode(retainedTag.Bytes).Unwrap()["工作区/😀"] == initialRoots.Roots["工作区/😀"],
    "Owned decoded addresses changed after Dispose.");

HeadStoreAudit audit;
long dataFrames;
using (var data = DataStore.OpenReadOnly(dataPath)) {
    using (var heads = Heads.OpenReadOnly(headsPath, data)) {
        Require(data.IsReadOnly && heads.IsReadOnly, "Cold verification must use read-only owners.");
        Require(data.StoreId.SequenceEqual(dataIdentity) && heads.HeadStoreId.SequenceEqual(headsIdentity),
            "Cold reopen changed a persisted identity.");
        Require(heads.ListHeads().Unwrap().Select(id => id.LocalId).Order().SequenceEqual(new uint[] { 1, 2, 3, 4 }),
            "Cold discovery changed the formal ref set.");
        HeadEntry current = heads.ReadHead(initial.HeadId).Unwrap();
        Require(current.Id == latest, "Cold ReadHead returned a different current entryId.");
        VerifyRoots(data, current.Payload, latestRoots);
        List<HeadEntry> history = History(heads, initial.HeadId);
        Require(history.Select(entry => entry.Id).SequenceEqual(new[] { latest, selectedEntryId, initial }),
            "Cold history lost or merged exact entryIds.");
        VerifyRoots(data, history[1].Payload, initialRoots);
        VerifyForks(heads, selectedEntryId, initial, reservedFork, automaticFork);
        VerifyForkHistory(heads, reservedFork, selectedEntryId, initial, initialRoots.Payload);
        VerifyForkHistory(heads, automaticFork, selectedEntryId, initial, initialRoots.Payload);
        VerifyRoots(data, heads.ReadHead(reservedFork.HeadId).Unwrap().Payload, initialRoots);
        VerifyRoots(data, heads.ReadHead(automaticFork.HeadId).Unwrap().Payload, initialRoots);
        HeadEntry opaque = heads.ReadHead(opaqueInitial.HeadId).Unwrap();
        Require(opaque.Id == opaqueLatest && opaque.Payload.SequenceEqual(opaqueLatestBytes), "Cold opaque root bytes changed.");
        Require(History(heads, opaqueInitial.HeadId).Select(entry => entry.Id)
            .SequenceEqual(new[] { opaqueLatest, opaqueInitial }), "Cold opaque history changed.");
        VerifyTags(heads, relatedTag, opaqueTag, selectedEntryId, initialRoots.Payload, opaqueInitialBytes);
        VerifyRoots(data, heads.ResolveTag(relatedTag).Unwrap().Bytes, initialRoots);
        audit = heads.Audit().Unwrap();
        Require(audit.HeadCount == 4 && audit.EntryCount == 7 && audit.OriginCount == 2 && audit.TagCount == 2
            && audit.TagBucketCount is >= 1 and <= 2, "Complete read-only Audit returned unexpected physical totals.");
    }
    data.EnsureUsable();
    using var extra = data.ReadFrame(afterDispose).Unwrap();
    Require(extra.Tag == 99 && extra.PayloadAndMeta.SequenceEqual("after-versionstore-dispose"u8),
        "Borrowed data stopped working after HeadStore Dispose.");
    // VS Audit does not traverse data: explicitly read all referenced frames above and audit data here.
    dataFrames = data.Audit(_ => { }).Unwrap();
    Require(dataFrames == 5, "Data Audit did not cover the actual data frames.");
}
HeadStoreAudit batchAudit = VerifyBatchPublication(directory);
VerifyReservableWriters();
Console.WriteLine($"HeadStore public package smoke passed: {audit.HeadCount} refs, {audit.EntryCount} entries, {audit.OriginCount} forks, {audit.TagCount} tags, {dataFrames} data frames; NamedFrameMap/opaque publish, exact history, immutable tags and cold read-only Audit.");
Console.WriteLine($"HeadStore package increments passed: {batchAudit.HeadCount} isolated refs, {batchAudit.EntryCount} entries; single/CU PublishHeads, exact ReadEntry, both-layer cold reopen, EnsureUsable, healthy RecoveryReports and transitive Data borrow/cancel.");
Console.WriteLine($"Retained smoke files: {directory}");

static HeadStoreAudit VerifyBatchPublication(string directory) {
    string dataPath = Path.Combine(directory, "batch-data");
    string headsPath = Path.Combine(directory, "batch-heads");
    byte[] initialABytes = [0xa0];
    byte[] initialBBytes = [0xb0];
    byte[] singleBytes = [0xa1, 0x00, 0xff];
    byte[] cuABytes = [0xa2, 0x80];
    byte[] cuBBytes = [0xb2, 0x00, 0x7f, 0xff];
    byte[] latestABytes = [0xa3, 0xfe];
    var expected = new Dictionary<HeadEntryId, byte[]>();
    HeadEntryId single;
    HeadEntryId cuA;
    HeadEntryId cuB;
    HeadEntryId latestA;
    IReadOnlyList<HeadStoreRecoveryReport> retainedReports;
    using (var data = DataStore.Create(dataPath))
    using (var heads = Heads.Create(headsPath, data)) {
        heads.EnsureUsable();
        retainedReports = heads.RecoveryReports;
        Require(retainedReports.Count == 0, "Fresh HeadStore reported physical recovery.");
        HeadEntryId initialA = heads.CreateHead(initialABytes, out var outcome).Unwrap();
        Confirmed(outcome);
        HeadEntryId initialB = heads.CreateHead(initialBBytes, out outcome).Unwrap();
        Confirmed(outcome);

        byte[] singleInput = [0xf0, 0xa1, 0x00, 0xff, 0xf1];
        var singlePublication = heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [initialA.HeadId] = singleInput.AsMemory(1, 3)
        }, out outcome).Unwrap();
        Confirmed(outcome);
        Require(singlePublication.Count == 1, "Single-member PublishHeads returned a different member set.");
        single = singlePublication[initialA.HeadId];
        singleInput.AsSpan().Fill(0xcc);

        var cuPublication = heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [initialB.HeadId] = cuBBytes,
            [initialA.HeadId] = cuABytes
        }, out outcome).Unwrap();
        Confirmed(outcome);
        Require(cuPublication.Count == 2, "CU PublishHeads returned a partial or different member set.");
        cuA = cuPublication[initialA.HeadId];
        cuB = cuPublication[initialB.HeadId];
        Require(cuA != single && cuB != initialB, "CU reused an earlier publication entryId.");
        Require(heads.ReadHead(cuA.HeadId).Unwrap().Id == cuA
            && heads.ReadHead(cuB.HeadId).Unwrap().Id == cuB, "CU did not install both member heads.");

        // Cover one CU member with a later ordinary Entry; the old complete group remains readable.
        var latestPublication = heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [initialA.HeadId] = latestABytes
        }, out outcome).Unwrap();
        Confirmed(outcome);
        Require(latestPublication.Count == 1, "The later single-member publication changed its member set.");
        latestA = latestPublication[initialA.HeadId];
        Require(latestA != cuA, "A later single-member publication reused its CU entryId.");
        expected.Add(initialA, initialABytes);
        expected.Add(initialB, initialBBytes);
        expected.Add(single, singleBytes);
        expected.Add(cuA, cuABytes);
        expected.Add(cuB, cuBBytes);
        expected.Add(latestA, latestABytes);
        VerifyBatchReads(heads, expected, latestA, cuB);
        Require(retainedReports.Count == 0, "Healthy batch publication unexpectedly recovered a tail.");
    }
    Require(retainedReports.Count == 0, "The saved recovery view became unreadable or changed after Dispose.");
    VerifyPublicationKind(headsPath, single, 1);
    VerifyPublicationKind(headsPath, cuA, 2);
    VerifyPublicationKind(headsPath, cuB, 2);
    VerifyPublicationKind(headsPath, latestA, 1);

    // Both layers are new owners on each reopen; none of these reads use the original cache.
    using (var data = DataStore.Open(dataPath))
    using (var heads = Heads.Open(headsPath, data)) {
        VerifyBatchReads(heads, expected, latestA, cuB);
        Require(heads.RecoveryReports.Count == 0, "Healthy writable reopen unexpectedly repaired a ref.");
    }
    using (var data = DataStore.OpenReadOnly(dataPath))
    using (var heads = Heads.OpenReadOnly(headsPath, data)) {
        VerifyBatchReads(heads, expected, latestA, cuB);
        HeadStoreAudit audit = heads.Audit().Unwrap();
        Require(audit.HeadCount == 2 && audit.EntryCount == 6 && audit.OriginCount == 0
            && audit.TagCount == 0 && audit.TagBucketCount == 0, "Isolated batch Audit returned unexpected totals.");
        Require(heads.RecoveryReports.Count == 0, "Read-only batch verification reported a repair.");
        return audit;
    }
}

static void VerifyBatchReads(Heads heads, IReadOnlyDictionary<HeadEntryId, byte[]> expected,
    HeadEntryId latestA, HeadEntryId latestB) {
    heads.EnsureUsable();
    foreach (var entry in expected) {
        HeadEntry exact = heads.ReadEntry(entry.Key).Unwrap();
        Require(exact.Id == entry.Key && exact.Payload.SequenceEqual(entry.Value),
            "ReadEntry changed an exact old or current publication.");
    }
    foreach (HeadEntryId entryId in new[] { latestA, latestB }) {
        HeadEntry current = heads.ReadHead(entryId.HeadId).Unwrap();
        Require(current.Id == entryId && current.Payload.SequenceEqual(expected[entryId]),
            "ReadHead disagrees with the expected current batch member.");
    }
}

static void VerifyPublicationKind(string headsPath, HeadEntryId entryId, uint expectedTag) {
    string headPath = Path.Combine(headsPath, "refs", $"{entryId.HeadId.LocalId:x8}.rbf");
    using var file = RbfFile.OpenReadOnlyExisting(headPath, RbfCacheMode.Off);
    using var frame = file.ReadPooledFrame(entryId.Ticket).Unwrap();
    Require(frame.Ticket == entryId.Ticket && frame.Tag == expectedTag && frame.TailMetaLength == 8
        && !frame.IsTombstone, "PublishHeads chose a different physical Entry/CU record kind.");
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
    var frames = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
    foreach (string key in new[] { "entry", "工作区/😀" }) {
        frames.Add(key, data.Append(41, EncodeData(key, generation), [(byte)generation]).Unwrap());
    }
    return new(NamedFrameMapCodec.Encode(frames).Unwrap(), frames, generation);
}

static byte[] EncodeData(string key, int generation) => Encoding.UTF8.GetBytes($"{key}:{generation}");

static void VerifyRoots(DataStore data, ReadOnlySpan<byte> payload, RootSet expected) {
    IReadOnlyDictionary<string, FrameAddress> frames = NamedFrameMapCodec.Decode(payload).Unwrap();
    Require(frames.Count == expected.Roots.Count, "NamedFrameMap changed its root count.");
    foreach (var entry in expected.Roots) {
        Require(frames.TryGetValue(entry.Key, out FrameAddress address) && address == entry.Value,
            "NamedFrameMap changed an Ordinal key or frame address.");
        using var frame = data.ReadFrame(address).Unwrap();
        byte[] body = EncodeData(entry.Key, expected.Generation).Concat(new[] { (byte)expected.Generation }).ToArray();
        Require(frame.Tag == 41 && frame.TailMetaLength == 1 && !frame.IsTombstone
            && frame.PayloadAndMeta.SequenceEqual(body), "A published root does not read the actual expected data bytes.");
    }
}

static List<HeadEntry> History(Heads heads, HeadId headId) {
    var entries = new List<HeadEntry>();
    HeadHistoryEnd end = heads.ReadHeadHistory(headId, new HeadHistoryBudget(16, 128), entry => {
        entries.Add(entry);
        return true;
    }).Unwrap();
    Require(end == HeadHistoryEnd.Complete, "Bounded history did not reach a complete source prefix.");
    return entries;
}

static void VerifyForkHistory(Heads heads, HeadEntryId fork, HeadEntryId selected, HeadEntryId initial, byte[] payload) {
    List<HeadEntry> history = History(heads, fork.HeadId);
    Require(history.Select(entry => entry.Id).SequenceEqual(new[] { fork, selected, initial }),
        "Fork inherited a different source point or included later source history.");
    Require(history.All(entry => entry.Payload.SequenceEqual(payload)), "Fork history changed exact source bytes.");
}

static void VerifyForks(Heads heads, HeadEntryId selected, HeadEntryId initial, params HeadEntryId[] forks) {
    IReadOnlyList<HeadOriginInfo> graph = heads.ListHeadOrigins(4).Unwrap();
    Require(graph.Count == forks.Length, "ListHeadOrigins changed the declaration count.");
    foreach (HeadEntryId fork in forks) {
        HeadOriginInfo edge = graph.Single(info => info.ChildHeadId == fork.HeadId);
        Require(edge.SourceHeadId == initial.HeadId && edge.SourceEntryTicket == selected.Ticket,
            "ListHeadOrigins changed the complete exact source coordinate.");
    }
}

static void VerifyTags(Heads heads, string relatedName, string opaqueName, HeadEntryId selected,
    byte[] relatedBytes, byte[] opaqueBytes) {
    Require(heads.ResolveTag(relatedName).Unwrap().Bytes.SequenceEqual(relatedBytes), "Related immutable tag bytes changed.");
    Require(heads.ResolveTag(opaqueName).Unwrap().Bytes.SequenceEqual(opaqueBytes), "Anonymous immutable tag bytes changed with its ref.");
    VerifyRelated(heads.ReadTagInfo(relatedName).Unwrap(), relatedName, selected);
    var scanned = new Dictionary<string, TagInfo>(StringComparer.Ordinal);
    TagScanEnd end = heads.ScanTags(info => {
        Require(scanned.TryAdd(info.Name, info), "ScanTags repeated a tag name.");
        return true;
    }).Unwrap();
    Require(end == TagScanEnd.Complete && scanned.Count == 2, "Tag metadata scan was incomplete.");
    VerifyRelated(scanned[relatedName], relatedName, selected);
    TagInfo anonymous = heads.ReadTagInfo(opaqueName).Unwrap();
    Require(anonymous.Name == opaqueName && anonymous.RelatedRefId is null && anonymous.RelatedTicket is null
        && scanned[opaqueName].RelatedRefId is null && scanned[opaqueName].RelatedTicket is null,
        "Anonymous tag acquired a related coordinate.");
}

static void VerifyRelated(TagInfo info, string name, HeadEntryId entryId) {
    Require(info.Name == name && info.RelatedRefId == entryId.HeadId && info.RelatedTicket == entryId.Ticket,
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
