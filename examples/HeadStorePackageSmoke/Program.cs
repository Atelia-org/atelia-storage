using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Runtime.CompilerServices;
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
VerifyCompactHandles();
string dataPath = Path.Combine(directory, "data");
string headsPath = Path.Combine(directory, "heads");
const string selectedTag = "selected/历史😀";
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
HeadId reservedFork;
HeadId automaticFork;
HeadEntry selected;
HeadSetTag retainedTag;
HeadEntry retainedTagEntry;
HeadSetTagInfo retainedInfo;
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
        Require(heads.GetLocalHeadId(opaqueInitial.HeadId).Unwrap() == 3, "Automatic allocation entered the reserved range.");
        selectedEntryId = heads.PublishHead(initial.HeadId, initialRoots.Payload, out outcome).Unwrap();
        Confirmed(outcome);
        Require(selectedEntryId != initial, "Equal bytes must retain distinct publication entryIds.");
        retainedTag = heads.CreateTag(selectedTag, [initial.HeadId, opaqueInitial.HeadId], out outcome).Unwrap();
        Confirmed(outcome);
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
        Require(heads.GetLocalHeadId(reservedFork).Unwrap() == 2 && heads.GetLocalHeadId(automaticFork).Unwrap() == 4, "Fork allocation changed the reserved/automatic domains.");
        Require(heads.ReadHead(initial.HeadId).Unwrap().Id == latest, "Fork changed the source head.");
        VerifyForks(heads, selectedEntryId, initial, reservedFork, automaticFork);
        VerifyForkHistory(heads, reservedFork, selectedEntryId, initial, initialRoots.Payload);
        VerifyForkHistory(heads, automaticFork, selectedEntryId, initial, initialRoots.Payload);

        heads.CreateTag(opaqueTag, [opaqueInitial.HeadId, reservedFork], out outcome).Unwrap();
        Confirmed(outcome);
        opaqueLatest = heads.PublishHead(opaqueInitial.HeadId, opaqueLatestBytes, out outcome).Unwrap();
        Confirmed(outcome);
        Require(heads.ReadHead(opaqueInitial.HeadId).Unwrap().Payload.SequenceEqual(opaqueLatestBytes), "Opaque root bytes were interpreted or changed.");
        var duplicate = heads.CreateTag(selectedTag, [initial.HeadId], out outcome);
        Require(duplicate.IsFailure && duplicate.Error!.ErrorCode == "HeadStore.DuplicateTagName"
            && outcome == PublicationOutcome.NotAttempted, "An immutable tag was overwritten or attempted publication.");
        retainedTag = heads.ReadTag(selectedTag).Unwrap();
        retainedTagEntry = heads.ReadEntry(retainedTag.Entries[selectedEntryId.HeadId]).Unwrap();
        retainedInfo = heads.ReadTagInfo(selectedTag).Unwrap();
        Require(retainedTagEntry.Payload.SequenceEqual(selected.Payload), "The tagged historical Entry changed after newer publication.");
        VerifyTags(heads, selectedTag, opaqueTag, selectedEntryId, opaqueInitial, reservedFork, initialRoots.Payload, opaqueInitialBytes);
        VerifyRoots(data, heads.ReadHead(initial.HeadId).Unwrap().Payload, latestRoots);
        VerifyRoots(data, retainedTagEntry.Payload, initialRoots);
    }
    // The caller owns borrowed data. Closing VS leaves it usable for further work.
    data.EnsureUsable();
    afterDispose = data.Append(99, "after-versionstore-dispose"u8).Unwrap();
    data.ConfirmDurable();
}
Require(selected.Id == selectedEntryId && selected.Payload.SequenceEqual(retainedTagEntry.Payload),
    "Owned entry/tag bytes depend on either disposed owner.");
Require(retainedTag.Entries.Count == 2 && retainedTag.Entries[selectedEntryId.HeadId] == selectedEntryId
    && retainedTag.Entries[opaqueInitial.HeadId] == opaqueInitial, "The retained member table depends on disposed owners.");
