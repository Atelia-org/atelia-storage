using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Storage;
using Atelia.VersionStore.Internal.Tags;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.InternalProcess;

// This entry point belongs to the test assembly and its existing IVT identity.
// It is deliberately separate from the public VersionStore.Smoke consumer.
internal static class InternalProcessHost {
    internal const uint SourceId = 7;
    internal const uint ForkId = 8;
    internal const uint AutomaticId = 257;
    internal const long RecordedAt = 1_800_123_456_789;
    internal static readonly byte[] Initial = [0xa7, 1, 0xfa];
    internal static readonly byte[] Current = [0xa7, 2, 0xfb];
    internal static readonly byte[] Created = [0xf7, 3, 0xfc, 0x10];
    internal static readonly byte[] Appended = [0xf7, 4, 0xfd, 0x11];
    internal static readonly byte[] Conflict = [0xcc, 0x42, 0xee];
    internal const string SeedTag = "v4f-existing-0";

    public static int Main(string[] args) {
        try {
            if (args.Length < 3 || args[0] != "v4f-internal") {
                throw new ArgumentException("Expected v4f-internal <command> <root> [scenario].");
            }
            args = args[1..];
            if (args[0].StartsWith("tail.", StringComparison.Ordinal)) {
                return InternalTailHost.Run(args);
            }
            switch (args[0]) {
                case "seed": Seed(args[1]); break;
                case "interrupt": Interrupt(args[1], args[2]); break;
                case "verify": Verify(args[1], args[2], args[3]); break;
                case "retry": Retry(args[1], args[2]); break;
                case "conflict": CreateConflict(args[1]); break;
                case "verify-conflict": VerifyConflict(args[1]); break;
                case "recover-conflict": RecoverConflict(args[1]); break;
                case "verify-conflict-recovered": VerifyConflictRecovered(args[1]); break;
                default: throw new ArgumentException($"Unknown internal process command {args[0]}.");
            }
            return 0;
        }
        catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static string DataPath(string root) => Path.Combine(root, "data");
    internal static string VersionPath(string root) => Path.Combine(root, "versions");
    internal static string PendingPath(string root) => Path.Combine(VersionPath(root), "creating", "pending.rbf");
    internal static string RefPath(string root, uint id) => Path.Combine(VersionPath(root), VersionStorePaths.GetRefRelativePath(id));
    internal static string TagPath(string root, string name) => Path.Combine(VersionPath(root), "tags", $"{TagNameCodec.GetBucketId(name):x8}.rbf");

    internal static string NewTag {
        get {
            for (int i = 0; ; i++) {
                string name = $"v4f-new-{i}";
                if (TagNameCodec.GetBucketId(name) != TagNameCodec.GetBucketId(SeedTag)) { return name; }
            }
        }
    }

    internal static string ExistingBucketTag {
        get {
            for (int i = 1; ; i++) {
                string name = $"v4f-existing-{i}";
                if (TagNameCodec.GetBucketId(name) == TagNameCodec.GetBucketId(SeedTag)) { return name; }
            }
        }
    }

    internal static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    internal static void Complete(string command, object facts) {
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(new { pid = Environment.ProcessId, command, facts }));
        Console.Out.Flush();
    }

    [DoesNotReturn]
    internal static void Park(string command, object facts) {
        Console.WriteLine("READY " + JsonSerializer.Serialize(new { pid = Environment.ProcessId, command, facts }));
        Console.Out.Flush();
        // There is no release path, stdin EOF path, managed exception or normal Dispose.
        Thread.Sleep(Timeout.Infinite);
        throw new InvalidOperationException("An infinite checkpoint unexpectedly returned.");
    }

    private static void Seed(string root) {
        Directory.CreateDirectory(root);
        using (var data = DataStore.Create(DataPath(root)))
        using (var store = Store.Create(VersionPath(root), data, 256)) {
            RefRevision initial = store.CreateRef(SourceId, Initial, out var outcome).Unwrap();
            Require(outcome == PublicationOutcome.Confirmed, "Seed initial was not confirmed.");
            store.PublishRef(initial.RefId, Current, out outcome).Unwrap();
            Require(outcome == PublicationOutcome.Confirmed, "Seed head was not confirmed.");
            store.CreateTag(SeedTag, Initial, out outcome).Unwrap();
            Require(outcome == PublicationOutcome.Confirmed, "Seed tag was not confirmed.");
        }
        Complete("seed", new { sourceId = SourceId });
    }

