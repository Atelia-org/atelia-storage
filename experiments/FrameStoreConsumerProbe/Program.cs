using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.FrameStore;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStoreConsumerProbe;

internal static class Program {
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static int Main(string[] args) {
        string? output = null;
        string? revision = null;
        try {
            for (int i = 0; i < args.Length; i += 2) {
                if (i + 1 >= args.Length) { throw new ArgumentException("Options require a value."); }
                switch (args[i]) {
                    case "--output-directory": output = args[i + 1]; break;
                    case "--revision": revision = args[i + 1]; break;
                    default: throw new ArgumentException("Unknown option: " + args[i]);
                }
            }
            string root = Path.GetFullPath(output ?? Path.Combine("artifacts", "framestore-consumer-probe",
                DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")));
            if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("Output directory must be new: " + root); }
            Directory.CreateDirectory(root);
            return Execute(root, revision);
        }
        catch (Exception error) {
            Console.Error.WriteLine(error);
            return 2;
        }
    }

    private static int Execute(string root, string? revision) {
        DateTime started = DateTime.UtcNow;
        var run = new ConsumerRun(root);
        Exception? failure = null;
        try { run.Execute(); }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        string result = Path.Combine(root, "result.json");
        var evidence = new {
            schemaVersion = 1, passed = failure is null, startedUtc = started, finishedUtc = DateTime.UtcNow,
            callerSuppliedRevision = revision,
            environment = new {
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), pid = Environment.ProcessId,
                currentDirectory = Environment.CurrentDirectory, outputDirectory = root,
                dotnetSystemIoDisableFileLocking = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING"),
                appContextDisableFileLocking = AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled) && disabled,
                probeAssembly = AssemblyIdentity(Assembly.GetExecutingAssembly()), frameStoreAssembly = AssemblyIdentity(typeof(Store).Assembly)
            },
            scope = "Source-only public FrameStore consumer; six bounded fixtures and fresh read-only owners after Dispose. No package, publication, process crash or cold OS-cache claim.",
            input = new { fixtures = 6, nodePayloadBytes = NodeCodec.PayloadLength, addressFieldBytes = FrameAddress.EncodedSize, completedUserFrames = 11 },
            assertionCount = run.Checks.Count, checks = run.Checks, scenarios = run.Scenarios,
            failure = failure is null ? null : new { type = failure.GetType().FullName, failure.Message, detail = failure.ToString() }
        };
        File.WriteAllText(result, JsonSerializer.Serialize(evidence, Json));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = failure is null, assertionCount = run.Checks.Count, scenarios = run.Scenarios.Count, result }));
        return failure is null ? 0 : 1;
    }

    private static object AssemblyIdentity(Assembly assembly) {
        string path = assembly.Location;
        return new { path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
    }
}

internal sealed record CheckEvidence(string Name, bool Passed);
internal sealed record SavedRoot(uint ExpectedIdentity, string AddressHex);
internal sealed record AddressManifest(string StoreIdHex, Dictionary<string, SavedRoot> Roots, Dictionary<string, string> Addresses,
    string Purpose = "Probe evidence only; not a VersionStore root or application publication record.");
internal sealed record NodeObservation(uint Identity, uint ExpectedTargetIdentity, string AddressHex, string ReferenceHex);
internal sealed record GraphObservation(string LogicalGraph, NodeObservation[] Nodes);
internal sealed record LayoutObservation(string[] ActiveFiles, string[] ArchiveFiles);

internal sealed class ConsumerRun(string root) {
    internal List<CheckEvidence> Checks { get; } = [];
    internal List<object> Scenarios { get; } = [];

