using Atelia.Rbf.Internal;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.InternalProcess;

// A root and two header-only children join one CU. Member qualification must
// inspect actual member records without recursively resolving their old values.
internal static class ReferenceForkProcessHost {
    internal static int Run(string[] args) {
        switch (args[0]) {
            case "reference-fork.seed": Seed(args[1]); break;
            case "reference-fork.interrupt": Interrupt(args[1], args[2]); break;
            case "reference-fork.verify": Verify(args[1], args[2], args[3] == "ro"); break;
            default: throw new ArgumentException("Unknown reference-fork command.");
        }
        return 0;
    }

    private static void Seed(string root) {
        Directory.CreateDirectory(root);
        using (var data = DataStore.Create(InternalProcessHost.DataPath(root)))
        using (var store = Store.Create(InternalProcessHost.VersionPath(root), data, 3)) {
            HeadEntryId source = store.CreateHead(1, [0x71], out var outcome).Unwrap();
            Require(outcome == PublicationOutcome.Confirmed, "Root was not confirmed.");
            for (uint id = 2; id <= 3; id++) {
                HeadId child = store.ForkHead(id, source, out outcome).Unwrap();
                Require(outcome == PublicationOutcome.Confirmed && store.ReadHead(child).Unwrap().Id == source,
                    "Header-only fork did not retain its actual source Entry.");
            }
        }
        InternalProcessHost.Complete("reference-fork.seed", new { heads = 3, entries = 1 });
    }

    private static void Interrupt(string root, string scenario) {
        Require(scenario is "first-end" or "all-ends" or "flush-1" or "flush-2" or "flush-3" or "confirmed",
            "Unknown first CU checkpoint.");
        using var data = DataStore.Open(InternalProcessHost.DataPath(root));
        int completeEnds = 0, durableFlushes = 0;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        void Park() => InternalProcessHost.Park("reference-fork.interrupt", new {
            scenario, completeEnds, durableFlushes, observedOutcome = outcome.ToString(), resultDelivered = false
        });
        var hooks = new HeadStoreTestHooks {
            AfterHeadRecordAppended = _ => {
                completeEnds++;
                if ((scenario == "first-end" && completeEnds == 1) || (scenario == "all-ends" && completeEnds == 3)) {
                    Require(durableFlushes == 0, "A member flushed before every CU End.");
                    Park();
                }
            },
            AfterPublicationConfirmed = () => {
                Require(completeEnds == 3 && durableFlushes == 3 && outcome == PublicationOutcome.Confirmed,
                    "First CU confirmation lacked completed member and flush evidence.");
                if (scenario == "confirmed") { Park(); }
            }
        };
        string[] targets = Enumerable.Range(1, 3)
            .Select(id => Path.GetFullPath(InternalProcessHost.HeadPath(root, (uint)id))).ToArray();
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            AfterFlush = request => {
                if (request.Path is null || !targets.Contains(request.Path, StringComparer.OrdinalIgnoreCase)) { return; }
                durableFlushes++;
                Require(completeEnds == 3, "First CU member flushed before all complete Ends.");
                if (scenario == $"flush-{durableFlushes}") { Park(); }
            }
        };
        try {
            using var store = HeadStoreFactory.Open(InternalProcessHost.VersionPath(root), data, false, testHooks: hooks);
            // The selected HeadIds are targets, even though all old Entries belong to Head 1.
            var changes = store.ListHeads().Unwrap().ToDictionary(id => id,
                id => (ReadOnlyMemory<byte>)new byte[] { 0x72, (byte)id.LocalId });
            store.PublishHeads(changes, out outcome).Unwrap();
            throw new InvalidOperationException("First CU returned without reaching the checkpoint.");
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    private static void Verify(string root, string scenario, bool readOnly) {
        bool partial = scenario == "first-end";
        using (var data = readOnly ? DataStore.OpenReadOnly(InternalProcessHost.DataPath(root)) : DataStore.Open(InternalProcessHost.DataPath(root)))
        using (var store = readOnly ? Store.OpenReadOnly(InternalProcessHost.VersionPath(root), data) : Store.Open(InternalProcessHost.VersionPath(root), data)) {
            var origins = store.ListHeadOrigins(8).Unwrap();
            Require(origins.Count == 2 && origins.All(origin => origin.SourceHeadId.LocalId == 1), "Fork declarations changed.");
            var source = origins[0];
            Require(origins.All(origin => origin.SourceEntryTicket == source.SourceEntryTicket), "Fork sources split.");
            foreach (HeadId id in store.ListHeads().Unwrap()) {
                HeadEntry current = store.ReadHead(id).Unwrap();
                Require(partial
                    ? current.Id.HeadId == source.SourceHeadId && current.Id.Ticket == source.SourceEntryTicket
                        && current.Payload.SequenceEqual(new byte[] { 0x71 })
                    : current.Id.HeadId == id && current.Payload.SequenceEqual(new byte[] { 0x72, (byte)id.LocalId }),
                    "First CU cold current failed to choose either exact source or complete new group.");
                var history = new List<HeadEntry>();
                Require(store.ReadHeadHistory(id, new(16, 256), entry => { history.Add(entry); return true; }).Unwrap()
                    == HeadHistoryEnd.Complete && history.Count == (partial ? 1 : 2), "First CU history gained a clone or exposed a partial group.");
                Require(history[0].Id == current.Id && history[^1].Id.HeadId == source.SourceHeadId
                    && history[^1].Id.Ticket == source.SourceEntryTicket, "First CU inherited prefix differs.");
            }
            if (readOnly) {
                var audit = store.Audit().Unwrap();
                Require(audit.HeadCount == 3 && audit.EntryCount == (partial ? 1 : 4) && audit.OriginCount == 2,
                    "First CU Audit counted a clone or accepted a partial group.");
            }
        }
        InternalProcessHost.Complete("reference-fork.verify", new {
            scenario, readOnly, allOld = partial, allNew = !partial, historyChecked = true, auditChecked = readOnly
        });
    }

    private static void Require(bool condition, string message) => InternalProcessHost.Require(condition, message);
}
