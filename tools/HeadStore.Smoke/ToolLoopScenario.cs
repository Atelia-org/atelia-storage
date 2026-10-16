using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using HeadOwner = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Smoke;

/// <summary>A single application attempt, persisted and recovered through public APIs.</summary>
internal static class ToolLoopScenario {
    private const int SchemaVersion = 1;
    private const uint ApplicationHead = 1;
    private const uint RuntimeTag = 0x544C5231;
    private const uint HistoryTag = 0x544C4831;
    private const uint TasksTag = 0x544C5431;
    private const uint ContextTag = 0x544C4331;
    private const string Prepared = "prepared";
    private const string Started = "started";
    private const string ResultPublished = "result";
    private const string ResultCheckpoint = "result-published";
    private const string FrozenRequest = "{\"tool\":\"mock-add\",\"operands\":[20,22],\"note\":\"冻结请求😀\"}";
    private const string ExpectedResult = "42";
    private const string ApplicationContext = "tool-loop/public-consumer/v1";
    private static readonly string[] RootKeys = ["context", "history", "runtime", "tasks"];
    private static readonly HeadHistoryBudget Budget = new(16, 256);

    internal static int Run(string[] args) {
        if (args.Length < 2) { throw new ArgumentException("Expected a tool command and application root."); }
        string root = Path.GetFullPath(args[1]);
        switch (args[0]) {
            case "tool-worker" when args.Length == 3:
                Worker(root, ReadCheckpoint(args[2]));
                break;
            case "tool-recover" when args.Length == 2:
                Recover(root);
                break;
            case "tool-read" when args.Length == 2:
                Read(root);
                break;
            default:
                throw new ArgumentException("Unknown tool command or invalid argument count.");
        }
        return 0;
    }

    private static void Worker(string root, string checkpoint) {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) {
            throw new ArgumentException("tool-worker requires an absent or empty application root.");
        }
        Directory.CreateDirectory(root);
        using var data = DataStore.Create(DataPath(root));
        using var heads = HeadOwner.Create(VersionPath(root), data, reservedHeadIdUpperBound: 1);
        var context = new ContextRecord(SchemaVersion, Convert.ToBase64String(data.StoreId),
            Convert.ToBase64String(heads.HeadStoreId), ApplicationContext);
        var task = new TaskRecord(SchemaVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            FrozenRequest, RequestHash(FrozenRequest));
        var preparedRuntime = new RuntimeRecord(SchemaVersion, Prepared, 0, 0, null);
        var preparedHistory = new HistoryRecord(SchemaVersion, [Event(Prepared, task, null)]);
        byte[] preparedPayload = WriteEntry(data, preparedRuntime, preparedHistory, task, context, null);
        PublicationOutcome preparedOutcome = PublicationOutcome.NotAttempted;
        HeadEntryId entryId = heads.CreateHead(ApplicationHead, preparedPayload, out preparedOutcome).Unwrap();
        RequireConfirmed(preparedOutcome);
        ApplicationSnapshot current = ReadSingleEntry(data, heads, heads.ReadHead(entryId.HeadId).Unwrap());

        var startedRuntime = new RuntimeRecord(SchemaVersion, Started, 0, 0, null);
        var startedHistory = new HistoryRecord(SchemaVersion, [.. preparedHistory.Events, Event(Started, task, null)]);
        byte[] startedPayload = WriteEntry(data, startedRuntime, startedHistory, task, context, current);
        PublicationOutcome startedOutcome = PublicationOutcome.NotAttempted;
        _ = heads.PublishHead(entryId.HeadId, startedPayload, out startedOutcome).Unwrap();
        RequireConfirmed(startedOutcome);
        current = ReadSingleEntry(data, heads, heads.ReadHead(entryId.HeadId).Unwrap());

