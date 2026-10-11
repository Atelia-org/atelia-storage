using Atelia.Rbf.Internal;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.InternalProcess;

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
                store.CreateRef(id, Old(id), out PublicationOutcome outcome).Unwrap();
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
        var hooks = new VersionStoreTestHooks {
            GetRecordedAtUnixMs = () => RecordedAt,
            AfterRefRecordAppended = ticket => {
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
        var refPaths = Enumerable.Range(1, 3).Select(id => Path.GetFullPath(InternalProcessHost.RefPath(root, (uint)id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            AfterFlush = request => {
                if (request.Path is null || !refPaths.Contains(request.Path)) { return; }
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
            using var store = VersionStoreFactory.Open(InternalProcessHost.VersionPath(root), data, false, testHooks: hooks);
            var changes = Enumerable.Range(1, 3).ToDictionary(id => store.ReadRef((uint)id).Unwrap().Revision.RefId,
                id => (ReadOnlyMemory<byte>)New((uint)id));
            store.PublishRefs(changes, out outcome).Unwrap();
            throw new InvalidOperationException("CU returned instead of parking inside the named checkpoint.");
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    private static void Verify(string root, string scenario) {
        using (var data = DataStore.OpenReadOnly(InternalProcessHost.DataPath(root)))
        using (var store = Store.OpenReadOnly(InternalProcessHost.VersionPath(root), data)) {
            bool partial = IsPartial(scenario);
            var revisions = new Dictionary<uint, RefRevision>();
            for (uint id = 1; id <= 3; id++) {
                byte[] expected = partial ? Old(id) : New(id);
                var current = store.ReadRef(id).Unwrap();
                revisions.Add(id, current.Revision);
                InternalProcessHost.Require(current.Payload.SequenceEqual(expected), "Cold CU current differs.");
                if (!partial) { InternalProcessHost.Require(current.RecordedAtUnixMs == RecordedAt, "CU timestamp was not retained."); }
                var history = History(store, current.Revision.RefId);
                InternalProcessHost.Require(history.Count == (partial ? 1 : 2), "Cold CU history exposed a partial group or lost a complete group.");
                InternalProcessHost.Require(history[0].Revision == current.Revision && history[^1].Payload.SequenceEqual(Old(id)),
                    "Cold CU history/current ordering differs.");
            }
            bool hasFork = File.Exists(InternalProcessHost.RefPath(root, 4));
            var forks = store.ListForks(10).Unwrap();
            InternalProcessHost.Require(forks.Count == (hasFork ? 1 : 0), "Cold CU fork declarations differ.");
            if (hasFork) {
                var fork = store.ReadRef(4u).Unwrap();
                InternalProcessHost.Require(fork.Payload.SequenceEqual(partial ? Old(1) : New(1)), "CU fork cloned Members instead of root bytes.");
                InternalProcessHost.Require(forks[0].ChildRefId.LocalId == 4 && forks[0].SourceRefId == revisions[1].RefId
                    && forks[0].SourceSnapshotTicket == revisions[1].Ticket, "CU fork lost its exact checked source.");
                InternalProcessHost.Require(History(store, fork.Revision.RefId).Count == (partial ? 2 : 3), "CU fork inherited history differs.");
            }
            var audit = store.Audit().Unwrap();
            InternalProcessHost.Require(audit.RefCount == (hasFork ? 4 : 3) && audit.SnapshotCount == (partial ? 3 : 6) + (hasFork ? 1 : 0)
                && audit.ForkCount == (hasFork ? 1 : 0), "Cold CU Audit disagrees with published history.");
        }
        InternalProcessHost.Complete("cu-verify", new { scenario, allOld = IsPartial(scenario), allNew = !IsPartial(scenario),
            currentChecked = true, historyChecked = true, forksChecked = true, auditChecked = true });
    }

    private static void Fork(string root, string scenario) {
        using (var data = DataStore.Open(InternalProcessHost.DataPath(root)))
        using (var store = Store.Open(InternalProcessHost.VersionPath(root), data)) {
            var source = store.ReadRef(1u).Unwrap();
            RefRevision fork = store.ForkRef(4u, source.Revision, out PublicationOutcome outcome).Unwrap();
            InternalProcessHost.Require(outcome == PublicationOutcome.Confirmed && fork.RefId.LocalId == 4, "Fresh CU fork was not confirmed.");
            var child = store.ReadRef(fork.RefId).Unwrap();
            InternalProcessHost.Require(child.Payload.SequenceEqual(IsPartial(scenario) ? Old(1) : New(1)), "Fork copied a CU envelope.");
        }
        InternalProcessHost.Complete("cu-fork", new { scenario, freshRequest = true, exactSource = true, clonedOnlyRoot = true });
    }

    private static List<RefBytesSnapshot> History(Store store, RefId id) {
        var snapshots = new List<RefBytesSnapshot>();
        InternalProcessHost.Require(store.ReadRefHistory(id, new(100, 10_000), value => { snapshots.Add(value); return true; }).Unwrap()
            == HistoryEnd.Complete, "Cold CU history did not complete.");
        return snapshots;
    }
}
