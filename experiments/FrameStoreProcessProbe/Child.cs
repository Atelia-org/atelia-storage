using System.Text;
using System.Text.Json;
using Atelia.FrameStore;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStoreProcessProbe;

internal static class Child {
    private const int PartialBytes = 2 * 1024 * 1024;
    private const int DeclaredBytes = 4 * 1024 * 1024;

    internal static int Run() {
        Request? request = null;
        try {
            request = JsonSerializer.Deserialize<Request>(Console.ReadLine()
                ?? throw new EndOfStreamException("No initial JSON request."), Protocol.Json)
                ?? throw new InvalidDataException("Null JSON request.");
            Protocol.Write(new("boot", Environment.ProcessId, request.Token));
            switch (request.Operation) {
                case "setup": Setup(request); break;
                case "hold": Hold(request); break;
                case "try": TryOpen(request); break;
                case "inspect": Inspect(request); break;
                case "crash": Crash(request); break;
                default: throw new InvalidDataException("Unknown child operation.");
            }
            return 0;
        }
        catch (Exception error) {
            Console.Error.WriteLine(error);
            Protocol.Write(new("error", Environment.ProcessId, request?.Token ?? "", Error: ErrorInfo.From(error)));
            return 1;
        }
    }

    private static void Setup(Request request) {
        var expected = new List<FrameExpectation>();
        string id;
        using (var store = Store.Create(request.Root, request.Scenario == "archive" ? 1024 : 64L * 1024 * 1024 * 1024)) {
            id = Convert.ToHexString(store.StoreId);
            if (request.Scenario != "empty") {
                byte[] bytes = request.Scenario == "archive" ? Payload(4096, 0x33) : "active-prefix"u8.ToArray();
                expected.Add(Protocol.Expect("setup-prefix", store.Append(1, bytes).Unwrap(), 1, bytes));
            }
            store.ConfirmDurable();
        }
        Protocol.Write(new("result", Environment.ProcessId, request.Token,
            new("setup-disposed", true, id, 0, expected.ToArray(), [], [], [], [], [])));
    }

    private static void Hold(Request request) {
        using var store = Open(request.Root, request.Scenario);
        Protocol.Write(new("ready", Environment.ProcessId, request.Token,
            new("owner-open-returned", false, Convert.ToHexString(store.StoreId), 0, [], [], [], [], [], [])));
        WaitForDispose(request, store);
    }

    private static void TryOpen(Request request) {
        Observation observation;
        try {
            using var store = Open(request.Root, request.Scenario);
            observation = new("opened", Convert.ToHexString(store.StoreId), store.IsReadOnly);
        }
        catch (Exception error) when (Protocol.LockConflict(error)) {
            observation = new("blocked", Error: ErrorInfo.From(error));
        }
        Protocol.Write(new("result", Environment.ProcessId, request.Token, Observation: observation));
    }

    private static void Inspect(Request request) {
        Store store;
        try {
            store = Open(request.Root, request.Scenario);
        }
        // A dirty public Builder tail may make strict RO opening reject. Other errors are failures.
        catch (InvalidDataException error) when (request.Scenario == "reader") {
            Protocol.Write(new("result", Environment.ProcessId, request.Token,
                Observation: new("rejected", Error: ErrorInfo.From(error))));
            return;
        }
        Observation observation;
        using (store) { observation = Protocol.Inspect(store, request); }
        Protocol.Write(new("result", Environment.ProcessId, request.Token, Observation: observation));
    }

