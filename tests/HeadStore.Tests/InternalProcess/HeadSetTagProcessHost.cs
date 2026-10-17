using System.Text.Json;
using Atelia.Rbf.Internal;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.InternalProcess;

// The parent terminates the child inside real publication windows. No file edits
// or managed unwind produce the interrupted image; this is ProcessCrashOnly.
internal static class HeadSetTagProcessHost {
    private const string Name = "set-tag/cu-and-fork";
    private static readonly byte[] A = [0xa2, 0x00, 0xff];
    private static readonly byte[] B = [0xb2, 0x80];
    private static readonly byte[] Later = [0xfe, 0x03];

    internal static int Run(string[] args) {
        string root = args[1];
        switch (args[0]) {
            case "set-tag.seed": Seed(root); break;
            case "set-tag.interrupt": Interrupt(root, args[2]); break;
            case "set-tag.verify": Verify(root, args[2], args[3]); break;
            case "set-tag.advance": Advance(root); break;
            default: throw new ArgumentException("Unknown HeadSetTag process command.");
        }
        return 0;
    }

    private static void Seed(string root) {
        Directory.CreateDirectory(root);
        Coordinates coordinates;
        using (var data = DataStore.Create(InternalProcessHost.DataPath(root)))
        using (var store = Store.Create(InternalProcessHost.VersionPath(root), data, 4)) {
            HeadEntryId a = store.CreateHead(1, [0xa1], out var outcome).Unwrap();
            Confirmed(outcome);
            HeadEntryId b = store.CreateHead(2, [0xb1], out outcome).Unwrap();
            Confirmed(outcome);
            var group = store.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [a.HeadId] = A, [b.HeadId] = B
            }, out outcome).Unwrap();
            Confirmed(outcome);
            HeadId child = store.ForkHead(3, group[a.HeadId], out outcome).Unwrap();
            Confirmed(outcome);
            store.CreateHead(4, [0xcc], out outcome).Unwrap();
            Confirmed(outcome);
            coordinates = new(group[a.HeadId].Ticket.Packed, group[b.HeadId].Ticket.Packed, store.ReadHead(child).Unwrap().Id.Ticket.Packed);
        }
        File.WriteAllText(Path.Combine(root, "set-tag-coordinates.json"), JsonSerializer.Serialize(coordinates));
        InternalProcessHost.Complete("set-tag.seed", new { heads = 4, entries = 5, origins = 1 });
    }

    private static void Interrupt(string root, string scenario) {
        Require(scenario is "dependencies-confirmed" or "record-appended" or "confirmed", "Unknown tag window.");
        using var data = DataStore.Open(InternalProcessHost.DataPath(root));
        var flushes = new Dictionary<uint, int>();
        int bucketFlushes = 0;
        bool dependenciesConfirmed = false;
        bool recordAppended = false;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        string bucketPath = InternalProcessHost.TagPath(root, Name);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            AfterFlush = request => {
                for (uint id = 1; id <= 4; id++) {
                    if (SamePath(request.Path, InternalProcessHost.HeadPath(root, id))) {
                        flushes[id] = flushes.GetValueOrDefault(id) + 1;
                    }
                }
                if (SamePath(request.Path, bucketPath)) { bucketFlushes++; }
            }
        };
        void Park() => InternalProcessHost.Park("set-tag.interrupt", new {
            scenario, dependenciesConfirmed, recordAppended, bucketFlushes, outcome = outcome.ToString(),
            confirmedHeadIds = flushes.Keys.Order().ToArray(),
            dependencyFlushCounts = Enumerable.Range(1, 4).Select(id => flushes.GetValueOrDefault((uint)id)).ToArray(),
            resultDelivered = false
        });
        var hooks = new HeadStoreTestHooks {
            AfterTagDependenciesConfirmed = () => {
                Require(outcome == PublicationOutcome.NotAttempted, "Tag output began before dependencies were confirmed.");
                Require(flushes.Count == 3 && Enumerable.Range(1, 3).All(id => flushes.GetValueOrDefault((uint)id) == 1)
                    && !flushes.ContainsKey(4), "Dependency confirmation did not include exactly selected hosts and the CU peer.");
                Require(!File.Exists(bucketPath), "A new bucket was published before the dependency checkpoint.");
                dependenciesConfirmed = true;
                if (scenario == "dependencies-confirmed") { Park(); }
            },
            AfterTagRecordAppended = _ => {
                Require(dependenciesConfirmed && bucketFlushes == 0 && outcome == PublicationOutcome.Unknown,
                    "Complete TagRecord checkpoint crossed the bucket flush or lost its outcome.");
                recordAppended = true;
                if (scenario == "record-appended") { Park(); }
            },
            AfterPublicationConfirmed = () => {
                Require(dependenciesConfirmed && recordAppended && bucketFlushes == 1 && outcome == PublicationOutcome.Confirmed,
                    "Confirmed was not installed after the real bucket flush and before delivery.");
                if (scenario == "confirmed") { Park(); }
            }
        };
        try {
            using var store = HeadStoreFactory.Open(InternalProcessHost.VersionPath(root), data, false, testHooks: hooks);
            HeadId a = store.ReadHead(1).Unwrap().Id.HeadId;
            HeadId child = store.ListHeads().Unwrap().Single(id => id.GetLocalId(store) == 3);
            store.CreateTag(Name, [child, a], out outcome).Unwrap();
            throw new InvalidOperationException("CreateTag returned without reaching its kill checkpoint.");
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    private static void Advance(string root) {
        using (var data = DataStore.Open(InternalProcessHost.DataPath(root)))
        using (var store = Store.Open(InternalProcessHost.VersionPath(root), data)) {
            for (uint id = 1; id <= 3; id++) {
                store.PublishHead(store.ListHeads().Unwrap().Single(head => head.GetLocalId(store) == id), Later, out var outcome).Unwrap();
                Confirmed(outcome);
            }
        }
        File.WriteAllText(Path.Combine(root, "set-tag-advanced"), "three later ordinary publications");
        InternalProcessHost.Complete("set-tag.advance", new { updated = 3 });
    }

    private static void Verify(string root, string scenario, string mode) {
        bool readOnly = mode == "ro";
        Require(readOnly || mode == "rw", "Invalid verification owner mode.");
        bool expectedTag = scenario != "dependencies-confirmed";
        bool advanced = File.Exists(Path.Combine(root, "set-tag-advanced"));
        Coordinates expected = JsonSerializer.Deserialize<Coordinates>(File.ReadAllText(Path.Combine(root, "set-tag-coordinates.json")))!;
        using (var data = readOnly ? DataStore.OpenReadOnly(InternalProcessHost.DataPath(root)) : DataStore.Open(InternalProcessHost.DataPath(root)))
        using (var store = readOnly ? Store.OpenReadOnly(InternalProcessHost.VersionPath(root), data) : Store.Open(InternalProcessHost.VersionPath(root), data)) {
            var tag = store.ReadTag(Name);
            HeadEntry a = store.ReadHead(1).Unwrap();
            HeadEntry b = store.ReadHead(2).Unwrap();
            HeadEntry child = store.ReadHead(3).Unwrap();
            Require(a.Payload.SequenceEqual(advanced ? Later : A) && b.Payload.SequenceEqual(advanced ? Later : B)
                && child.Payload.SequenceEqual(advanced ? Later : A), "Cold current Entries differ.");
            if (expectedTag) {
                HeadSetTag value = tag.Unwrap();
                Require(value.Entries.Count == 2 && value.Entries.Keys.Select(id => id.GetLocalId(store)).Order().SequenceEqual(new uint[] { 1, 3 }),
                    "A necessary CU peer or unrelated Head leaked into the selected table.");
                HeadEntryId savedA = value.Entries[a.Id.HeadId];
                HeadEntryId savedChild = value.Entries[store.ListHeads().Unwrap().Single(id => id.GetLocalId(store) == 3)];
                Require(savedA.Ticket.Packed == expected.A && savedChild.Ticket.Packed == expected.Child
                    && savedA == savedChild, "Shared source coordinates changed or selected child identity was lost.");
                Require(store.ReadEntry(savedA).Unwrap().Payload.SequenceEqual(A)
                    && store.ReadEntry(savedChild).Unwrap().Payload.SequenceEqual(A), "Exact tagged history failed after cold reopen.");
                Require(store.ReadTagInfo(Name).Unwrap().EntryCount == 2, "Cold lightweight member count differs.");
                if (advanced) { Require(a.Id != savedA && child.Id != savedChild, "Later publication did not leave the tag fixed."); }
            }
            else {
                Require(tag.IsFailure && tag.Error!.ErrorCode == "HeadStore.TagNotFound", "Pre-output interruption exposed a tag.");
            }
            int scanned = 0;
            Require(store.ScanTags(info => { scanned++; Require(info.EntryCount == 2, "Scan member count differs."); return true; }).Unwrap()
                == HeadSetTagScanEnd.Complete && scanned == (expectedTag ? 1 : 0), "Cold tag scan differs.");
            if (readOnly) {
                var audit = store.Audit().Unwrap();
                Require(audit.HeadCount == 4 && audit.EntryCount == (advanced ? 8 : 5) && audit.OriginCount == 1
                    && audit.TagCount == (expectedTag ? 1 : 0), "Cold full Audit did not validate the selected dependencies.");
            }
        }
        InternalProcessHost.Complete("set-tag.verify", new { scenario, mode, found = expectedTag, advanced, exactMembers = expectedTag, audit = readOnly });
    }

    private static bool SamePath(string? left, string right) => left is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void Confirmed(PublicationOutcome outcome) => Require(outcome == PublicationOutcome.Confirmed, "Seed publication did not confirm.");
    private static void Require(bool condition, string message) => InternalProcessHost.Require(condition, message);
    private sealed record Coordinates(ulong A, ulong B, ulong Child);
}
