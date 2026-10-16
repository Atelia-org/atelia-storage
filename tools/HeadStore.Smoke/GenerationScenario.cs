using System.Security.Cryptography;
using System.Text.Json;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Smoke;

/// <summary>Controlled task completions; one driver owns all public storage calls.</summary>
internal static class GenerationScenario {
    private const int Schema = 1;
    private const string OperationId = "merge-operation/42";
    private const string WrongOperationId = "merge-operation/previous";
    private const uint RuntimeTag = 0x474E5231;
    private const uint HistoryTag = 0x474E4831;
    private const uint TasksTag = 0x474E5431;
    private const uint ContextTag = 0x474E4331;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HeadHistoryBudget Budget = new(16, 256);

    internal static int Run(string[] args) {
        if (args.Length != 2 || args[0] is not ("generation-write" or "generation-read")) {
            throw new ArgumentException("Usage: generation-write <fresh-root> | generation-read <root>");
        }
        string root = Path.GetFullPath(args[1]);
        if (args[0] == "generation-write") { Write(root); }
        else { Read(root); }
        return 0;
    }

    private static void Write(string root) {
        Require(!File.Exists(root) && (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()),
            "generation-write requires an absent or empty application root.");
        Directory.CreateDirectory(root);
        Summary summary;
        using (var data = DataStore.Create(Path.Combine(root, "data"))) {
            using var heads = Heads.Create(Path.Combine(root, "heads"), data, reservedHeadIdUpperBound: 1);
            var driver = new Driver(data, heads);
            Call[] first = driver.StartGeneration(1);
            driver.Receive(Injected(Complete(first[0].Request, WrongOperationId)));
            driver.Receive(first[0].Complete());
            driver.Receive(first[0].Task); // Same-generation duplicate, before any replacement.
            Call[] second = driver.StartGeneration(2);
            driver.Receive(first[1].Complete()); // Correct operationId, superseded generation.
            driver.Receive(Injected(Complete(second[1].Request, WrongOperationId)));
            driver.Receive(second[1].Complete()); // B finishes first; A's frozen request predates B.
            driver.Receive(second[1].Task);
            driver.Receive(first[0].Task); // Correct operationId and already completed old generation.
            driver.Receive(second[0].Complete()); // Must merge B from the latest head.
            Require(driver.Dispatched == 4 && driver.Received == 9 && driver.Rejected == 6,
                "The driver did not dispatch/consume the prescribed task vector.");
            summary = ValidateStore(data, heads);
            Require(summary.Dispatched == driver.Dispatched && summary.Rejected == driver.Rejected,
                "Published application counters differ from real driver decisions.");
        }
        Emit("generation-write", summary);
    }