    internal void Execute() {
        Check(FrameAddress.EncodedSize == 12, "public address codec has exactly 12 bytes");
        SelfReference();
        GraphObservation forward = MutualReference(reverseCompletion: false);
        GraphObservation reverse = MutualReference(reverseCompletion: true);
        Check(forward.LogicalGraph == reverse.LogicalGraph, "different completion orders decode the same logical A/B graph");
        GraphObservation reverseAcquisition = MutualReference(reverseCompletion: false, reverseAcquisition: true);
        Check(forward.LogicalGraph == reverseAcquisition.LogicalGraph, "different acquisition orders decode the same logical A/B graph");
        GraphObservation singleSlot = SingleSlotReference();
        Check(forward.LogicalGraph == singleSlot.LogicalGraph, "M=1 plus complete Append decodes the same logical A/B graph");
        CancelAndReuse();
    }

    private void Check(bool passed, string name) {
        Checks.Add(new(name, passed));
        if (!passed) { throw new InvalidDataException("Consumer probe assertion failed: " + name); }
    }

    private string Prepare(string name, int maxBuilders) {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        string storeRoot = Path.Combine(path, "store");
        Directory.CreateDirectory(storeRoot);
        File.WriteAllText(Path.Combine(storeRoot, "framestore.config.json"), JsonSerializer.Serialize(new { MaxOutstandingBuilders = maxBuilders }));
        return storeRoot;
    }