VerifyInfo(retainedInfo, selectedTag, 2);
Require(NamedFrameMapCodec.Decode(retainedTagEntry.Payload).Unwrap()["工作区/😀"] == initialRoots.Roots["工作区/😀"],
    "Owned decoded addresses changed after Dispose.");

HeadStoreAudit audit;
long dataFrames;
using (var data = DataStore.OpenReadOnly(dataPath)) {
    using (var heads = Heads.OpenReadOnly(headsPath, data)) {
        Require(data.IsReadOnly && heads.IsReadOnly, "Cold verification must use read-only owners.");
        Require(data.StoreId.SequenceEqual(dataIdentity) && heads.HeadStoreId.SequenceEqual(headsIdentity),
            "Cold reopen changed a persisted identity.");
        Require(heads.ListHeads().Unwrap().Select(id => heads.GetLocalHeadId(id).Unwrap()).Order().SequenceEqual(new uint[] { 1, 2, 3, 4 }),
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
        VerifyRoots(data, heads.ReadHead(reservedFork).Unwrap().Payload, initialRoots);
        VerifyRoots(data, heads.ReadHead(automaticFork).Unwrap().Payload, initialRoots);
        HeadEntry opaque = heads.ReadHead(opaqueInitial.HeadId).Unwrap();
        Require(opaque.Id == opaqueLatest && opaque.Payload.SequenceEqual(opaqueLatestBytes), "Cold opaque root bytes changed.");
        Require(History(heads, opaqueInitial.HeadId).Select(entry => entry.Id)
            .SequenceEqual(new[] { opaqueLatest, opaqueInitial }), "Cold opaque history changed.");
        VerifyTags(heads, selectedTag, opaqueTag, selectedEntryId, opaqueInitial, reservedFork, initialRoots.Payload, opaqueInitialBytes);
        VerifyRoots(data, heads.ReadEntry(heads.ReadTag(selectedTag).Unwrap().Entries[selectedEntryId.HeadId]).Unwrap().Payload, initialRoots);
        audit = heads.Audit().Unwrap();
        Require(audit.HeadCount == 4 && audit.EntryCount == 5 && audit.OriginCount == 2 && audit.TagCount == 2
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
VerifyReferenceForks(directory);
VerifyPersistentHandles(directory);
VerifyReservableWriters();
Console.WriteLine($"HeadStore public package smoke passed: {audit.HeadCount} heads, {audit.EntryCount} entries, {audit.OriginCount} forks, {audit.TagCount} tags, {dataFrames} data frames; named member tables, exact tagged history/fork identities and cold read-only Audit.");
Console.WriteLine($"HeadStore package increments passed: {batchAudit.HeadCount} isolated heads, {batchAudit.EntryCount} entries; format 4, CU subset/pair tags, single/CU PublishHeads, exact ReadEntry, both-layer cold reopen, EnsureUsable, healthy RecoveryReports and transitive Data borrow/cancel.");
Console.WriteLine($"Retained smoke files: {directory}");

// Public Handle encoding v1, using an application-owned schema and exact 4B/12B files.
static void VerifyPersistentHandles(string directory) {
    string dataPath = Path.Combine(directory, "handles-data");
    string headsPath = Path.Combine(directory, "handles-heads");
    string headFile = Path.Combine(directory, "selected-head.v1.bin");
    string entryFile = Path.Combine(directory, "bookmarked-entry.v1.bin");
    HeadId selected;
    HeadEntryId initial, bookmark, sourceLatest;
    using (var data = DataStore.Create(dataPath))
    using (var heads = Heads.Create(headsPath, data)) {
        initial = heads.CreateHead(new byte[] { 0x30 }, out var outcome).Unwrap(); Confirmed(outcome);
        bookmark = heads.PublishHead(initial.HeadId, new byte[] { 0x31 }, out outcome).Unwrap(); Confirmed(outcome);
        selected = heads.ForkHead(bookmark, out outcome).Unwrap(); Confirmed(outcome);
        sourceLatest = heads.PublishHead(initial.HeadId, new byte[] { 0x32 }, out outcome).Unwrap(); Confirmed(outcome);
    }
    Require(HeadId.EncodedSize == 4 && HeadEntryId.EncodedSize == 12, "Public persistent Handle sizes changed.");
    byte[] headBytes = new byte[HeadId.EncodedSize];
    byte[] entryBytes = new byte[HeadEntryId.EncodedSize];
    Require(selected.TryWrite(headBytes) && bookmark.TryWrite(entryBytes), "Owner-free Handle write failed.");
    File.WriteAllBytes(headFile, headBytes);
    File.WriteAllBytes(entryFile, entryBytes);
    // Version and field kinds belong to the application container, not each compact value.
    File.WriteAllText(Path.Combine(directory, "handles-schema.txt"), "Handle encoding v1\nselected-head: HeadId\nbookmarked-entry: HeadEntryId\n");
    Require(HeadId.TryRead(File.ReadAllBytes(headFile), out HeadId restoredSelected), "Selected Head decode failed.");
    Require(HeadEntryId.TryRead(File.ReadAllBytes(entryFile), out HeadEntryId restoredBookmark), "Entry decode failed.");
    Require(restoredSelected == selected && restoredBookmark == bookmark && restoredSelected != restoredBookmark.HeadId,
        "Persistent fields confused selected C with actual Entry owner A.");
    var keys = new Dictionary<HeadEntryId, string> { [bookmark] = "save" };
    Require(keys[restoredBookmark] == "save", "Restored Entry equality/hash changed.");

    HeadEntryId first, second;
    using (var data = DataStore.Open(dataPath))
    using (var heads = Heads.Open(headsPath, data)) {
        Require(heads.ReadHead(restoredSelected).Unwrap().Id == restoredBookmark,
            "Restored empty fork did not resolve its actual source Entry.");
        HeadEntry old = heads.ReadEntry(restoredBookmark).Unwrap();
        Require(old.Id == bookmark && old.Payload.SequenceEqual(new byte[] { 0x31 }), "Restored old save changed.");
        first = heads.PublishHead(restoredSelected, old.Payload, out var outcome).Unwrap(); Confirmed(outcome);
        var published = heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [restoredSelected] = old.Payload.ToArray()
        }, out outcome).Unwrap(); Confirmed(outcome);
        second = published[restoredSelected];
        Require(first != second && first.HeadId == restoredSelected && second.HeadId == restoredSelected,
            "Restored selected Head did not receive two distinct same-value saves.");
        Require(heads.ReadHead(bookmark.HeadId).Unwrap().Id == sourceLatest, "Publishing selected C wrote source A.");
        HeadEntryId[] expected = [second, first, bookmark, initial];
        Require(EntryHistory(heads, second).Select(entry => entry.Id).SequenceEqual(expected),
            "Accurate history included a later source suffix or merged same-value saves.");
        HeadId fork = heads.ForkHead(restoredBookmark, out outcome).Unwrap(); Confirmed(outcome);
        Require(History(heads, fork).Select(entry => entry.Id).SequenceEqual(new[] { bookmark, initial }),
            "Fork of imported bookmark changed its exact inherited prefix.");

        var pageOne = new List<HeadEntryId>();
        Require(heads.ReadEntryHistory(second, new(2, 256), entry => {
            pageOne.Add(entry.Id);
            return pageOne.Count < 2;
        }).Unwrap() == HeadHistoryEnd.VisitorStopped, "First fixed page did not stop after two entries.");
        Require(pageOne.SequenceEqual(new[] { second, first }), "First fixed page changed its anchor.");
        heads.PublishHead(restoredSelected, new byte[] { 0x33 }, out outcome).Unwrap(); Confirmed(outcome);
        var continuation = new List<HeadEntryId>();
        int visits = 0;
        Require(heads.ReadEntryHistory(first, new(3, 256), entry => {
            if (++visits == 1) { Require(entry.Id == first, "Continuation did not repeat its inclusive anchor."); }
            else { continuation.Add(entry.Id); }
            return true;
        }).Unwrap() == HeadHistoryEnd.Complete, "Continuation did not reach the root.");
        Require(pageOne.Concat(continuation).SequenceEqual(expected), "Paging lost, duplicated, or absorbed newer Entries.");
        int rootVisits = 0;
        Require(heads.ReadEntryHistory(initial, new(1, 256), entry => {
            Require(entry.Id == initial, "Root continuation changed its duplicate anchor."); rootVisits++; return true;
        }).Unwrap() == HeadHistoryEnd.Complete && rootVisits == 1, "Root continuation did not yield the final duplicate-only page.");
        Require(heads.ReadEntry(restoredBookmark).Unwrap().Id == bookmark, "Bookmark changed after C advanced.");
    }
    using (var data = DataStore.OpenReadOnly(dataPath))
    using (var heads = Heads.OpenReadOnly(headsPath, data)) {
        Require(EntryHistory(heads, second).Select(entry => entry.Id).SequenceEqual(new[] { second, first, bookmark, initial }),
            "Cold exact history changed its fixed prefix.");
        Require(heads.ReadHead(bookmark.HeadId).Unwrap().Id == sourceLatest, "Source A changed across imported C writes.");
        HeadStoreAudit audit = heads.Audit().Unwrap();
        Require(audit.HeadCount == 3 && audit.EntryCount == 6 && audit.OriginCount == 2, "Persistent Handle Audit totals changed.");
    }
    Console.WriteLine("Persistent Handle package chain passed: 4B HeadId / 12B EntryId, owner-free disk decode, selected C writes, stable old save, exact history/fork and inclusive paging.");
}

static List<HeadEntry> EntryHistory(Heads heads, HeadEntryId start) {
    var entries = new List<HeadEntry>();
    Require(heads.ReadEntryHistory(start, new(16, 512), entry => { entries.Add(entry); return true; }).Unwrap()
        == HeadHistoryEnd.Complete, "Exact Entry history did not complete.");
    return entries;
}


// The selected Head is the write target; its resolved Entry belongs to its actual source.
// A fork adds no domain save. Tags retain all selected Heads even when values coincide.
static void VerifyReferenceForks(string directory) {
    string dataPath = Path.Combine(directory, "reference-data");
    string headsPath = Path.Combine(directory, "reference-heads");
    HeadEntryId source;
    HeadId child, sibling, emptyGrandchild;
    HeadEntryId childPublication;
    using (var data = DataStore.Create(dataPath))
    using (var heads = Heads.Create(headsPath, data)) {
        source = heads.CreateHead([0x91], out var outcome).Unwrap(); Confirmed(outcome);
        Require(heads.ReadHeadOrigin(source.HeadId).Unwrap().SourceEntryId is null,
            "An independent root declared a fork source.");
        child = heads.ForkHead(source, out outcome).Unwrap(); Confirmed(outcome);
        HeadOrigin retainedOrigin = heads.ReadHeadOrigin(child).Unwrap();
        Require(retainedOrigin.SourceEntryId == source, "Empty fork origin did not retain its exact source.");
        sibling = heads.ForkHead(source, out outcome).Unwrap(); Confirmed(outcome);
        HeadEntry before = heads.ReadHead(child).Unwrap();
        Require(before.Id == source, "Fork introduced a synthetic save identity.");
        emptyGrandchild = heads.ForkHead(before.Id, out outcome).Unwrap(); Confirmed(outcome);
        Require(heads.ListHeadOrigins(8).Unwrap().All(edge => edge.SourceHeadId == source.HeadId
            && edge.SourceEntryTicket == source.Ticket), "Empty fork chain failed to collapse to its actual source.");
        HeadSetTag tag = heads.CreateTag("shared-source", [source.HeadId, child, sibling, emptyGrandchild], out outcome).Unwrap();
        Confirmed(outcome);
        Require(tag.Entries.Count == 4 && tag.Entries.Values.All(id => id == source), "Tag lost shared-source selected Heads.");
        // First save goes to child, using before.Id as the application's predecessor.
        childPublication = heads.PublishHead(child, [0x92], out outcome).Unwrap(); Confirmed(outcome);
        Require(heads.ReadHeadOrigin(child).Unwrap().SourceEntryId == source
            && retainedOrigin.SourceEntryId == source, "First local publication changed the fixed fork origin.");
        Require(childPublication.HeadId == child && heads.ReadHead(child).Unwrap().Id == childPublication,
            "Fork's first save went to its source Head.");
        Require(History(heads, child).Select(entry => entry.Id).SequenceEqual(new[] { childPublication, source }),
            "First save requires duplicate clone filtering.");
        heads.PublishHead(source.HeadId, [0x93], out outcome).Unwrap(); Confirmed(outcome);
    }
    foreach (bool readOnly in new[] { false, true }) {
        using var data = readOnly ? DataStore.OpenReadOnly(dataPath) : DataStore.Open(dataPath);
        using var heads = readOnly ? Heads.OpenReadOnly(headsPath, data) : Heads.Open(headsPath, data);
        Require(heads.ReadHeadOrigin(source.HeadId).Unwrap().SourceEntryId is null
            && new[] { child, sibling, emptyGrandchild }.All(id => heads.ReadHeadOrigin(id).Unwrap().SourceEntryId == source),
            "Cold targeted origins changed after the child and source advanced.");
        Require(heads.ReadHead(child).Unwrap().Id == childPublication && heads.ReadHead(sibling).Unwrap().Id == source
            && heads.ReadHead(emptyGrandchild).Unwrap().Id == source, "Cold reference fork changed after its source advanced.");
        HeadSetTag tag = heads.ReadTag("shared-source").Unwrap();
        Require(tag.Entries.Keys.OrderBy(id => heads.GetLocalHeadId(id).Unwrap()).SequenceEqual(new[] { source.HeadId, child, sibling, emptyGrandchild }.OrderBy(id => heads.GetLocalHeadId(id).Unwrap()))
            && tag.Entries.Values.All(id => id == source), "Cold shared-source tag changed keys or exact values.");
        if (readOnly) {
            HeadStoreAudit audit = heads.Audit().Unwrap();
            Require(audit.HeadCount == 4 && audit.EntryCount == 3 && audit.OriginCount == 3 && audit.TagCount == 1,
                "Reference-fork Audit counted cloned Entries.");
        }
    }
    Console.WriteLine("Targeted origin package chain passed: root, empty fork, first local save and both-layer cold read-only reopen.");
}

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

        HeadSetTag subset = heads.CreateTag("cu-subset", [cuA.HeadId], out outcome).Unwrap();
        Confirmed(outcome);
        Require(subset.Entries.Count == 1 && subset.Entries[cuA.HeadId] == cuA, "Unselected CU peer leaked into the tag.");
        HeadSetTag pair = heads.CreateTag("cu-pair", [cuB.HeadId, cuA.HeadId], out outcome).Unwrap();
        Confirmed(outcome);
        Require(pair.Entries.Count == 2 && pair.Entries[cuA.HeadId] == cuA && pair.Entries[cuB.HeadId] == cuB,
            "Tag tore a jointly published group.");

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
        VerifyBatchTags(heads, cuA, cuB, cuABytes, cuBBytes);
        Require(retainedReports.Count == 0, "Healthy batch publication unexpectedly recovered a tail.");
    }
    Require(retainedReports.Count == 0, "The saved recovery view became unreadable or changed after Dispose.");

    // Both layers are new owners on each reopen; none of these reads use the original cache.
    using (var data = DataStore.Open(dataPath))
    using (var heads = Heads.Open(headsPath, data)) {
        VerifyBatchReads(heads, expected, latestA, cuB);
        VerifyBatchTags(heads, cuA, cuB, cuABytes, cuBBytes);
        Require(heads.RecoveryReports.Count == 0, "Healthy writable reopen unexpectedly repaired a ref.");
    }
    using (var data = DataStore.OpenReadOnly(dataPath))
    using (var heads = Heads.OpenReadOnly(headsPath, data)) {
        VerifyBatchReads(heads, expected, latestA, cuB);
        VerifyBatchTags(heads, cuA, cuB, cuABytes, cuBBytes);
        VerifyPublicationKind(heads, headsPath, single, 1);
        VerifyPublicationKind(heads, headsPath, cuA, 2);
        VerifyPublicationKind(heads, headsPath, cuB, 2);
        VerifyPublicationKind(heads, headsPath, latestA, 1);
        HeadStoreAudit audit = heads.Audit().Unwrap();
        Require(audit.HeadCount == 2 && audit.EntryCount == 6 && audit.OriginCount == 0
            && audit.TagCount == 2 && audit.TagBucketCount is >= 1 and <= 2, "Isolated batch Audit returned unexpected totals.");
        Require(heads.RecoveryReports.Count == 0, "Read-only batch verification reported a repair.");
        return audit;
    }
}