        // The first external dispatch is reachable only after the live Started
        // call returns normally with Confirmed. A cold read grants no such proof.
        BackendReceipt? receipt = null;
        PublicationOutcome resultOutcome = PublicationOutcome.NotAttempted;
        if (checkpoint != Started) {
            receipt = DispatchToMockBackend(root, current);
        }
        if (checkpoint == ResultCheckpoint) {
            BackendReceipt completed = receipt ?? throw new InvalidDataException("The mock backend did not return a receipt.");
            PublishResult(data, heads, current, completed, out resultOutcome);
            current = ReadSingleEntry(data, heads, heads.ReadHead(entryId.HeadId).Unwrap());
        }
        HistoryEvidence history = ReadHistory(data, heads, current);
        EmitReport("ready", "tool-worker", current, history, current.Runtime.Phase, "checkpoint", 0,
            receipt?.DispatchCount ?? 0, receipt is null ? "none" : "receipt", checkpoint == ResultCheckpoint ? 3 : 2,
            checkpoint, startedOutcome.ToString(), resultOutcome.ToString());

        // EOF and input are failures. The parent must verify READY, kill this
        // live owner process, wait for its exit, and then start a cold command.
        string? input = Console.ReadLine();
        throw new InvalidOperationException(input is null
            ? "tool-worker reached EOF before the parent killed it."
            : "tool-worker must be killed, not released through stdin.");
    }

    private static void Recover(string root) {
        using var data = DataStore.Open(DataPath(root));
        using var heads = HeadOwner.Open(VersionPath(root), data);
        ApplicationSnapshot current = ReadCurrent(data, heads);
        HistoryEvidence history = ReadHistory(data, heads, current);
        if (current.Runtime.Phase == ResultPublished) {
            // Published application state is sufficient. In particular, a lost
            // external receipt cannot trigger another execution or settlement.
            EmitReport("ok", "tool-recover", current, history, ResultPublished, "already-result", 0,
                current.Runtime.DispatchCount, "published-result", 0);
            return;
        }
        Require(current.Runtime.Phase == Started, "Cold recovery requires a Started or published Result phase.");
        BackendReceipt? receipt = QueryMockBackend(root, current);
        if (receipt is null) {
            // Absence of a receipt does not establish that execution never took
            // place. Keep the existing Started entry and expose uncertainty.
            EmitReport("ok", "tool-recover", current, history, "uncertain", "uncertain", 1, 0, "none", 0);
            return;
        }

        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        PublishResult(data, heads, current, receipt, out outcome);
        current = ReadCurrent(data, heads);
        history = ReadHistory(data, heads, current);
        EmitReport("ok", "tool-recover", current, history, ResultPublished, "query-result", 1,
            current.Runtime.DispatchCount, "published-result", 1, resultOutcome: outcome.ToString());
    }

    private static void Read(string root) {
        using var data = DataStore.OpenReadOnly(DataPath(root));
        using var heads = HeadOwner.OpenReadOnly(VersionPath(root), data);
        Require(data.IsReadOnly && heads.IsReadOnly, "tool-read requires both read-only owners.");
        ApplicationSnapshot current = ReadCurrent(data, heads);
        HistoryEvidence history = ReadHistory(data, heads, current);
        int queryCount = 0;
        int dispatchCount = current.Runtime.DispatchCount;
        string evidence = current.Runtime.Phase == ResultPublished ? "published-result" : "none";
        string disposition = current.Runtime.Phase == ResultPublished ? "already-result" : "prepared-needs-new-attempt";
        if (current.Runtime.Phase == Started) {
            BackendReceipt? receipt = QueryMockBackend(root, current);
            queryCount = 1;
            dispatchCount = receipt?.DispatchCount ?? 0;
            evidence = receipt is null ? "none" : "receipt";
            disposition = receipt is null ? "uncertain" : "query-result";
        }
        HeadStoreAudit audit = heads.Audit().Unwrap();
        Require(audit.HeadCount == 1 && audit.EntryCount == history.Phases.Length
            && audit.OriginCount == 0 && audit.TagCount == 0, "The read-only audit differs from application history.");
        EmitReport("ok", "tool-read", current, history, current.Runtime.Phase, disposition,
            queryCount, dispatchCount, evidence, 0);
    }

    private static void PublishResult(DataStore data, HeadOwner heads, ApplicationSnapshot current,
        BackendReceipt receipt, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        Require(current.Runtime.Phase == Started, "Only the existing Started attempt can acquire its result.");
        ValidateReceipt(receipt, current); // All backend rejection precedes Append and PublishHead.
        var runtime = new RuntimeRecord(SchemaVersion, ResultPublished, receipt.DispatchCount, 1, receipt.Result);
        var history = new HistoryRecord(SchemaVersion,
            [.. current.History.Events, Event(ResultPublished, current.Task, receipt.Result)]);
        byte[] payload = WriteEntry(data, runtime, history, current.Task, current.Context, current);
        _ = heads.PublishHead(current.Id.HeadId, payload, out outcome).Unwrap();
        RequireConfirmed(outcome);
    }

    private static BackendReceipt DispatchToMockBackend(string root, ApplicationSnapshot current) {
        Require(current.Runtime.Phase == Started, "The mock backend requires a Started attempt.");
        var receipt = new BackendReceipt(SchemaVersion, current.Context.DataStoreId, current.Context.HeadStoreId,
            current.Task.OperationId, current.Task.AttemptId, current.Task.Request, current.Task.RequestSha256,
            ExpectedResult, 1);
        ValidateReceipt(receipt, current);
        // A separate fixture file models an external backend's durable answer.
        // It is deliberately outside both stores and is not VS authority. This
        // mock accepts one dispatch; recovery only queries it and never calls here.
        using var stream = new FileStream(ReceiptPath(root), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(receipt));
        stream.Flush(flushToDisk: true);
        return receipt;
    }

    private static BackendReceipt? QueryMockBackend(string root, ApplicationSnapshot current) {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(ReceiptPath(root)); }
        catch (FileNotFoundException) { return null; }
        BackendReceipt receipt = JsonSerializer.Deserialize<BackendReceipt>(bytes)
            ?? throw new InvalidDataException("The mock backend receipt is null.");
        ValidateReceipt(receipt, current);
        return receipt;
    }

    private static void ValidateReceipt(BackendReceipt receipt, ApplicationSnapshot current) {
        Require(receipt.SchemaVersion == SchemaVersion && receipt.DataStoreId == current.Context.DataStoreId
            && receipt.HeadStoreId == current.Context.HeadStoreId,
            "The mock backend receipt is bound to a different store identity.");
        Require(receipt.OperationId == current.Task.OperationId && receipt.AttemptId == current.Task.AttemptId,
            "The mock backend receipt is bound to a different operation or attempt identity.");
        Require(receipt.Request == current.Task.Request && receipt.RequestSha256 == current.Task.RequestSha256,
            "The mock backend receipt does not match the exact frozen request.");
        Require(receipt.Result == ExpectedResult && receipt.DispatchCount == 1,
            "The mock backend receipt has an invalid result or dispatch count.");
    }

    private static ApplicationSnapshot ReadCurrent(DataStore data, HeadOwner heads) {
        IReadOnlyList<HeadId> refs = heads.ListHeads().Unwrap();
        Require(refs.Count == 1 && refs[0].LocalId == ApplicationHead, "The application must own exactly its fixed ref.");
        return ReadSingleEntry(data, heads, heads.ReadHead(ApplicationHead).Unwrap());
    }

    private static byte[] WriteEntry(DataStore data, RuntimeRecord runtime, HistoryRecord history,
        TaskRecord task, ContextRecord context, ApplicationSnapshot? previous) {
        var frames = new Dictionary<string, FrameAddress>(StringComparer.Ordinal) {
            ["runtime"] = AppendRecord(data, RuntimeTag, runtime),
            ["history"] = AppendRecord(data, HistoryTag, history),
            ["tasks"] = previous is null ? AppendRecord(data, TasksTag, task) : previous.Roots["tasks"],
            ["context"] = previous is null ? AppendRecord(data, ContextTag, context) : previous.Roots["context"]
        };
        return NamedFrameMapCodec.Encode(frames).Unwrap();
    }

    private static FrameAddress AppendRecord<T>(DataStore data, uint tag, T value) =>
        data.Append(tag, JsonSerializer.SerializeToUtf8Bytes(value)).Unwrap();

    private static T ReadRecord<T>(DataStore data, FrameAddress address, uint tag) where T : class {
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        Require(frame.Address == address && frame.Tag == tag && frame.TailMetaLength == 0 && !frame.IsTombstone,
            "The application data record has an invalid frame envelope.");
        return JsonSerializer.Deserialize<T>(frame.PayloadAndMeta)
            ?? throw new InvalidDataException("The application data record is null.");
    }

    private static ApplicationSnapshot ReadSingleEntry(DataStore data, HeadOwner heads, HeadEntry entry) {
        IReadOnlyDictionary<string, FrameAddress> frames = NamedFrameMapCodec.Decode(entry.Payload).Unwrap();
        Require(frames.Keys.Order(StringComparer.Ordinal).SequenceEqual(RootKeys), "The application NamedFrameMap must contain exactly four frames.");
        var state = new ApplicationSnapshot(entry.Id, frames,
            ReadRecord<RuntimeRecord>(data, frames["runtime"], RuntimeTag),
            ReadRecord<HistoryRecord>(data, frames["history"], HistoryTag),
            ReadRecord<TaskRecord>(data, frames["tasks"], TasksTag),
            ReadRecord<ContextRecord>(data, frames["context"], ContextTag));
        ValidateState(data, heads, state);
        return state;
    }

    private static void ValidateState(DataStore data, HeadOwner heads, ApplicationSnapshot state) {
        Require(state.Runtime.SchemaVersion == SchemaVersion && state.History.SchemaVersion == SchemaVersion
            && state.Task.SchemaVersion == SchemaVersion && state.Context.SchemaVersion == SchemaVersion,
            "The application schema version differs.");
        Require(state.Context.DataStoreId == Convert.ToBase64String(data.StoreId)
            && state.Context.HeadStoreId == Convert.ToBase64String(heads.HeadStoreId)
            && state.Context.ApplicationContext == ApplicationContext, "The application context has the wrong store binding.");
        Require(Guid.TryParseExact(state.Task.OperationId, "D", out _)
            && Guid.TryParseExact(state.Task.AttemptId, "D", out _), "The frozen attempt identity is invalid.");
        Require(state.Task.Request == FrozenRequest && state.Task.RequestSha256 == RequestHash(FrozenRequest),
            "The frozen request differs from the application request.");
        string[] phases = ExpectedPhases(state.Runtime.Phase);
        HistoryEvent[] events = state.History.Events ?? throw new InvalidDataException("The application history is null.");
        Require(events.Length == phases.Length,
            "The application phase differs from its business history.");
        for (int i = 0; i < phases.Length; i++) {
            HistoryEvent entry = events[i] ?? throw new InvalidDataException("An application history event is null.");
            Require(entry.Phase == phases[i] && entry.OperationId == state.Task.OperationId
                && entry.AttemptId == state.Task.AttemptId && entry.RequestSha256 == state.Task.RequestSha256
                && entry.Result == (phases[i] == ResultPublished ? ExpectedResult : null),
                "A business history event differs from the frozen attempt.");
        }
        bool completed = state.Runtime.Phase == ResultPublished;
        Require(state.Runtime.DispatchCount == (completed ? 1 : 0)
            && state.Runtime.SettlementCount == (completed ? 1 : 0)
            && state.Runtime.Result == (completed ? ExpectedResult : null), "The runtime result or counters differ from its phase.");
    }

    private static HistoryEvidence ReadHistory(DataStore data, HeadOwner heads, ApplicationSnapshot current) {
        var entries = new List<ApplicationSnapshot>();
        HeadHistoryEnd end = heads.ReadHeadHistory(current.Id.HeadId, Budget, entry => {
            ApplicationSnapshot historical = ReadSingleEntry(data, heads, entry);
            Require(historical.Task == current.Task && historical.Context == current.Context
                && historical.Roots["tasks"] == current.Roots["tasks"]
                && historical.Roots["context"] == current.Roots["context"], "A publication changed the frozen task or context.");
            entries.Add(historical);
            return true;
        }).Unwrap();
        Require(end == HeadHistoryEnd.Complete, "The bounded application history did not complete.");
        entries.Reverse();
        string[] phases = entries.Select(state => state.Runtime.Phase).ToArray();
        Require(phases.SequenceEqual(ExpectedPhases(current.Runtime.Phase)), "An attempt phase was republished or skipped.");
        Require(entries[^1].Id == current.Id, "The history head differs from the current entryId.");
        object[] evidence = entries.Select(state => (object)new {
            phase = state.Runtime.Phase, ticket = state.Id.Ticket.Packed,
            dispatchCount = state.Runtime.DispatchCount, settlementCount = state.Runtime.SettlementCount,
            businessEvents = state.History.Events.Length, rootCount = state.Roots.Count,
            operationId = state.Task.OperationId, attemptId = state.Task.AttemptId, requestSha256 = state.Task.RequestSha256
        }).ToArray();
        return new HistoryEvidence(phases, evidence);
    }

    private static void EmitReport(string kind, string command, ApplicationSnapshot state, HistoryEvidence history,
        string phase, string recoveryAction, int backendQueryCount, int dispatchCount, string dispatchEvidence,
        int publishedThisRun, string? checkpoint = null, string? startedOutcome = null, string? resultOutcome = null) {
        Console.WriteLine(JsonSerializer.Serialize(new {
            kind, status = kind == "ready" ? "ready" : "ok", command, scenario = command, pid = Environment.ProcessId,
            checkpoint, phase, storedPhase = state.Runtime.Phase, recoveryAction, recoveryDisposition = recoveryAction,
            backendQueryCount, dispatchCount, persistedDispatchCount = state.Runtime.DispatchCount, dispatchEvidence,
            externalExecutionKnown = dispatchEvidence != "none", settlementCount = state.Runtime.SettlementCount,
            publishedThisRun, entryCount = history.Phases.Length, publicationCount = history.Phases.Length,
            startedPublications = history.Phases.Count(value => value == Started),
            resultPublications = history.Phases.Count(value => value == ResultPublished),
            historyPhases = history.Phases, historyEntries = history.Entries,
            rootKeys = state.Roots.Keys.Order(StringComparer.Ordinal).ToArray(),
            operationId = state.Task.OperationId, attemptId = state.Task.AttemptId,
            request = state.Task.Request, requestSha256 = state.Task.RequestSha256,
            dataStoreId = state.Context.DataStoreId, headStoreId = state.Context.HeadStoreId,
            result = state.Runtime.Result, startedOutcome, resultOutcome,
            backendReceiptAuthority = "external-mock-only", publicBoundary = "after-call-before-owner-dispose"
        }));
        Console.Out.Flush();
    }

    private static HistoryEvent Event(string phase, TaskRecord task, string? result) =>
        new(phase, task.OperationId, task.AttemptId, task.RequestSha256, result);
    private static string[] ExpectedPhases(string phase) => phase switch {
        Prepared => [Prepared],
        Started => [Prepared, Started],
        ResultPublished => [Prepared, Started, ResultPublished],
        _ => throw new InvalidDataException("The application phase is unknown.")
    };
    private static string ReadCheckpoint(string checkpoint) => checkpoint is Started or "external-completed" or ResultCheckpoint
        ? checkpoint : throw new ArgumentException("Expected started, external-completed, or result-published checkpoint.");
    private static string RequestHash(string request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
    private static string DataPath(string root) => Path.Combine(root, "data");
    private static string VersionPath(string root) => Path.Combine(root, "heads");
    private static string ReceiptPath(string root) => Path.Combine(root, "tool-backend-receipt.json");
    private static void RequireConfirmed(PublicationOutcome outcome) =>
        Require(outcome == PublicationOutcome.Confirmed, "A successful public publication did not report Confirmed.");
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private sealed record RuntimeRecord(int SchemaVersion, string Phase, int DispatchCount, int SettlementCount, string? Result);
    private sealed record HistoryRecord(int SchemaVersion, HistoryEvent[] Events);
    private sealed record HistoryEvent(string Phase, string OperationId, string AttemptId, string RequestSha256, string? Result);
    private sealed record TaskRecord(int SchemaVersion, string OperationId, string AttemptId, string Request, string RequestSha256);
    private sealed record ContextRecord(int SchemaVersion, string DataStoreId, string HeadStoreId, string ApplicationContext);
    private sealed record BackendReceipt(int SchemaVersion, string DataStoreId, string HeadStoreId,
        string OperationId, string AttemptId, string Request, string RequestSha256, string Result, int DispatchCount);
    private sealed record ApplicationSnapshot(HeadEntryId Id, IReadOnlyDictionary<string, FrameAddress> Roots,
        RuntimeRecord Runtime, HistoryRecord History, TaskRecord Task, ContextRecord Context);
    private sealed record HistoryEvidence(string[] Phases, object[] Entries);
}