    internal static RefRevision OldSource(Store store) {
        RefRevision initial = default;
        RefId source = store.ReadRef(SourceId).Unwrap().Revision.RefId;
        store.ReadRefHistory(source, new HistoryBudget(10, 100), snapshot => {
            if (snapshot.Payload.SequenceEqual(Initial)) { initial = snapshot.Revision; }
            return true;
        }).Unwrap();
        Require(initial != default, "The exact old source was not issued by checked history.");
        return initial;
    }

    private static void Interrupt(string root, string scenario) {
        using var data = DataStore.Open(DataPath(root));
        Store? store = null;
        int pendingFlushes = 0;
        int targetFlushes = 0;
        string targetPath = scenario == "tag-append" ? TagPath(root, ExistingBucketTag) : RefPath(root, SourceId);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            AfterFlush = request => {
                if (request.Path == PendingPath(root)) { pendingFlushes++; }
                if (request.Path == targetPath) { targetFlushes++; }
            }
        };
        var hooks = new VersionStoreTestHooks { GetRecordedAtUnixMs = () => RecordedAt };
        bool fork = scenario.StartsWith("fork-", StringComparison.Ordinal);
        uint targetId = fork ? ForkId : AutomaticId;
        byte[] expected = fork ? Initial : Created;
        hooks.BeforeNewRefRename = () => {
            if (!scenario.EndsWith("-private", StringComparison.Ordinal)) { return; }
            Require(pendingFlushes == 1, "Private ref did not execute its real durable flush.");
            Require(!File.Exists(RefPath(root, targetId)), "Target exists before rename.");
            ProvePendingClosed(root);
            Require(StoreIdentity.TryRead(store!.VersionStoreId, out var identity), "Bad store identity.");
            using (var pending = RbfFile.OpenReadOnlyExisting(PendingPath(root), RbfCacheMode.Off)) {
                var snapshot = RefRecordReader.ReadCurrent(pending, identity, RefId.Create(identity, targetId)).Unwrap();
                Require(snapshot.Payload.SequenceEqual(expected), "Private Snapshot differs from its request.");
            }
            Park("interrupt", new { scenario, targetId, pendingFlushes, pendingClosed = true, renameReturned = false });
        };
        hooks.AfterPublicationConfirmed = () => {
            if (!scenario.EndsWith("-renamed", StringComparison.Ordinal)) { return; }
            Require(pendingFlushes == 1, "Published ref did not execute private durable flush.");
            Require(!File.Exists(PendingPath(root)) && File.Exists(RefPath(root, targetId)), "Rename did not publish the formal file.");
            Park("interrupt", new { scenario, targetId, pendingFlushes, renameReturned = true, resultDelivered = false });
        };
        hooks.BeforeNewTagBucketRename = () => {
            if (scenario != "tag-private") { return; }
            Require(pendingFlushes == 1, "Private tag header did not execute its real durable flush.");
            Require(!File.Exists(TagPath(root, NewTag)), "Formal tag bucket already exists.");
            ProvePendingClosed(root);
            Require(StoreIdentity.TryRead(store!.VersionStoreId, out var identity), "Bad store identity.");
            using (var pending = RbfFile.OpenReadOnlyExisting(PendingPath(root), RbfCacheMode.Off)) {
                var header = TagRecordReader.CheckHeader(pending, identity, TagNameCodec.GetBucketId(NewTag)).Unwrap();
                Require(pending.TailOffset == header.RecordsOffset, "Private tag file contains more than its complete header.");
            }
            Park("interrupt", new { scenario, pendingFlushes, pendingClosed = true, renameReturned = false });
        };
        hooks.AfterTagBucketPublished = () => {
            if (scenario != "tag-empty") { return; }
            Require(pendingFlushes == 1, "Empty bucket did not execute its private durable flush.");
            Require(!File.Exists(PendingPath(root)) && File.Exists(TagPath(root, NewTag)), "Empty bucket rename did not return.");
            Park("interrupt", new { scenario, pendingFlushes, renameReturned = true, firstAppendStarted = false });
        };
        hooks.AfterRefRecordAppended = ticket => {
            Require(scenario == "ref-append", "Unexpected ref Append checkpoint.");
            Require(targetFlushes == 0, "Formal ref was flushed before the checkpoint.");
            Park("interrupt", new { scenario, ticket = ticket.Packed, appendReturned = true, targetFlushes, physicalLength = new FileInfo(targetPath).Length });
        };
        hooks.AfterTagRecordAppended = ticket => {
            Require(scenario == "tag-append", "Unexpected tag Append checkpoint.");
            Require(targetFlushes == 0, "Existing tag bucket was flushed before the checkpoint.");
            Park("interrupt", new { scenario, ticket = ticket.Packed, appendReturned = true, targetFlushes, physicalLength = new FileInfo(targetPath).Length });
        };
        using (store = VersionStoreFactory.Open(VersionPath(root), data, false, testHooks: hooks)) {
            switch (scenario) {
                case "create-auto-private":
                case "create-auto-renamed": store.CreateRef(Created, out _).Unwrap(); break;
                case "fork-specified-private":
                case "fork-specified-renamed": store.ForkRef(ForkId, OldSource(store), out _).Unwrap(); break;
                case "tag-private":
                case "tag-empty": store.CreateTag(NewTag, Created, out _).Unwrap(); break;
                case "ref-append": store.PublishRef(store.ReadRef(SourceId).Unwrap().Revision.RefId, Appended, out _).Unwrap(); break;
                case "tag-append": store.CreateTag(ExistingBucketTag, Appended, out _).Unwrap(); break;
                default: throw new ArgumentException($"Unknown checkpoint {scenario}.");
            }
            throw new InvalidOperationException("Mutation returned instead of parking inside its checkpoint.");
        }
    }

    private static void ProvePendingClosed(string root) {
        // An exclusive reopen independently witnesses that the former private handle is closed.
        using var probe = new FileStream(PendingPath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Require(probe.Length > 0, "Private file is empty.");
    }

    private static void Verify(string root, string scenario, string mode) {
        bool readOnly = mode is "ro-before" or "ro-after";
        Require(readOnly || mode == "rw", "Bad verification mode.");
        bool privateFile = scenario.EndsWith("-private", StringComparison.Ordinal);
        using (var data = readOnly ? DataStore.OpenReadOnly(DataPath(root)) : DataStore.Open(DataPath(root)))
        using (var store = readOnly ? Store.OpenReadOnly(VersionPath(root), data) : Store.Open(VersionPath(root), data)) {
            Require(File.Exists(PendingPath(root)) == (privateFile && mode == "ro-before"), "Pending preservation/cancellation differs.");
            uint target = scenario.StartsWith("fork-", StringComparison.Ordinal) ? ForkId : AutomaticId;
            uint[] expectedIds = scenario.EndsWith("-renamed", StringComparison.Ordinal) ? [SourceId, target] : [SourceId];
            Require(store.ListRefs().Unwrap().Select(id => id.LocalId).Order().SequenceEqual(expectedIds.Order()), "Formal ref membership differs.");
            var current = store.ReadRef(SourceId).Unwrap();
            Require(current.Payload.SequenceEqual(scenario == "ref-append" ? Appended : Current), "Source/current payload differs.");
            if (scenario == "ref-append") {
                Require(current.RecordedAtUnixMs == RecordedAt, "Full ref Append mixed payload and record metadata.");
            }
            if (scenario.EndsWith("-renamed", StringComparison.Ordinal)) {
                var created = store.ReadRef(target).Unwrap();
                Require(created.Payload.SequenceEqual(target == ForkId ? Initial : Created), "Created/forked payload differs.");
                Require(created.RecordedAtUnixMs == RecordedAt, "Created/forked record metadata differs.");
                var forks = store.ListForks(10).Unwrap();
                if (target == ForkId) {
                    Require(forks.Count == 1, "Fork declaration missing.");
                    var exact = OldSource(store);
                    Require(forks[0].ChildRefId.LocalId == ForkId && forks[0].SourceRefId == exact.RefId
                        && forks[0].SourceSnapshotTicket == exact.Ticket, "Fork declaration lost the exact old source.");
                }
                else { Require(forks.Count == 0, "Ordinary creation gained fork origin."); }
            }
            Require(store.ResolveTag(SeedTag).Unwrap().Bytes.SequenceEqual(Initial), "Existing seed tag changed.");
            if (scenario is "tag-private" or "tag-empty") {
                Require(store.ResolveTag(NewTag).Error is TagNotFoundError, "Unappended tag did not produce healthy missing.");
                Require(store.ReadTagInfo(NewTag).Error is TagNotFoundError, "Unappended tag metadata did not produce healthy missing.");
                Require(File.Exists(TagPath(root, NewTag)) == (scenario == "tag-empty"), "Private/empty bucket formal state differs.");
                if (scenario == "tag-empty") {
                    // Public Audit independently accepts and counts the legal header-only bucket.
                    if (store.IsReadOnly) {
                        var audit = store.Audit().Unwrap();
                        Require(audit.TagBucketCount == 2 && audit.TagCount == 1, "Header-only bucket audit counts differ.");
                    }
                }
            }
            if (scenario == "tag-append") {
                Require(store.ResolveTag(ExistingBucketTag).Unwrap().Bytes.SequenceEqual(Appended), "Full tag Append was not preserved.");
                var info = store.ReadTagInfo(ExistingBucketTag).Unwrap();
                Require(info.Name == ExistingBucketTag && info.RecordedAtUnixMs == RecordedAt
                    && info.RelatedRefId is null && info.RelatedTicket is null,
                    "Full tag Append mixed payload and record metadata.");
            }
        }
        Complete("verify", new { scenario, mode, pending = File.Exists(PendingPath(root)) });
    }

    private static void Retry(string root, string scenario) {
        using (var data = DataStore.Open(DataPath(root)))
        using (var store = Store.Open(VersionPath(root), data)) {
            PublicationOutcome outcome;
            if (scenario == "create-auto-private") {
                Require(store.CreateRef(Created, out outcome).Unwrap().RefId.LocalId == AutomaticId, "Canceled auto creation burned its id.");
            }
            else if (scenario == "fork-specified-private") {
                Require(store.ForkRef(ForkId, OldSource(store), out outcome).Unwrap().RefId.LocalId == ForkId, "Canceled fork did not permit fresh creation.");
            }
            else {
                Require(scenario is "tag-private" or "tag-empty", "Only private/empty requests can be retried.");
                store.CreateTag(NewTag, Created, out outcome).Unwrap();
                Require(store.ResolveTag(NewTag).Unwrap().Bytes.SequenceEqual(Created), "Fresh tag request was not published.");
            }
            Require(outcome == PublicationOutcome.Confirmed, "Fresh request was not confirmed.");
            Require(!File.Exists(PendingPath(root)), "Fresh request left private residue.");
        }
        Complete("retry", new { scenario, freshRequest = true });
    }

    private static void CreateConflict(string root) {
        using var data = DataStore.Open(DataPath(root));
        Store? store = null;
        byte[]? targetBytes = null;
        var hooks = new VersionStoreTestHooks {
            BeforeNewRefRename = () => {
                Require(StoreIdentity.TryRead(store!.VersionStoreId, out var identity), "Bad store identity.");
                Span<byte> header = stackalloc byte[RefHeaderCodec.NoOriginPayloadSize];
                Require(RefHeaderCodec.TryWrite(identity, RefId.Create(identity, AutomaticId), null, header), "Cannot encode conflict header.");
                Span<byte> meta = stackalloc byte[RecordMetadataCodec.EncodedSize];
                RecordMetadataCodec.TryWrite(123, meta);
                using (var target = RbfFile.CreateNew(RefPath(root, AutomaticId), RbfCacheMode.Off)) {
                    target.Append(RefRecordCodec.HeaderTag, header).Unwrap();
                    target.Append(RefRecordCodec.SnapshotTag, Conflict, meta).Unwrap();
                    target.DurableFlush();
                }
                targetBytes = File.ReadAllBytes(RefPath(root, AutomaticId));
            }
        };
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        bool rejected = false;
        using (store = VersionStoreFactory.Open(VersionPath(root), data, false, testHooks: hooks)) {
            try { store.CreateRef(Created, out outcome).Unwrap(); }
            catch (IOException) { rejected = true; }
            Require(rejected && outcome == PublicationOutcome.Unknown, "VS creation did not reject its occupied publication target.");
            Require(targetBytes is not null && File.ReadAllBytes(RefPath(root, AutomaticId)).SequenceEqual(targetBytes), "Conflict target was overwritten.");
            Require(File.Exists(PendingPath(root)), "Failed publication consumed the private request.");
            bool faultGuard = false;
            try { store.ListRefs(); }
            catch (InvalidOperationException) { faultGuard = true; }
            Require(faultGuard, "Failed publication left the original owner usable.");
            data.EnsureUsable();
        }
        Complete("conflict", new { targetId = AutomaticId, outcome = outcome.ToString(), targetUnchanged = true,
            rejectionStage = "platform-target-preflight", nativeRenameAttempted = false, ownerFaulted = true });
    }

    private static void VerifyConflict(string root) {
        using (var data = DataStore.OpenReadOnly(DataPath(root)))
        using (var store = Store.OpenReadOnly(VersionPath(root), data)) {
            Require(store.ListRefs().Unwrap().Select(id => id.LocalId).Order().SequenceEqual(new[] { SourceId, AutomaticId }), "Conflict target identity was not discoverable.");
            Require(store.ReadRef(AutomaticId).Unwrap().Payload.SequenceEqual(Conflict), "Conflict target content was replaced.");
            Require(store.ReadRef(SourceId).Unwrap().Payload.SequenceEqual(Current), "Source changed during conflict.");
            Require(File.Exists(PendingPath(root)), "RO consumed conflict pending.");
        }
        Complete("verify-conflict", new { targetId = AutomaticId, targetUnchanged = true });
    }

    private static void RecoverConflict(string root) {
        byte[] before = File.ReadAllBytes(RefPath(root, AutomaticId));
        using (var data = DataStore.Open(DataPath(root)))
        using (var store = Store.Open(VersionPath(root), data)) {
            Require(!File.Exists(PendingPath(root)), "RW did not cancel the failed private request.");
            Require(store.ReadRef(AutomaticId).Unwrap().Payload.SequenceEqual(Conflict), "RW adopted the failed request over the actual formal target.");
            RefRevision fresh = store.CreateRef(Appended, out var outcome).Unwrap();
            Require(outcome == PublicationOutcome.Confirmed && fresh.RefId.LocalId == AutomaticId + 1,
                "RW high watermark did not come from the actual formal set.");
        }
        Require(File.ReadAllBytes(RefPath(root, AutomaticId)).SequenceEqual(before), "RW changed the conflict target.");
        Complete("recover-conflict", new { targetId = AutomaticId, pendingCanceled = true, freshId = AutomaticId + 1 });
    }

    private static void VerifyConflictRecovered(string root) {
        using (var data = DataStore.OpenReadOnly(DataPath(root)))
        using (var store = Store.OpenReadOnly(VersionPath(root), data)) {
            Require(!File.Exists(PendingPath(root)), "Canceled conflict request reappeared.");
            Require(store.ReadRef(AutomaticId).Unwrap().Payload.SequenceEqual(Conflict), "Conflict target changed after fresh publication.");
            Require(store.ReadRef(AutomaticId + 1).Unwrap().Payload.SequenceEqual(Appended), "Fresh automatic request differs.");
            Require(store.ListRefs().Unwrap().Select(id => id.LocalId).Order()
                .SequenceEqual(new[] { SourceId, AutomaticId, AutomaticId + 1 }), "Recovered formal set differs.");
        }
        Complete("verify-conflict-recovered", new { targetId = AutomaticId, pending = false });
    }
}