static void VerifyBatchTags(Heads heads, HeadEntryId a, HeadEntryId b, byte[] aBytes, byte[] bBytes) {
    HeadSetTag subset = heads.ReadTag("cu-subset").Unwrap();
    HeadSetTag pair = heads.ReadTag("cu-pair").Unwrap();
    Require(subset.Entries.Count == 1 && subset.Entries[a.HeadId] == a, "Cold tag subset changed or included its CU peer.");
    Require(pair.Entries.Count == 2 && pair.Entries[a.HeadId] == a && pair.Entries[b.HeadId] == b, "Cold CU tag changed its fixed positions.");
    Require(heads.ReadEntry(pair.Entries[a.HeadId]).Unwrap().Payload.SequenceEqual(aBytes)
        && heads.ReadEntry(pair.Entries[b.HeadId]).Unwrap().Payload.SequenceEqual(bBytes), "Cold tagged CU Entries were not exactly readable.");
}

static void VerifyBatchReads(Heads heads, IReadOnlyDictionary<HeadEntryId, byte[]> expected,
    HeadEntryId latestA, HeadEntryId latestB) {
    heads.EnsureUsable();
    foreach (var entry in expected) {
        HeadEntry exact = heads.ReadEntry(entry.Key).Unwrap();
        Require(exact.Id == entry.Key && exact.Payload.SequenceEqual(entry.Value),
            "ReadEntry changed an exact old or current publication.");
        byte[] codeword = new byte[HeadEntryId.EncodedSize];
        Require(entry.Key.TryWrite(codeword) && HeadEntryId.TryRead(codeword, out _), "Published Entry codec failed.");
        Require(HeadEntryId.TryRead(codeword, out HeadEntryId imported), "Published Entry could not decode.");
        List<HeadEntry> history = EntryHistory(heads, imported);
        HeadEntryId[] prefix = expected.Keys.Where(id => id.HeadId == imported.HeadId && id.Ticket.Offset <= imported.Ticket.Offset)
            .OrderByDescending(id => id.Ticket.Offset).ToArray();
        Require(history.Select(item => item.Id).SequenceEqual(prefix)
            && history.All(item => item.Payload.SequenceEqual(expected[item.Id])),
            "Imported ordinary/CU Entry history changed its exact prefix or payload.");
    }
    foreach (HeadEntryId entryId in new[] { latestA, latestB }) {
        HeadEntry current = heads.ReadHead(entryId.HeadId).Unwrap();
        Require(current.Id == entryId && current.Payload.SequenceEqual(expected[entryId]),
            "ReadHead disagrees with the expected current batch member.");
    }
}

