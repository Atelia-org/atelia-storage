using Atelia.Rbf.Internal;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.InternalProcess;

/// <summary>生产根/data/RBF 的具名 CU 窗口；READY 后无返回或 Dispose 路径，父进程实际 Kill。</summary>
internal static class ConditionalUpdateProcessHost {
    private const long RecordedAt = 1_801_000_000_123;
    internal static byte[] Old(uint id) => [(byte)id, 0x10, 0x11];
    internal static byte[] New(uint id) => Enumerable.Range(0, 128 + (int)id * 4).Select(i => (byte)(id * 32 + (uint)i)).ToArray();
    private static bool IsPartial(string scenario) => scenario == "first-end";

    internal static int Run(string[] args) {
        switch (args[0]) {
            case "cu-seed": Seed(args[1]); break;
            case "cu-interrupt": Interrupt(args[1], args[2]); break;
            case "cu-verify": Verify(args[1], args[2]); break;
            case "cu-fork": Fork(args[1], args[2]); break;
            default: throw new ArgumentException($"Unknown ConditionalUpdate process command {args[0]}.");
        }
        return 0;
    }

    private static void Seed(string root) {
        Directory.CreateDirectory(root);
        using (var data = DataStore.Create(InternalProcessHost.DataPath(root)))
        using (var store = Store.Create(InternalProcessHost.VersionPath(root), data, 256)) {
            for (uint id = 1; id <= 3; id++) {
                store.CreateHead(id, Old(id), out PublicationOutcome outcome).Unwrap();
                InternalProcessHost.Require(outcome == PublicationOutcome.Confirmed, "CU seed was not confirmed.");
            }
        }
        InternalProcessHost.Complete("cu-seed", new { members = 3 });
    }