    private void SelfReference() {
        const string name = "self-reference-archive";
        string storeRoot = Prepare(name, 1);
        string manifestPath = Path.Combine(root, name, "consumer-addresses.json");
        var events = new List<string>();
        LayoutObservation beforeArchive;
        LayoutObservation afterArchive;
        using (var owner = Store.Create(storeRoot, rotationThresholdBytes: 64)) {
            using var builder = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress address);
            string early = AddressHex(address, name + " early address");
            WriteNode(builder.PayloadAndMeta, 7, 7, address, name);
            Check(builder.EndAppend(NodeCodec.Tag).Unwrap() == address, name + ": completed address equals early address");
            events.Add("Begin sized self -> write own 12B address -> End");
            GraphObservation before = ReadGraph(owner, address, 7, 1, name + " before archive");
            beforeArchive = Layout(storeRoot);
            Check(beforeArchive.ActiveFiles.Length == 1 && beforeArchive.ArchiveFiles.Length == 0, name + ": End returns before archive maintenance");
            owner.ConfirmDurable();
            afterArchive = Layout(storeRoot);
            Check(afterArchive.ActiveFiles.Length == 0 && afterArchive.ArchiveFiles.Length == 1, name + ": Confirm archived the stopped file");
            GraphObservation after = ReadGraph(owner, address, 7, 1, name + " after archive");
            Check(before.LogicalGraph == after.LogicalGraph, name + ": archive preserves the logical self-reference");
            Check(AddressHex(address, name + " after archive address") == early, name + ": archive preserves all 12 address bytes");
            Save(manifestPath, owner, new() { ["self"] = new(7, early) }, new() { ["self"] = early });
            events.Add("ConfirmDurable -> archive -> ReadFrame(same address) -> save evidence addresses -> Dispose");
        }
        AddressManifest saved = Load(manifestPath);
        GraphObservation reopened;
        using (var reader = OpenReader(storeRoot, saved, name)) {
            reopened = ReadSavedGraph(reader, saved, "self", 1, name + " reopened");
        }
        Check(Layout(storeRoot).ArchiveFiles.SequenceEqual(afterArchive.ArchiveFiles), name + ": read-only reopen keeps archive paths");
        events.Add("Reload 12B evidence address -> new OpenReadOnly -> decode graph");
        Scenarios.Add(new { name, storeRoot, manifestPath, events, beforeArchive, afterArchive, reopened });
    }

    private GraphObservation MutualReference(bool reverseCompletion, bool reverseAcquisition = false) {
        string name = reverseAcquisition ? "mutual-reverse-acquisition"
            : reverseCompletion ? "mutual-reverse-completion" : "mutual-forward-completion";
        string storeRoot = Prepare(name, 2);
        string manifestPath = Path.Combine(root, name, "consumer-addresses.json");
        var events = new List<string>();
        using (var owner = Store.Create(storeRoot)) {
            string firstName = reverseAcquisition ? "B" : "A";
            string secondName = reverseAcquisition ? "A" : "B";
            using var first = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress firstAddress);
            WriteHeader(first.PayloadAndMeta, reverseAcquisition ? 2u : 1u, reverseAcquisition ? 1u : 2u, name + " " + firstName);
            events.Add("Begin " + firstName + " -> write " + firstName + " header");
            using var second = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress secondAddress);
            WriteHeader(second.PayloadAndMeta, reverseAcquisition ? 1u : 2u, reverseAcquisition ? 2u : 1u, name + " " + secondName);
            events.Add("Begin " + secondName + " while " + firstName + " is active -> write " + secondName + " header");
            FrameBuilder a = reverseAcquisition ? second : first;
            FrameBuilder b = reverseAcquisition ? first : second;
            FrameAddress aAddress = reverseAcquisition ? secondAddress : firstAddress;
            FrameAddress bAddress = reverseAcquisition ? firstAddress : secondAddress;
            var aWriter = a.PayloadAndMeta;
            var bWriter = b.PayloadAndMeta;
            Check(aAddress != bAddress, name + ": overlapping sized builders have distinct addresses");
            int aReference = ReserveReference(aWriter, name + " A");
            int bReference = ReserveReference(bWriter, name + " B");
            FillReference(bWriter, bReference, aAddress, name + " B");
            FillReference(aWriter, aReference, bAddress, name + " A");
            events.Add("Reserve A reference -> reserve B reference -> backfill/Commit B -> backfill/Commit A");
            if (reverseCompletion) {
                Check(b.EndAppend(NodeCodec.Tag).Unwrap() == bAddress, name + ": B End preserves its early address");
                Check(a.EndAppend(NodeCodec.Tag).Unwrap() == aAddress, name + ": A End preserves its early address");
                events.Add("End B -> End A");
            }
            else {
                Check(a.EndAppend(NodeCodec.Tag).Unwrap() == aAddress, name + ": A End preserves its early address");
                Check(b.EndAppend(NodeCodec.Tag).Unwrap() == bAddress, name + ": B End preserves its early address");
                events.Add("End A -> End B");
            }
            owner.ConfirmDurable();
            Save(manifestPath, owner,
                new() { ["A"] = new(1, AddressHex(aAddress, name + " save A")) },
                new() { ["A"] = AddressHex(aAddress, name + " A"), ["B"] = AddressHex(bAddress, name + " B") });
            events.Add("ConfirmDurable -> save evidence addresses -> Dispose");
        }
        AddressManifest saved = Load(manifestPath);
        GraphObservation reopened;
        using (var reader = OpenReader(storeRoot, saved, name)) {
            reopened = ReadSavedGraph(reader, saved, "A", 2, name + " reopened");
            Check(reopened.Nodes[0].ReferenceHex == saved.Addresses["B"] && reopened.Nodes[1].ReferenceHex == saved.Addresses["A"],
                name + ": decoded reference fields match the saved opposite addresses");
        }
        events.Add("Reload 12B evidence address -> new OpenReadOnly -> follow decoded A/B references");
        Scenarios.Add(new { name, storeRoot, manifestPath, maxOutstandingBuilders = 2, events, reopened, layout = Layout(storeRoot) });
        return reopened;
    }

    private GraphObservation SingleSlotReference() {
        const string name = "mutual-single-slot-append";
        string storeRoot = Prepare(name, 1);
        string manifestPath = Path.Combine(root, name, "consumer-addresses.json");
        var events = new List<string>();
        using (var owner = Store.Create(storeRoot)) {
            using var a = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress aAddress);
            var writer = a.PayloadAndMeta;
            WriteHeader(writer, 1, 2, name + " A");
            int reference = ReserveReference(writer, name + " A");
            events.Add("Begin sized A -> write header -> reserve its 12B reference");
            ExpectInvalidOperation(() => { using var rejected = owner.BeginAppend(); }, name + ": a second Builder exceeds M=1");
            FrameAddress bAddress = owner.Append(NodeCodec.Tag, NodeCodec.Encode(2, 1, aAddress)).Unwrap();
            events.Add("Append complete B with reference to early A while its sole Builder slot is occupied");
            ReadNode(owner, bAddress, name + " completed B");
            ExpectBuildingReadRejection(owner, aAddress, name + ": complete B does not make early A readable");
            FillReference(writer, reference, bAddress, name + " A");
            Check(a.EndAppend(NodeCodec.Tag).Unwrap() == aAddress, name + ": backfilled A preserves its early address");
            events.Add("TryGetReservedSpan A -> write B address -> Commit -> End A");
            owner.ConfirmDurable();
            Save(manifestPath, owner,
                new() { ["A"] = new(1, AddressHex(aAddress, name + " save A")) },
                new() { ["A"] = AddressHex(aAddress, name + " A"), ["B"] = AddressHex(bAddress, name + " B") });
            events.Add("ConfirmDurable -> save evidence addresses -> Dispose");
        }
        AddressManifest saved = Load(manifestPath);
        GraphObservation reopened;
        using (var reader = OpenReader(storeRoot, saved, name)) {
            reopened = ReadSavedGraph(reader, saved, "A", 2, name + " reopened");
        }
        events.Add("Reload 12B evidence address -> new OpenReadOnly -> decode A/B cycle");
        Scenarios.Add(new { name, storeRoot, manifestPath, maxOutstandingBuilders = 1, events, reopened, layout = Layout(storeRoot) });
        return reopened;
    }

    private void CancelAndReuse() {
        const string name = "cancel-reuse-abandoned-graph";
        string storeRoot = Prepare(name, 2);
        string manifestPath = Path.Combine(root, name, "consumer-addresses.json");
        var events = new List<string>();
        bool oldGraphAbandoned = false;
        string staleEndError;
        using (var owner = Store.Create(storeRoot)) {
            using var b = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress bAddress);
            FrameBuilder oldBuilder = b;
            FramePayloadWriter oldWriter = b.PayloadAndMeta;
            WriteHeader(oldWriter, 2, 1, name + " B");
            int oldReservation = ReserveReference(oldWriter, name + " B");
            using var a = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress aAddress);
            WriteNode(a.PayloadAndMeta, 1, 2, bAddress, name + " A");
            Check(a.EndAppend(NodeCodec.Tag).Unwrap() == aAddress, name + ": A completes with provisional reference to B");
            owner.ConfirmDurable();
            events.Add("Begin B -> Begin A -> complete A pointing to B -> ConfirmDurable while B is unfinished");
            ExpectBuildingReadRejection(owner, bAddress, name + ": ConfirmDurable does not complete A's missing dependency B");
            b.Dispose();
            oldGraphAbandoned = true;
            events.Add("Cancel B -> consumer abandons the old A/B construction state");
            using var c = owner.BeginAppend(NodeCodec.PayloadLength, 0, out FrameAddress cAddress);
            Check(cAddress == bAddress, name + ": C with the same declared size reuses B's numeric address");
            var staleEnd = oldBuilder.EndAppend(NodeCodec.Tag);
            staleEndError = staleEnd.Error?.ErrorCode ?? "unexpected success";
            Check(staleEnd.IsFailure && staleEndError == "FrameStore.StateError", name + ": old Builder cannot End C's new lease");
            ExpectInvalidOperation(() => { _ = oldBuilder.PayloadAndMeta; }, name + ": old Builder cannot obtain a writer");
            ExpectInvalidOperation(() => { _ = oldWriter.GetMemory(1); }, name + ": old Writer cannot borrow memory");
            ExpectInvalidOperation(() => oldWriter.Advance(0), name + ": old Writer cannot Advance");
            ExpectInvalidOperation(() => { _ = oldWriter.Length; }, name + ": old Writer cannot inspect the new lease's length");
            ExpectInvalidOperation(() => { oldWriter.TryGetReservedSpan(oldReservation, out _); }, name + ": old Writer cannot recover its reservation");
            ExpectInvalidOperation(() => oldWriter.Commit(oldReservation), name + ": old Writer cannot Commit an old reservation");
            oldBuilder.Dispose();
            WriteNode(c.PayloadAndMeta, 3, 3, cAddress, name + " C");
            Check(c.EndAppend(NodeCodec.Tag).Unwrap() == cAddress, name + ": stale Dispose did not cancel C");
            owner.ConfirmDurable();
            events.Add("Begin C at b -> reject stale Builder/Writer operations -> stale Dispose -> complete fresh C self-reference -> ConfirmDurable");
            Save(manifestPath, owner,
                new() { ["abandoned-A"] = new(1, AddressHex(aAddress, name + " save A")), ["fresh-C"] = new(3, AddressHex(cAddress, name + " save C")) },
                new() { ["A"] = AddressHex(aAddress, name + " A"), ["cancelled-B"] = AddressHex(bAddress, name + " B"), ["C"] = AddressHex(cAddress, name + " C") });
        }
        AddressManifest saved = Load(manifestPath);
        GraphObservation freshGraph;
        NodeObservation orphanA;
        NodeObservation physicalAtCancelledAddress;
        ConsumerIdentityMismatch rejection;
        using (var reader = OpenReader(storeRoot, saved, name)) {
            FrameAddress aAddress = ParseAddress(saved.Addresses["A"], name + " saved A");
            FrameAddress bAddress = ParseAddress(saved.Addresses["cancelled-B"], name + " saved B");
            orphanA = ReadNode(reader, aAddress, name + " orphan A");
            physicalAtCancelledAddress = ReadNode(reader, bAddress, name + " physical reused address");
            Check(physicalAtCancelledAddress.Identity == 3, name + ": public physical ReadFrame(b) legitimately returns C");
            Check(orphanA.ExpectedTargetIdentity == 2 && orphanA.ReferenceHex == saved.Addresses["cancelled-B"], name + ": orphan A still expects B at b");
            rejection = ExpectIdentityRejection(() => ReadSavedGraph(reader, saved, "abandoned-A", 2, name + " old graph"), name);
            Check(rejection.ExpectedIdentity == 2 && rejection.ActualIdentity == 3, name + ": consumer rejects B/C identity mismatch");
            freshGraph = ReadSavedGraph(reader, saved, "fresh-C", 1, name + " fresh graph");
        }
        Check(oldGraphAbandoned, name + ": old graph remains abandoned after physical address reuse");
        events.Add("Dispose -> reload evidence addresses -> new OpenReadOnly -> physical b reads C -> consumer rejects old A/B identity -> decode fresh C graph");
        Scenarios.Add(new {
            name, storeRoot, manifestPath, events, oldGraphAbandoned, staleEndError, orphanA, physicalAtCancelledAddress,
            consumerRejection = new { rejection.ExpectedIdentity, rejection.ActualIdentity, rejection.AddressHex, rejection.Message },
            responsibility = "The consumer checks node identity and abandons provisional state. FrameStore validates physical bytes, not graph provenance.",
            freshGraph, layout = Layout(storeRoot)
        });
    }

    private void WriteNode(FramePayloadWriter writer, uint identity, uint targetIdentity, FrameAddress reference, string name) {
        WriteHeader(writer, identity, targetIdentity, name);
        int token = ReserveReference(writer, name);
        FillReference(writer, token, reference, name);
    }

    private void WriteHeader(FramePayloadWriter writer, uint identity, uint targetIdentity, string name) {
        Span<byte> header = stackalloc byte[NodeCodec.HeaderLength];
        NodeCodec.WriteHeader(header, identity, targetIdentity);
        long before = writer.Length;
        header.CopyTo(writer.GetSpan(header.Length));
        writer.Advance(header.Length);
        Check(writer.Length - before == header.Length, name + ": header write advances the logical length by 12B");
    }

    private int ReserveReference(FramePayloadWriter writer, string name) {
        long before = writer.Length;
        writer.ReserveSpan(FrameAddress.EncodedSize, out int token, "node-reference").Clear();
        Check(writer.Length - before == FrameAddress.EncodedSize, name + ": reservation contributes exactly one 12B address field");
        return token;
    }

    private void FillReference(FramePayloadWriter writer, int token, FrameAddress address, string name) {
        Check(writer.TryGetReservedSpan(token, out Span<byte> field), name + ": public reservation can be reacquired for backfill");
        Check(field.Length == FrameAddress.EncodedSize && address.TryWrite(field), name + ": backfill uses the exact public 12B codec");
        writer.Commit(token);
    }

    private void Save(string path, Store owner, Dictionary<string, SavedRoot> roots, Dictionary<string, string> addresses) {
        var manifest = new AddressManifest(Convert.ToHexString(owner.StoreId), roots, addresses);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, Program.Json));
    }

    private static AddressManifest Load(string path) => JsonSerializer.Deserialize<AddressManifest>(File.ReadAllText(path), Program.Json)
        ?? throw new InvalidDataException("Missing address manifest: " + path);

    private Store OpenReader(string storeRoot, AddressManifest saved, string name) {
        var reader = Store.OpenReadOnly(storeRoot);
        try {
            Check(reader.IsReadOnly, name + ": a fresh read-only owner was opened after writer Dispose");
            Check(Convert.ToHexString(reader.StoreId) == saved.StoreIdHex, name + ": saved addresses are associated with the same StoreId");
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    private GraphObservation ReadSavedGraph(Store reader, AddressManifest manifest, string rootName, int nodeCount, string name) {
        SavedRoot savedRoot = manifest.Roots[rootName];
        return ReadGraph(reader, ParseAddress(savedRoot.AddressHex, name + " saved root"), savedRoot.ExpectedIdentity, nodeCount, name);
    }

    private GraphObservation ReadGraph(Store owner, FrameAddress address, uint expectedIdentity, int expectedNodeCount, string name) {
        var visited = new Dictionary<FrameAddress, NodeObservation>();
        var nodes = new List<NodeObservation>();
        while (true) {
            if (visited.TryGetValue(address, out NodeObservation? prior)) {
                RequireIdentity(prior.Identity, expectedIdentity, prior.AddressHex);
                break;
            }
            Check(nodes.Count < 8, name + ": graph traversal stays within the declared fixture bound");
            NodeObservation node = ReadNode(owner, address, name + " node " + nodes.Count);
            RequireIdentity(node.Identity, expectedIdentity, node.AddressHex);
            visited.Add(address, node);
            nodes.Add(node);
            address = ParseAddress(node.ReferenceHex, name + " decoded edge");
            expectedIdentity = node.ExpectedTargetIdentity;
        }
        Check(nodes.Count == expectedNodeCount, name + ": cycle has the expected logical node count");
        Check(nodes.Select(node => node.Identity).Distinct().Count() == nodes.Count, name + ": logical node identities are unique");
        string logicalGraph = string.Join(",", nodes.OrderBy(node => node.Identity).Select(node => node.Identity + "->" + node.ExpectedTargetIdentity));
        return new(logicalGraph, nodes.ToArray());
    }

    private NodeObservation ReadNode(Store owner, FrameAddress address, string name) {
        using var frame = owner.ReadFrame(address).Unwrap();
        Check(frame.Address == address && frame.Tag == NodeCodec.Tag && !frame.IsTombstone && frame.TailMetaLength == 0,
            name + ": public full read returns the requested normal node frame");
        ReadOnlySpan<byte> bytes = frame.PayloadAndMeta;
        Check(bytes.Length == NodeCodec.PayloadLength && bytes[..4].SequenceEqual("FSN1"u8), name + ": node payload has the declared format");
        uint identity = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        uint expectedTargetIdentity = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        Check(FrameAddress.TryRead(bytes.Slice(NodeCodec.HeaderLength, FrameAddress.EncodedSize), out FrameAddress target), name + ": decoded node reference is an exact 12B field");
        return new(identity, expectedTargetIdentity, AddressHex(address, name + " address"), AddressHex(target, name + " reference"));
    }

    private string AddressHex(FrameAddress address, string name) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Check(address.TryWrite(bytes), name + ": public address encoding succeeds");
        Check(FrameAddress.TryRead(bytes, out FrameAddress decoded) && decoded == address, name + ": public address codec round-trips its value");
        return Convert.ToHexString(bytes);
    }

    private FrameAddress ParseAddress(string hex, string name) {
        byte[] bytes = Convert.FromHexString(hex);
        Check(bytes.Length == 12 && FrameAddress.TryRead(bytes, out _), name + ": persisted address has exactly 12 decodable bytes");
        FrameAddress.TryRead(bytes, out FrameAddress address);
        return address;
    }

    private void ExpectInvalidOperation(Action action, string name) {
        try { action(); }
        catch (InvalidOperationException) { Check(true, name); return; }
        Check(false, name);
    }

    private void ExpectBuildingReadRejection(Store owner, FrameAddress address, string name) {
        // The Building completed-prefix guard rejects before producing a Result.
        // A Result failure must not be mistaken for that lifecycle rejection.
        FrameRead? unexpectedFrame = null;
        try {
            var result = owner.ReadFrame(address);
            if (result.IsSuccess) { unexpectedFrame = result.Unwrap(); }
        }
        catch (InvalidOperationException) { Check(true, name + " (Building guard)"); return; }
        unexpectedFrame?.Dispose();
        Check(false, name + " (Building guard)");
    }

    private ConsumerIdentityMismatch ExpectIdentityRejection(Action action, string name) {
        try { action(); }
        catch (ConsumerIdentityMismatch rejection) { Check(true, name + ": consumer refuses the abandoned graph"); return rejection; }
        Check(false, name + ": consumer refuses the abandoned graph");
        throw new InvalidOperationException("Unreachable assertion continuation.");
    }

    private static void RequireIdentity(uint actualIdentity, uint expectedIdentity, string addressHex) {
        if (actualIdentity != expectedIdentity) { throw new ConsumerIdentityMismatch(expectedIdentity, actualIdentity, addressHex); }
    }

    private static LayoutObservation Layout(string storeRoot) {
        string[] Files(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(storeRoot, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        return new(Files(Path.Combine(storeRoot, "active")), Files(Path.Combine(storeRoot, "archive")));
    }
}

internal static class NodeCodec {
    internal const uint Tag = 0x4E4F4445;
    internal const int HeaderLength = 12;
    internal const int PayloadLength = HeaderLength + FrameAddress.EncodedSize;

    internal static void WriteHeader(Span<byte> destination, uint identity, uint expectedTargetIdentity) {
        "FSN1"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], identity);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], expectedTargetIdentity);
    }

    internal static byte[] Encode(uint identity, uint expectedTargetIdentity, FrameAddress reference) {
        byte[] payload = new byte[PayloadLength];
        WriteHeader(payload, identity, expectedTargetIdentity);
        if (!reference.TryWrite(payload.AsSpan(HeaderLength, FrameAddress.EncodedSize))) { throw new InvalidDataException("Invalid node reference."); }
        return payload;
    }
}

internal sealed class ConsumerIdentityMismatch(uint expectedIdentity, uint actualIdentity, string addressHex)
    : Exception($"Consumer expected node identity {expectedIdentity}, but physical frame at {addressHex} contains {actualIdentity}.") {
    internal uint ExpectedIdentity { get; } = expectedIdentity;
    internal uint ActualIdentity { get; } = actualIdentity;
    internal string AddressHex { get; } = addressHex;
}