    private static void Read(string root) {
        Summary summary;
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data"))) {
            using var heads = Heads.OpenReadOnly(Path.Combine(root, "heads"), data);
            Require(data.IsReadOnly && heads.IsReadOnly, "Cold reader must use read-only owners.");
            summary = ValidateStore(data, heads);
            HeadStoreAudit audit = heads.Audit().Unwrap();
            Require(audit.HeadCount == 1 && audit.EntryCount == 5 && audit.OriginCount == 0
                && audit.TagBucketCount == 0 && audit.TagCount == 0, "Unexpected physical version inventory.");
        }
        Emit("generation-read", summary);
    }

    private sealed class Driver {
        private readonly DataStore _data;
        private readonly Heads _heads;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly List<Decision> _decisions = [];
        private bool _created;

        internal Driver(DataStore data, Heads heads) { _data = data; _heads = heads; }
        internal int Dispatched { get; private set; }
        internal int Received => _decisions.Count;
        internal int Rejected => _decisions.Count(item => item.Reason != "accepted");

        internal Call[] StartGeneration(int generation) {
            CheckThread();
            State? previous = _created ? Latest() : null;
            Require(generation == (previous?.Runtime.Generation ?? 0) + 1, "Generation must advance exactly once.");
            Request[] requests = Requests(generation);
            Request[] frozen = previous is null ? requests : [.. previous.Context.Requests, .. requests];
            var runtime = new RuntimeRecord(Schema, OperationId, generation, previous?.Runtime.Accepted ?? 0,
                Rejected, frozen.Length, previous?.Runtime.Settled ?? 0);
            var state = new State(runtime, new HistoryRecord(Schema, [.. _decisions]),
                new TasksRecord(Schema, OperationId, generation, requests.Select(request => new Slot(request, "Started", null)).ToArray()),
                new ContextRecord(Schema, "input * 2 + offset", frozen));
            Publish(state);
            // Construct and hand out incomplete tasks only after this generation's frozen
            // requests and Started state return normally with Confirmed publication evidence.
            Call[] calls = requests.Select(request => new Call(request)).ToArray();
            Require(calls.All(call => !call.Task.IsCompleted), "A call completed before it was dispatched.");
            Dispatched += calls.Length;
            return calls;
        }

        internal void Receive(Task<Completion> completed) {
            CheckThread();
            Require(completed.IsCompletedSuccessfully, "The controlled queue must deliver a completed task.");
            Completion result = completed.GetAwaiter().GetResult();
            HeadEntry before = _heads.ReadHead(1).Unwrap();
            State state = ReadState(_data, before);
            string reason;
            int slotIndex = Array.FindIndex(state.Tasks.Slots, slot => slot.Request.Slot == result.Slot);
            // Each identity dimension is tested independently. The application deliberately
            // keeps operationId stable across generations, so old results exercise generation.
            if (result.OperationId != state.Runtime.OperationId) { reason = "operationId"; }
            else if (result.Generation != state.Runtime.Generation) { reason = "generation"; }
            else {
                Require(slotIndex >= 0, "The result names an unknown slot.");
                Slot slot = state.Tasks.Slots[slotIndex];
                if (slot.Phase == "Result") { reason = "duplicate"; }
                else {
                    Require(slot.Phase == "Started" && result == Complete(slot.Request),
                        "The result does not match the complete frozen request or result digest.");
                    reason = "accepted";
                }
            }
            _decisions.Add(new Decision(_decisions.Count + 1, result, reason));
            if (reason != "accepted") {
                // Audit observations stay in the driver until the next legitimate publication.
                // Rejection appends neither a data frame nor a ref entryId.
                HeadEntry after = _heads.ReadHead(1).Unwrap();
                Require(after.Id == before.Id && after.Payload.SequenceEqual(before.Payload),
                    "Rejecting a completion changed the persisted state.");
                return;
            }
            Slot[] merged = state.Tasks.Slots.ToArray();
            merged[slotIndex] = merged[slotIndex] with { Phase = "Result", Result = result };
            Publish(state with {
                Runtime = state.Runtime with { Accepted = state.Runtime.Accepted + 1, Rejected = Rejected,
                    Settled = checked(state.Runtime.Settled + result.Value) },
                History = new HistoryRecord(Schema, [.. _decisions]),
                Tasks = state.Tasks with { Slots = merged }
            });
        }

        private State Latest() {
            CheckThread();
            return ReadState(_data, _heads.ReadHead(1).Unwrap());
        }

        private void Publish(State state) {
            CheckThread();
            byte[] bytes = AppendState(_data, state);
            PublicationOutcome outcome = PublicationOutcome.NotAttempted;
            if (!_created) { _ = _heads.CreateHead(1, bytes, out outcome).Unwrap(); }
            else {
                HeadId id = _heads.ReadHead(1).Unwrap().Id.HeadId;
                _ = _heads.PublishHead(id, bytes, out outcome).Unwrap();
            }
            Require(outcome == PublicationOutcome.Confirmed, "Successful publication did not report Confirmed.");
            _created = true;
        }

        private void CheckThread() => Require(Environment.CurrentManagedThreadId == _thread,
            "Storage access escaped the single driver thread.");
    }

    // Sources contain immutable application requests, not a captured NamedFrameMap or store.
    // Explicit completion order makes all nine inbox deliveries reproducible without sleeps.
    private sealed class Call {
        private readonly TaskCompletionSource<Completion> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Call(Request request) { Request = request; }
        internal Request Request { get; }
        internal Task<Completion> Task => _source.Task;
        internal Task<Completion> Complete() {
            _source.SetResult(GenerationScenario.Complete(Request));
            return _source.Task;
        }
    }

    private static Task<Completion> Injected(Completion result) {
        var source = new TaskCompletionSource<Completion>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(result);
        return source.Task;
    }

    private static byte[] AppendState(DataStore data, State state) => NamedFrameMapCodec.Encode(
        new Dictionary<string, FrameAddress>(StringComparer.Ordinal) {
            ["runtime"] = data.Append(RuntimeTag, Serialize(state.Runtime)).Unwrap(),
            ["history"] = data.Append(HistoryTag, Serialize(state.History)).Unwrap(),
            ["tasks"] = data.Append(TasksTag, Serialize(state.Tasks)).Unwrap(),
            ["context"] = data.Append(ContextTag, Serialize(state.Context)).Unwrap()
        }).Unwrap();

    private static State ReadState(DataStore data, HeadEntry entry) {
        IReadOnlyDictionary<string, FrameAddress> frames = NamedFrameMapCodec.Decode(entry.Payload).Unwrap();
        Require(frames.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "context", "history", "runtime", "tasks" }),
            "The application NamedFrameMap must contain all four frames.");
        return new State(ReadRecord<RuntimeRecord>(data, frames["runtime"], RuntimeTag),
            ReadRecord<HistoryRecord>(data, frames["history"], HistoryTag),
            ReadRecord<TasksRecord>(data, frames["tasks"], TasksTag),
            ReadRecord<ContextRecord>(data, frames["context"], ContextTag));
    }

    private static T ReadRecord<T>(DataStore data, FrameAddress address, uint tag) {
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        Require(frame.Tag == tag && frame.TailMetaLength == 0 && !frame.IsTombstone,
            "A root resolved to the wrong application frame shape.");
        T value = JsonSerializer.Deserialize<T>(frame.PayloadAndMeta, JsonOptions)
            ?? throw new InvalidDataException("An application root contains null JSON.");
        Require(frame.PayloadAndMeta.SequenceEqual(Serialize(value)), "Application JSON is not the complete expected schema.");
        return value;
    }

    private static Summary ValidateStore(DataStore data, Heads heads) {
        Require(heads.ListHeads().Unwrap().Select(id => id.LocalId).SequenceEqual(new uint[] { 1 }),
            "Unexpected formal ref set.");
        HeadEntry head = heads.ReadHead(1).Unwrap();
        var history = new List<HeadEntry>();
        HeadHistoryEnd end = heads.ReadHeadHistory(head.Id.HeadId, Budget, entry => { history.Add(entry); return true; }).Unwrap();
        Require(end == HeadHistoryEnd.Complete && history.Count == 5 && history[0].Id == head.Id
            && history[0].Payload.SequenceEqual(head.Payload), "Incomplete or inconsistent ref history.");
        Require(history.Select(entry => entry.Id).Distinct().Count() == 5, "Head entryIds were reused.");
        Decision[] decisions = ExpectedDecisions();
        var addresses = new HashSet<FrameAddress>();
        int[] accepted = [0, 1, 1, 2, 3];
        int[] rejected = [0, 1, 2, 4, 6];
        int[] settled = [0, 11, 11, 213, 314];
        int[] decisionCounts = [0, 2, 3, 6, 9];
        for (int stage = 0; stage < 5; stage++) {
            HeadEntry entry = history[4 - stage];
            State state = ReadState(data, entry);
            int generation = stage < 2 ? 1 : 2;
            Require(state.Runtime == new RuntimeRecord(Schema, OperationId, generation, accepted[stage], rejected[stage],
                generation * 2, settled[stage]), "Historical runtime counters or identity differ.");
            Require(state.History.Schema == Schema && state.History.Decisions.SequenceEqual(decisions.Take(decisionCounts[stage])),
                "The complete historical decision transcript differs.");
            Request[] frozen = generation == 1 ? Requests(1) : [.. Requests(1), .. Requests(2)];
            Require(state.Context.Schema == Schema && state.Context.Rule == "input * 2 + offset"
                && state.Context.Requests.SequenceEqual(frozen), "Frozen request context changed.");
            Require(state.Tasks.Schema == Schema && state.Tasks.OperationId == OperationId && state.Tasks.Generation == generation
                && state.Tasks.Slots.Length == 2, "Historical task identity or slot count differs.");
            Request[] currentRequests = Requests(generation);
            for (int slot = 0; slot < 2; slot++) {
                bool completed = (stage == 1 && slot == 0) || (stage == 3 && slot == 1) || stage == 4;
                var expected = new Slot(currentRequests[slot], completed ? "Result" : "Started",
                    completed ? Complete(currentRequests[slot]) : null);
                Require(state.Tasks.Slots[slot] == expected, "A result was overwritten, duplicated, or lost from a historical slot.");
            }
            foreach (FrameAddress address in NamedFrameMapCodec.Decode(entry.Payload).Unwrap().Values) {
                Require(addresses.Add(address), "This fixture unexpectedly reused an application frame.");
            }
        }
        Require(heads.ListHeadOrigins(2).Unwrap().Count == 0, "The merge scenario created an unexpected fork.");
        long frames = data.Audit(static _ => { }).Unwrap();
        Require(frames == 20 && addresses.Count == 20, "Rejected completions appended data or a publication lost frames.");
        State final = ReadState(data, head);
        Require(final.Tasks.Slots.Sum(slot => slot.Result!.Value) == 303, "Current generation lost an A/B result.");
        return new Summary(1, history.Count, frames, final.Runtime.Generation, final.Runtime.Accepted,
            final.Runtime.Rejected, final.Runtime.Dispatched, final.Runtime.Settled,
            final.Tasks.Slots[0].Result!.Value, final.Tasks.Slots[1].Result!.Value,
            final.History.Decisions.Count(item => item.Reason == "operationId"),
            final.History.Decisions.Count(item => item.Reason == "generation"),
            final.History.Decisions.Count(item => item.Reason == "duplicate"));
    }

    private static Decision[] ExpectedDecisions() {
        Request[] first = Requests(1);
        Request[] second = Requests(2);
        return [
            new(1, Complete(first[0], WrongOperationId), "operationId"),
            new(2, Complete(first[0]), "accepted"),
            new(3, Complete(first[0]), "duplicate"),
            new(4, Complete(first[1]), "generation"),
            new(5, Complete(second[1], WrongOperationId), "operationId"),
            new(6, Complete(second[1]), "accepted"),
            new(7, Complete(second[1]), "duplicate"),
            new(8, Complete(first[0]), "generation"),
            new(9, Complete(second[0]), "accepted")
        ];
    }

    private static Request[] Requests(int generation) {
        Require(generation is 1 or 2, "Unknown generation.");
        return generation == 1 ? [RequestFor(1, "A", 5, 1), RequestFor(1, "B", 6, 1)]
            : [RequestFor(2, "A", 50, 1), RequestFor(2, "B", 100, 2)];
    }

    private static Request RequestFor(int generation, string slot, int input, int offset) {
        var content = new RequestContent(Schema, OperationId, generation, slot, input, offset);
        return new Request(content, Digest(content));
    }

    private static Completion Complete(Request request, string? operationId = null) {
        RequestContent frozen = request.Content;
        Require(request.Hash == Digest(frozen), "Frozen request integrity check failed.");
        var result = new ResultContent(operationId ?? frozen.OperationId, frozen.Generation, frozen.Slot,
            request.Hash, checked(frozen.Input * 2 + frozen.Offset));
        return new Completion(result, Digest(result));
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Serialize(value)));

    private static void Emit(string scenario, Summary summary) => Console.WriteLine(JsonSerializer.Serialize(new {
        status = "ok", ok = true, scenario, pid = Environment.ProcessId,
        counts = new { refs = summary.Heads, entries = summary.Entries, dataFrames = summary.DataFrames },
        generation = summary.Generation, operationId = OperationId, accepted = summary.Accepted,
        rejected = summary.Rejected, dispatched = summary.Dispatched, decisions = summary.Accepted + summary.Rejected,
        settled = summary.Settled, currentTotal = summary.A + summary.B, slots = new { A = summary.A, B = summary.B },
        rejections = new { operationId = summary.OperationRejected, generation = summary.GenerationRejected, duplicate = summary.DuplicateRejected },
        singleDriver = true, noLostUpdate = true
    }));

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private sealed record RequestContent(int Schema, string OperationId, int Generation, string Slot, int Input, int Offset);
    private sealed record Request(RequestContent Content, string Hash) {
        public string Slot => Content.Slot;
    }
    private sealed record ResultContent(string OperationId, int Generation, string Slot, string RequestHash, int Value);
    private sealed record Completion(ResultContent Content, string Hash) {
        public string OperationId => Content.OperationId;
        public int Generation => Content.Generation;
        public string Slot => Content.Slot;
        public int Value => Content.Value;
    }
    private sealed record Decision(int Sequence, Completion Result, string Reason);
    private sealed record RuntimeRecord(int Schema, string OperationId, int Generation, int Accepted, int Rejected, int Dispatched, int Settled);
    private sealed record HistoryRecord(int Schema, Decision[] Decisions);
    private sealed record Slot(Request Request, string Phase, Completion? Result);
    private sealed record TasksRecord(int Schema, string OperationId, int Generation, Slot[] Slots);
    private sealed record ContextRecord(int Schema, string Rule, Request[] Requests);
    private sealed record State(RuntimeRecord Runtime, HistoryRecord History, TasksRecord Tasks, ContextRecord Context);
    private sealed record Summary(int Heads, int Entries, long DataFrames, int Generation, int Accepted, int Rejected,
        int Dispatched, int Settled, int A, int B, int OperationRejected, int GenerationRejected, int DuplicateRejected);
}