    private static void Interrupt(string root, string scenario) {
        InternalProcessHost.Require(scenario is "first-end" or "all-ends" or "flush-1" or "flush-2" or "flush-3" or "confirmed",
            "Unknown CU checkpoint.");
        using var data = DataStore.Open(InternalProcessHost.DataPath(root));
        int completeEnds = 0;
        int durableFlushes = 0;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        var tickets = new List<ulong>();
        var hooks = new HeadStoreTestHooks {
            GetRecordedAtUnixMs = () => RecordedAt,
            AfterHeadRecordAppended = ticket => {
                completeEnds++;
                tickets.Add(ticket.Packed);
                if ((scenario == "first-end" && completeEnds == 1) || (scenario == "all-ends" && completeEnds == 3)) {
                    InternalProcessHost.Require(durableFlushes == 0, "CU member was flushed before the End checkpoint.");
                    InternalProcessHost.Park("cu-interrupt", new {
                        scenario, completeEnds, durableFlushes, tickets, endReturned = true, resultDelivered = false,
                        observedOutcome = outcome.ToString()
                    });
                }
            },
            AfterPublicationConfirmed = () => {
                InternalProcessHost.Require(completeEnds == 3 && durableFlushes == 3 && outcome == PublicationOutcome.Confirmed,
                    "CU confirmation preceded its complete End/flush evidence.");
                if (scenario == "confirmed") {
                    InternalProcessHost.Park("cu-interrupt", new {
                        scenario, completeEnds, durableFlushes, tickets, confirmedRecorded = true,
                        resultDelivered = false, observedOutcome = outcome.ToString()
                    });
                }
            }
        };
        var headPaths = Enumerable.Range(1, 3).Select(id => Path.GetFullPath(InternalProcessHost.HeadPath(root, (uint)id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            AfterFlush = request => {
                if (request.Path is null || !headPaths.Contains(request.Path)) { return; }
                durableFlushes++;
                InternalProcessHost.Require(completeEnds == 3, "Native ref flush began before every CU End completed.");
                if (scenario == $"flush-{durableFlushes}") {
                    InternalProcessHost.Park("cu-interrupt", new {
                        scenario, completeEnds, durableFlushes, tickets, nativeFlushReturned = true,
                        resultDelivered = false, observedOutcome = outcome.ToString()
                    });
                }
            }
        };
        try {
            using var store = HeadStoreFactory.Open(InternalProcessHost.VersionPath(root), data, false, testHooks: hooks);
            var changes = Enumerable.Range(1, 3).ToDictionary(id => store.ReadHead((uint)id).Unwrap().Id.HeadId,
                id => (ReadOnlyMemory<byte>)New((uint)id));
            store.PublishHeads(changes, out outcome).Unwrap();
            throw new InvalidOperationException("CU returned instead of parking inside the named checkpoint.");
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    private static void Verify(string root, string scenario) {
        using (var data = DataStore.OpenReadOnly(InternalProcessHost.DataPath(root)))
        using (var store = Store.OpenReadOnly(InternalProcessHost.VersionPath(root), data)) {
            bool partial = IsPartial(scenario);
            var entryIds = new Dictionary<uint, HeadEntryId>();
            for (uint id = 1; id <= 3; id++) {
                byte[] expected = partial ? Old(id) : New(id);
                var current = store.ReadHead(id).Unwrap();
                entryIds.Add(id, current.Id);
                InternalProcessHost.Require(current.Payload.SequenceEqual(expected), "Cold CU current differs.");
                if (!partial) { InternalProcessHost.Require(current.RecordedAtUnixMs == RecordedAt, "CU timestamp was not retained."); }
                var history = History(store, current.Id.HeadId);
                InternalProcessHost.Require(history.Count == (partial ? 1 : 2), "Cold CU history exposed a partial group or lost a complete group.");
                InternalProcessHost.Require(history[0].Id == current.Id && history[^1].Payload.SequenceEqual(Old(id)),
                    "Cold CU history/current ordering differs.");
            }
            bool hasFork = File.Exists(InternalProcessHost.HeadPath(root, 4));
            var forks = store.ListHeadOrigins(10).Unwrap();
            InternalProcessHost.Require(forks.Count == (hasFork ? 1 : 0), "Cold CU fork declarations differ.");
            if (hasFork) {
                var fork = store.ReadHead(4u).Unwrap();
                InternalProcessHost.Require(fork.Payload.SequenceEqual(partial ? Old(1) : New(1)), "CU fork did not resolve its exact source root bytes.");
                InternalProcessHost.Require(forks[0].ChildHeadId.GetLocalId(store) == 4 && forks[0].SourceHeadId == entryIds[1].HeadId
                    && forks[0].SourceEntryTicket == entryIds[1].Ticket, "CU fork lost its exact checked source.");
                InternalProcessHost.Require(History(store, store.ListHeads().Unwrap().Single(id => id.GetLocalId(store) == 4)).Count == (partial ? 1 : 2), "CU fork inherited history differs.");
                HeadSetTag captured = store.ReadTag("after-cu-reopen").Unwrap();
                InternalProcessHost.Require(captured.Entries.Count == 2 && captured.Entries[entryIds[1].HeadId] == entryIds[1]
                    && captured.Entries[store.ListHeads().Unwrap().Single(id => id.GetLocalId(store) == 4)] == entryIds[1], "Cold tag did not retain the accepted CU and fork positions.");
                InternalProcessHost.Require(store.ReadEntry(captured.Entries[entryIds[1].HeadId]).Unwrap().Payload.SequenceEqual(partial ? Old(1) : New(1)),
                    "Cold tag member lost its exact accepted Entry.");
            }
            var audit = store.Audit().Unwrap();
            InternalProcessHost.Require(audit.HeadCount == (hasFork ? 4 : 3) && audit.EntryCount == (partial ? 3 : 6)
                && audit.OriginCount == (hasFork ? 1 : 0) && audit.TagCount == (hasFork ? 1 : 0), "Cold CU Audit disagrees with published history.");
        }
        InternalProcessHost.Complete("cu-verify", new { scenario, allOld = IsPartial(scenario), allNew = !IsPartial(scenario),
            currentChecked = true, historyChecked = true, forksChecked = true, auditChecked = true });
    }

    private static void Fork(string root, string scenario) {
        using (var data = DataStore.Open(InternalProcessHost.DataPath(root)))
        using (var store = Store.Open(InternalProcessHost.VersionPath(root), data)) {
            var source = store.ReadHead(1u).Unwrap();
            HeadId fork = store.ForkHead(4u, source.Id, out PublicationOutcome outcome).Unwrap();
            InternalProcessHost.Require(outcome == PublicationOutcome.Confirmed && fork.GetLocalId(store) == 4, "Fresh CU fork was not confirmed.");
            var child = store.ReadHead(fork).Unwrap();
            InternalProcessHost.Require(child.Id == source.Id && child.Payload.SequenceEqual(IsPartial(scenario) ? Old(1) : New(1)), "Fork source is not the exact accepted CU Entry.");
            // The old child was killed before/inside CU flush. This fresh request
            // captures the accepted cold state and confirms all required peers.
            HeadSetTag captured = store.CreateTag("after-cu-reopen", [source.Id.HeadId, fork], out outcome).Unwrap();
            InternalProcessHost.Require(outcome == PublicationOutcome.Confirmed && captured.Entries.Count == 2
                && captured.Entries[source.Id.HeadId] == source.Id && captured.Entries[fork] == source.Id,
                "Tag creation after cold CU recovery changed its selected member set.");
        }
        InternalProcessHost.Complete("cu-fork", new { scenario, freshRequest = true, exactSource = true, referenceOnly = true });
    }

    private static List<HeadEntry> History(Store store, HeadId id) {
        var entries = new List<HeadEntry>();
        InternalProcessHost.Require(store.ReadHeadHistory(id, new(100, 10_000), value => { entries.Add(value); return true; }).Unwrap()
            == HeadHistoryEnd.Complete, "Cold CU history did not complete.");
        return entries;
    }
}
