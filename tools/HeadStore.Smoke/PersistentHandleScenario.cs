using System.Text.Json;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Smoke;

// The application schema identifies Handle encoding v1 and the two distinct field kinds.
internal static class PersistentHandleScenario {
    internal static int Run(string[] args) {
        if (args.Length != 2) { throw new ArgumentException("Usage: handle-write <fresh-root> | handle-read <root>"); }
        string root = Path.GetFullPath(args[1]);
        if (args[0] == "handle-write") { Write(root); }
        else { Read(root); }
        return 0;
    }

    private static void Write(string root) {
        Require(!File.Exists(root) && (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()),
            "handle-write requires an absent or empty root.");
        Directory.CreateDirectory(root);
        HeadId selected;
        HeadEntryId initial, bookmark, sourceLatest;
        string dataId, storeId;
        using (var data = DataStore.Create(Path.Combine(root, "data")))
        using (var heads = Heads.Create(Path.Combine(root, "heads"), data)) {
            dataId = Convert.ToHexString(data.StoreId);
            storeId = Convert.ToHexString(heads.HeadStoreId);
            initial = heads.CreateHead(new byte[] { 0x10 }, out var outcome).Unwrap(); Confirmed(outcome);
            bookmark = heads.PublishHead(initial.HeadId, new byte[] { 0x11 }, out outcome).Unwrap(); Confirmed(outcome);
            selected = heads.ForkHead(bookmark, out outcome).Unwrap(); Confirmed(outcome);
            sourceLatest = heads.PublishHead(initial.HeadId, new byte[] { 0x12 }, out outcome).Unwrap(); Confirmed(outcome);
            Require(selected != bookmark.HeadId && heads.ReadHead(selected).Unwrap().Id == bookmark,
                "Empty fork lost its selected Head or actual source Entry.");
        }

        // Encode after both owners close. No local id or CLR struct dump is persisted.
        byte[] headBytes = new byte[HeadId.EncodedSize];
        byte[] entryBytes = Encode(bookmark);
        Require(selected.TryWrite(headBytes), "Selected Head could not encode without its owner.");
        File.WriteAllBytes(Path.Combine(root, "selected-head.v1.bin"), headBytes);
        File.WriteAllBytes(Path.Combine(root, "bookmarked-entry.v1.bin"), entryBytes);
        File.WriteAllText(Path.Combine(root, "handle-schema.json"), JsonSerializer.Serialize(
            new Manifest(1, "HeadId", "HeadEntryId", dataId, storeId,
                Convert.ToHexString(Encode(initial)), Convert.ToHexString(Encode(sourceLatest)))));
        Emit(new { status = "ok", scenario = "handle-write", pid = Environment.ProcessId,
            headHex = Convert.ToHexString(headBytes), entryHex = Convert.ToHexString(entryBytes),
            headBytes = headBytes.Length, entryBytes = entryBytes.Length, encodedAfterDispose = true });
    }