    private static void Crash(Request request) {
        bool archived = request.Scenario == "archive-return";
        using var store = Store.Create(request.Root, archived ? 1024 : 64L * 1024 * 1024 * 1024);
        var required = new List<FrameExpectation>();
        var optional = new List<FrameExpectation>();
        var unfinished = new List<string>();
        var keepAlive = new List<FrameBuilder>();
        var builderProgress = new List<BuilderProgress>();
        FileLength[] before = [];
        FileLength[] after = [];
        FrameObserved[] beforeArchive = [];
        bool confirmed = false;
        string reached = "Create-returned";

        if (request.Scenario is "append-return" or "confirm-return" or "archive-return") {
            byte[] prefix = "confirmed-prefix"u8.ToArray();
            required.Add(Protocol.Expect("confirmed-prefix", store.Append(11, prefix).Unwrap(), 11, prefix));
            store.ConfirmDurable();
            byte[] bytes = Payload(256 * 1024, 0x55);
            var expectation = Protocol.Expect("completed-output", store.Append(12, bytes).Unwrap(), 12, bytes);
            optional.Add(expectation);
            reached = "Append-returned-before-confirm";
            if (request.Scenario is "confirm-return" or "archive-return") {
                beforeArchive = archived ? [Protocol.Read(store, Protocol.Decode(expectation.Address))] : [];
                store.ConfirmDurable();
                required.Add(expectation);
                optional.Clear();
                confirmed = true;
                reached = archived ? "ConfirmDurable-returned-after-archive" : "ConfirmDurable-returned";
                if (archived) {
                    var afterArchive = Protocol.Read(store, Protocol.Decode(expectation.Address));
                    if (afterArchive != beforeArchive[0]) { throw new InvalidDataException("Archive changed address/content."); }
                }
            }
        }
        else if (request.Scenario is "known-builder" or "unknown-builder" or "mixed-builders") {
            int count = request.Scenario == "mixed-builders" ? 3 : 1;
            // Simultaneously lease distinct files before completing their prefixes.
            var prefixes = new List<(FrameBuilder Builder, FrameAddress Address, byte[] Bytes)>();
            for (int i = 0; i < count; i++) {
                byte[] bytes = Encoding.UTF8.GetBytes($"confirmed-file-prefix-{i}");
                var builder = store.BeginAppend(bytes.Length, 0, out var address);
                Write(builder.PayloadAndMeta, bytes);
                prefixes.Add((builder, address, bytes));
            }
            for (int i = 0; i < prefixes.Count; i++) {
                var prefix = prefixes[i];
                var completed = prefix.Builder.EndAppend((uint)(100 + i)).Unwrap();
                if (completed != prefix.Address) { throw new InvalidDataException("Early address changed."); }
                required.Add(Protocol.Expect($"confirmed-file-{i}", completed, (uint)(100 + i), prefix.Bytes));
            }
            for (int i = 0; i < count; i++) {
                bool known = request.Scenario == "known-builder" || request.Scenario == "mixed-builders" && i != 1;
                FrameBuilder builder;
                if (known) {
                    builder = store.BeginAppend(DeclaredBytes, 0, out var early);
                    unfinished.Add(Protocol.Encode(early));
                }
                else { builder = store.BeginAppend(); }
                keepAlive.Add(builder);
            }
            before = Protocol.Lengths(request.Root);
            foreach (var builder in keepAlive) {
                var writer = builder.PayloadAndMeta;
                long initialLength = writer.Length;
                byte[] chunk = Payload(64 * 1024, 0x5a);
                for (int offset = 0; offset < PartialBytes; offset += chunk.Length) { Write(writer, chunk); }
                if (writer.Length != initialLength + PartialBytes) {
                    throw new InvalidDataException("Unexpected advanced Builder length delta.");
                }
                builderProgress.Add(new(builderProgress.Count, initialLength, PartialBytes, writer.Length, 0));
            }
            after = Protocol.Lengths(request.Root);
            if (before.Length != count || after.Length != count || before.Where((item, i) =>
                item.RelativePath != after[i].RelativePath).Any()) {
                throw new InvalidDataException("Every Builder must retain its distinct active file before READY.");
            }
            // The completed file prefixes are still unconfirmed when their files are leased again.
            // Confirm now, while each Builder has advanced logical data; no Builder is ended/cancelled.
            store.ConfirmDurable();
            confirmed = true;
            for (int i = 0; i < keepAlive.Count; i++) {
                long length = keepAlive[i].PayloadAndMeta.Length;
                if (length != builderProgress[i].WriterLengthBeforeConfirm) {
                    throw new InvalidDataException("Active confirmation changed Builder content.");
                }
                builderProgress[i] = builderProgress[i] with { WriterLengthAfterConfirm = length };
            }
            // Existing confirmed output remains readable in every leased file.
            foreach (var prefix in required) { _ = Protocol.Read(store, Protocol.Decode(prefix.Address)); }
            reached = "ConfirmDurable-returned-with-active-Builders-without-EndAppend";
        }
        else if (request.Scenario != "create-return") { throw new InvalidDataException("Unknown crash scenario."); }

        Protocol.Write(new("ready", Environment.ProcessId, request.Token,
            new(reached, confirmed, Convert.ToHexString(store.StoreId), keepAlive.Count,
                required.ToArray(), optional.ToArray(), unfinished.ToArray(), before, after, beforeArchive, builderProgress.ToArray())));
        // No Builder Dispose/EndAppend is called before the parent terminates this process.
        WaitForDispose(request, store);
        GC.KeepAlive(keepAlive);
    }

    private static Store Open(string root, string mode) => mode == "reader" ? Store.OpenReadOnly(root) : Store.Open(root);

    private static void WaitForDispose(Request request, Store store) {
        var command = JsonSerializer.Deserialize<Request>(Console.ReadLine()
            ?? throw new EndOfStreamException("Holder stdin closed before dispose command."), Protocol.Json);
        if (command is null || command.Operation != "dispose" || command.Token != request.Token) {
            throw new InvalidDataException("Invalid holder dispose command.");
        }
        store.Dispose();
        Protocol.Write(new("disposed", Environment.ProcessId, request.Token));
    }

    private static byte[] Payload(int length, byte value) {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static void Write(FramePayloadWriter writer, ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }
}