static void VerifyPublicationKind(Heads heads, string headsPath, HeadEntryId entryId, uint expectedTag) {
    string headPath = Path.Combine(headsPath, "refs", $"{heads.GetLocalHeadId(entryId.HeadId).Unwrap():x8}.rbf");
    using var file = RbfFile.OpenReadOnlyExisting(headPath, RbfCacheMode.Off);
    var scan = file.ScanForward().GetEnumerator();
    Require(scan.MoveNext(), "Package-created Head is missing its header.");
    using (var header = file.ReadPooledFrame(scan.Current.Ticket).Unwrap()) {
        Require(header.Tag == 0 && BinaryPrimitives.ReadUInt32LittleEndian(header.PayloadAndMeta) == 4,
            "The public package did not create HeadStore format 4.");
    }
    using var frame = file.ReadPooledFrame(entryId.Ticket).Unwrap();
    Require(frame.Ticket == entryId.Ticket && frame.Tag == expectedTag && frame.TailMetaLength == 8
        && !frame.IsTombstone, "PublishHeads chose a different physical Entry/CU record kind.");
}

static void VerifyCompactHandles() {
    Require(Unsafe.SizeOf<HeadId>() == 4 && Unsafe.SizeOf<HeadEntryId>() == 16,
        "Compact Handle runtime sizes differ from the public contract.");
    Require(Heads.MaxLocalHeadId == 0x00FF_FFFF, "HeadStore local-id range changed.");
    const uint key = 0xA5C3_1987;
    Require(ZeroPreservingXor32.Apply(0, key) == 0 && ZeroPreservingXor32.Apply(key, key) == key,
        "The public Data mapping changed its zero-preserving special cases.");
    foreach (uint value in new uint[] { 1, Heads.MaxLocalHeadId, key, uint.MaxValue }) {
        uint encoded = ZeroPreservingXor32.Apply(value, key);
        Require(encoded != 0 && ZeroPreservingXor32.Apply(encoded, key) == value,
            "The public Data mapping did not round-trip a nonzero value.");
    }
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

static void VerifyForkHistory(Heads heads, HeadId fork, HeadEntryId selected, HeadEntryId initial, byte[] payload) {
    List<HeadEntry> history = History(heads, fork);
    Require(history.Select(entry => entry.Id).SequenceEqual(new[] { selected, initial }),
        "Fork inherited a different source point or included later source history.");
    Require(history.All(entry => entry.Payload.SequenceEqual(payload)), "Fork history changed exact source bytes.");
}

static void VerifyForks(Heads heads, HeadEntryId selected, HeadEntryId initial, params HeadId[] forks) {
    IReadOnlyList<HeadOriginInfo> graph = heads.ListHeadOrigins(4).Unwrap();
    Require(graph.Count == forks.Length, "ListHeadOrigins changed the declaration count.");
    foreach (HeadId fork in forks) {
        HeadOriginInfo edge = graph.Single(info => info.ChildHeadId == fork);
        Require(edge.SourceHeadId == initial.HeadId && edge.SourceEntryTicket == selected.Ticket,
            "ListHeadOrigins changed the complete exact source coordinate.");
    }
}

static void VerifyTags(Heads heads, string selectedName, string opaqueName, HeadEntryId selected,
    HeadEntryId opaque, HeadId fork, byte[] selectedBytes, byte[] opaqueBytes) {
    HeadSetTag selectedTag = heads.ReadTag(selectedName).Unwrap();
    Require(selectedTag.Entries.Count == 2 && selectedTag.Entries[selected.HeadId] == selected
        && selectedTag.Entries[opaque.HeadId] == opaque, "Selected member table changed after later publications.");
    HeadSetTag forkTag = heads.ReadTag(opaqueName).Unwrap();
    Require(forkTag.Entries.Count == 2 && forkTag.Entries[opaque.HeadId] == opaque
        && forkTag.Entries[fork] == selected, "Tag lost the fork child identity.");
    Require(heads.ReadEntry(selectedTag.Entries[selected.HeadId]).Unwrap().Payload.SequenceEqual(selectedBytes), "Tagged historical Entry bytes changed.");
    Require(heads.ReadEntry(forkTag.Entries[opaque.HeadId]).Unwrap().Payload.SequenceEqual(opaqueBytes), "Tagged opaque Entry bytes changed.");
    Require(heads.ReadEntry(forkTag.Entries[fork]).Unwrap().Payload.SequenceEqual(selectedBytes), "Tagged fork Entry bytes changed.");
    VerifyInfo(heads.ReadTagInfo(selectedName).Unwrap(), selectedName, 2);
    var scanned = new Dictionary<string, HeadSetTagInfo>(StringComparer.Ordinal);
    HeadSetTagScanEnd end = heads.ScanTags(info => {
        Require(scanned.TryAdd(info.Name, info), "ScanTags repeated a tag name.");
        return true;
    }).Unwrap();
    Require(end == HeadSetTagScanEnd.Complete && scanned.Count == 2, "Tag metadata scan was incomplete.");
    VerifyInfo(scanned[selectedName], selectedName, 2);
    VerifyInfo(scanned[opaqueName], opaqueName, 2);
}

static void VerifyInfo(HeadSetTagInfo info, string name, int count) {
    Require(info.Name == name && info.EntryCount == count, "Tag metadata changed its name or member count.");
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