    private static void Read(string root) {
        Manifest schema = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "handle-schema.json")))
            ?? throw new InvalidDataException("Missing application Handle schema.");
        Require(schema.EncodingVersion == 1 && schema.HeadKind == "HeadId" && schema.EntryKind == "HeadEntryId",
            "Unsupported application Handle schema.");
        byte[] headBytes = File.ReadAllBytes(Path.Combine(root, "selected-head.v1.bin"));
        byte[] entryBytes = File.ReadAllBytes(Path.Combine(root, "bookmarked-entry.v1.bin"));
        // This is deliberately before either Open: this process has never had an owner or issued values.
        Require(HeadId.TryRead(headBytes, out HeadId selected), "Persisted selected Head did not decode.");
        Require(HeadEntryId.TryRead(entryBytes, out HeadEntryId bookmark), "Persisted bookmark did not decode.");
        Require(HeadEntryId.TryRead(Convert.FromHexString(schema.Initial), out HeadEntryId initial), "Initial Entry did not decode.");
        Require(HeadEntryId.TryRead(Convert.FromHexString(schema.SourceLatest), out HeadEntryId sourceLatest), "Latest Entry did not decode.");
        byte[] roundTrip = new byte[HeadId.EncodedSize];
        Require(selected.TryWrite(roundTrip) && roundTrip.SequenceEqual(headBytes) && Encode(bookmark).SequenceEqual(entryBytes),
            "Owner-free decode changed the complete encoded values.");
        var savedKeys = new Dictionary<HeadEntryId, string> { [bookmark] = "old-save" };
        Require(HeadEntryId.TryRead(entryBytes, out HeadEntryId duplicate) && savedKeys[duplicate] == "old-save",
            "Decoded equality/hash changed.");

        HeadEntryId first, second;
        int pages, emptyPages;
        using (var data = DataStore.Open(Path.Combine(root, "data")))
        using (var heads = Heads.Open(Path.Combine(root, "heads"), data)) {
            Require(Convert.ToHexString(data.StoreId) == schema.DataId && Convert.ToHexString(heads.HeadStoreId) == schema.StoreId,
                "Application reopened a different library context.");
            Require(selected != bookmark.HeadId && heads.ReadHead(selected).Unwrap().Id == bookmark,
                "Imported selected C was confused with actual Entry owner A.");
            CheckEntry(heads, bookmark, 0x11);
            Require(heads.ReadHead(bookmark.HeadId).Unwrap().Id == sourceLatest, "Source advanced incorrectly before resume.");
            first = heads.PublishHead(selected, new byte[] { 0x11 }, out var outcome).Unwrap(); Confirmed(outcome);
            second = heads.PublishHead(selected, new byte[] { 0x11 }, out outcome).Unwrap(); Confirmed(outcome);
            Require(first.HeadId == selected && second.HeadId == selected && first != second && first != bookmark,
                "Two same-value saves must be distinct real Entries on selected C.");
            Require(heads.ReadHead(bookmark.HeadId).Unwrap().Id == sourceLatest, "Writing imported C changed source A.");
            CheckEntry(heads, bookmark, 0x11);
            HeadEntryId[] expected = [second, first, bookmark, initial];
            Require(ExactHistory(heads, second).SequenceEqual(expected), "Exact C history did not continue at A's old point.");

            // Imported bookmark remains usable by Fork after a completed history callback.
            HeadId oldFork = heads.ForkHead(bookmark, out outcome).Unwrap(); Confirmed(outcome);
            Require(CurrentHistory(heads, oldFork).SequenceEqual(new[] { bookmark, initial }), "Fork expanded the bookmarked prefix.");
            HeadId grandchild = heads.ForkHead(second, out outcome).Unwrap(); Confirmed(outcome);
            Require(CurrentHistory(heads, grandchild).SequenceEqual(expected), "Fork and exact Entry history disagree.");
            HeadEntryId grandchildSave = heads.PublishHead(grandchild, new byte[] { 0x20 }, out outcome).Unwrap(); Confirmed(outcome);
            Require(ExactHistory(heads, grandchildSave).SequenceEqual(new[] { grandchildSave }.Concat(expected)),
                "Multi-level fork history lost actual Entry identities.");

            var collected = new List<HeadEntryId>();
            HeadEntryId anchor = second;
            pages = 0;
            emptyPages = 0;
            while (true) {
                bool skipAnchor = pages != 0;
                int delivered = 0, added = 0;
                HeadHistoryEnd end = heads.ReadEntryHistory(anchor, new(2, 256), entry => {
                    delivered++;
                    if (skipAnchor && delivered == 1) {
                        Require(entry.Id == anchor, "Continuation did not start inclusively at its anchor.");
                        return true;
                    }
                    collected.Add(entry.Id);
                    anchor = entry.Id;
                    added++;
                    return false;
                }).Unwrap();
                pages++;
                if (added == 0) { emptyPages++; }
                if (end == HeadHistoryEnd.Complete) { break; }
                Require(end == HeadHistoryEnd.VisitorStopped && added == 1 && pages <= expected.Length,
                    "Paging neither completed nor made bounded progress.");
                if (pages == 1) {
                    // Later C suffix must not change the fixed start or the subsequent inclusive continuation.
                    heads.PublishHead(selected, new byte[] { 0x13 }, out outcome).Unwrap(); Confirmed(outcome);
                }
            }
            Require(collected.SequenceEqual(expected) && pages == 5 && emptyPages == 1,
                "Inclusive paging duplicated, omitted, or absorbed a newer save.");
            Require(heads.ReadHead(bookmark.HeadId).Unwrap().Id == sourceLatest, "Read/Fork/history composition changed A.");
        }
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data")))
        using (var heads = Heads.OpenReadOnly(Path.Combine(root, "heads"), data)) {
            CheckEntry(heads, bookmark, 0x11);
            CheckEntry(heads, first, 0x11);
            CheckEntry(heads, second, 0x11);
            Require(ExactHistory(heads, second).SequenceEqual(new[] { second, first, bookmark, initial }),
                "Cold exact history changed after later publication.");
            HeadStoreAudit audit = heads.Audit().Unwrap();
            Require(audit.HeadCount == 4 && audit.EntryCount == 7 && audit.OriginCount == 3,
                "Persistent Handle scenario produced unexpected physical inventory.");
        }
        Emit(new { status = "ok", scenario = "handle-read", pid = Environment.ProcessId,
            headHex = Convert.ToHexString(headBytes), entryHex = Convert.ToHexString(entryBytes),
            decodedBeforeOpen = true, selectedDiffersFromEntryOwner = selected != bookmark.HeadId,
            samePayloadDistinctEntries = first != second, sourceUnchanged = true, oldEntryStable = true,
            pages, emptyPages, exactHistoryEntries = 4, headCount = 4, entryCount = 7 });
    }

    private static HeadEntryId[] ExactHistory(Heads heads, HeadEntryId entryId) {
        var entries = new List<HeadEntryId>();
        Require(heads.ReadEntryHistory(entryId, new(16, 512), entry => { entries.Add(entry.Id); return true; }).Unwrap()
            == HeadHistoryEnd.Complete, "Exact history did not complete.");
        return entries.ToArray();
    }

    private static HeadEntryId[] CurrentHistory(Heads heads, HeadId headId) {
        var entries = new List<HeadEntryId>();
        Require(heads.ReadHeadHistory(headId, new(16, 512), entry => { entries.Add(entry.Id); return true; }).Unwrap()
            == HeadHistoryEnd.Complete, "Current history did not complete.");
        return entries.ToArray();
    }

    private static void CheckEntry(Heads heads, HeadEntryId id, byte expected) {
        HeadEntry entry = heads.ReadEntry(id).Unwrap();
        Require(entry.Id == id && entry.Payload.SequenceEqual(new[] { expected }), "Exact old Entry identity or bytes changed.");
    }
    private static byte[] Encode(HeadEntryId id) {
        var bytes = new byte[HeadEntryId.EncodedSize];
        Require(id.TryWrite(bytes), "Entry could not encode without its owner.");
        return bytes;
    }
    private static void Confirmed(PublicationOutcome outcome) => Require(outcome == PublicationOutcome.Confirmed, "Publication did not confirm.");
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
    private static void Emit(object message) => Console.WriteLine(JsonSerializer.Serialize(message));
    private sealed record Manifest(int EncodingVersion, string HeadKind, string EntryKind, string DataId, string StoreId,
        string Initial, string SourceLatest);
}
