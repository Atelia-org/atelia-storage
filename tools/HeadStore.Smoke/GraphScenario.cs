using System.Buffers.Binary;
using System.Text.Json;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Smoke;

/// <summary>A small application codec and graph, consumed through public storage APIs only.</summary>
internal static class GraphScenario {
    private const uint NodeTag = 0x47524E31;
    private const uint StateTag = 0x47525331;
    private const uint RecordVersion = 1;
    private const int NodeSize = 16 + 2 * FrameAddress.EncodedSize;
    private const int StateSize = 24;
    private const string WorkspaceKey = "工作区/😀";
    private const string SelectedTag = "selected/历史😀";
    private const string RewindTag = "rewind";
    private const string BranchTag = "branch-two";
    private static readonly HeadHistoryBudget Budget = new(64, 512);

    internal static int Run(string command, string rootPath) {
        string root = Path.GetFullPath(rootPath);
        if (command == "graph-write") { Write(root); }
        else { Read(root); }
        return 0;
    }

    private static void Write(string root) {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) {
            throw new ArgumentException("graph-write requires an absent or empty application root.");
        }
        Directory.CreateDirectory(root);

        HeadEntry selected;
        HeadSetTag savedTag;
        HeadEntry savedTagEntry;
        HeadSetTagInfo savedInfo;
        IReadOnlyDictionary<string, FrameAddress> savedRoots;
        HeadEntryId initialEntryId;
        HeadEntryId selectedEntryId;
        HeadEntryId branchTwoEntryId;
        using (var data = DataStore.Create(Path.Combine(root, "data"), rotationThresholdBytes: 128)) {
            using (var heads = Heads.Create(Path.Combine(root, "heads"), data, reservedHeadIdUpperBound: 2)) {
                byte[] initialBytes = WriteGraph(data, 0, reverseAllocation: false);
                initialEntryId = heads.CreateHead(1, initialBytes, out var outcome).Unwrap();
                Confirmed(outcome);
                HeadEntryId duplicate = heads.PublishHead(initialEntryId.HeadId, initialBytes, out outcome).Unwrap();
                Confirmed(outcome);
                Require(duplicate != initialEntryId, "Same bytes must create a distinct entryId.");
                heads.CreateTag(SelectedTag, [initialEntryId.HeadId], out outcome).Unwrap();
                Confirmed(outcome);

                byte[] nextBytes = WriteGraph(data, 1, reverseAllocation: true);
                HeadEntryId next = heads.PublishHead(initialEntryId.HeadId, nextBytes, out outcome).Unwrap();
                Confirmed(outcome);
                int visited = 0;
                HeadEntry? selectedCandidate = null;
                HeadHistoryEnd end = heads.ReadHeadHistory(initialEntryId.HeadId, Budget, entry => {
                    visited++;
                    if (visited == 2) { selectedCandidate = entry; return false; }
                    return true;
                }).Unwrap();
                Require(end == HeadHistoryEnd.VisitorStopped && visited == 2, "History must stop after selecting a nonhead point.");
                selected = selectedCandidate ?? throw new InvalidDataException("History did not deliver a selected entry.");
                selectedEntryId = selected.Id;
                Require(selectedEntryId == duplicate && selectedEntryId != next, "Fork source must be the exact historical entryId.");
                Require(selected.Payload.SequenceEqual(initialBytes), "Selected bytes changed during later publication.");
                HeadEntry exact = heads.ReadEntry(selectedEntryId).Unwrap();
                Require(exact.Id == selectedEntryId && exact.RecordedAtUnixMs == selected.RecordedAtUnixMs
                    && exact.Payload.SequenceEqual(selected.Payload), "Exact entryId read changed identity, time or bytes.");
                savedRoots = NamedFrameMapCodec.Decode(selected.Payload).Unwrap();
                heads.EnsureUsable();
                ValidateGraph(data, savedRoots, 0);

                HeadId branchTwo = heads.ForkHead(2, selectedEntryId, out outcome).Unwrap();
                Confirmed(outcome);
                HeadId branchThree = heads.ForkHead(selectedEntryId, out outcome).Unwrap();
                Confirmed(outcome);
                Require(branchTwo.LocalId == 2 && branchThree.LocalId == 3, "Reserved and automatic fork allocation changed.");
                Require(heads.ReadHead(branchTwo).Unwrap().Payload.SequenceEqual(selected.Payload), "Reserved fork changed source bytes.");
                Require(heads.ReadHead(branchThree).Unwrap().Payload.SequenceEqual(selected.Payload), "Automatic fork changed source bytes.");

                byte[] branchTwoBytes = WriteGraph(data, 2, reverseAllocation: false);
                branchTwoEntryId = heads.PublishHead(branchTwo, branchTwoBytes, out outcome).Unwrap();
                Confirmed(outcome);
                byte[] branchThreeBytes = WriteGraph(data, 3, reverseAllocation: true);
                heads.PublishHead(branchThree, branchThreeBytes, out outcome).Unwrap();
                Confirmed(outcome);
                HeadId deepFork = heads.ForkHead(branchTwoEntryId, out outcome).Unwrap();
                Confirmed(outcome);
                Require(deepFork.LocalId == 4, "Fork-of-fork automatic allocation changed.");

                HeadEntryId rewind = heads.PublishHead(initialEntryId.HeadId, selected.Payload, out outcome).Unwrap();
                Confirmed(outcome);
                Require(rewind != selectedEntryId && rewind != initialEntryId, "Rewind must append a new entryId.");
                heads.CreateTag(RewindTag, [rewind.HeadId], out outcome).Unwrap();
                Confirmed(outcome);
                heads.CreateTag(BranchTag, [branchTwo, deepFork], out outcome).Unwrap();
                Confirmed(outcome);
                savedTag = heads.ReadTag(SelectedTag).Unwrap();
                savedTagEntry = heads.ReadEntry(savedTag.Entries.Single().Value).Unwrap();
                Require(savedTagEntry.Id == selected.Id, "Tag changed its captured exact Entry identity.");
                savedInfo = heads.ReadTagInfo(SelectedTag).Unwrap();

                // Later source updates must not widen either exact inherited prefix.
                byte[] finalMainBytes = WriteGraph(data, 4, reverseAllocation: false);
                heads.PublishHead(initialEntryId.HeadId, finalMainBytes, out outcome).Unwrap();
                Confirmed(outcome);
                byte[] finalBranchBytes = WriteGraph(data, 6, reverseAllocation: true);
                heads.PublishHead(branchTwo, finalBranchBytes, out outcome).Unwrap();
                Confirmed(outcome);
                Require(selected.Payload.SequenceEqual(savedTagEntry.Payload), "Later updates changed retained entry/tag bytes.");
            }
        }

        // These objects retain their values after both borrowed and owning stores close.
        Require(selected.Id == selectedEntryId && selectedEntryId != initialEntryId, "Retained entryId changed after Dispose.");
        Require(savedTag.Entries.Count == 1 && savedTag.Entries[selectedEntryId.HeadId] == selectedEntryId, "Retained tag table depends on disposed owners.");
        Require(selected.Payload.SequenceEqual(savedTagEntry.Payload), "Retained bytes depend on owner lifetime.");
        Require(NamedFrameMapCodec.Decode(savedTagEntry.Payload).Unwrap()["A"] == savedRoots["A"], "Retained decoded frames changed after Dispose.");
        ValidateInfo(savedInfo, 1);
        Require(branchTwoEntryId.HeadId.LocalId == 2, "Retained branch entryId changed after Dispose.");
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", scenario = "graph-write", pid = Environment.ProcessId,
            refs = 4, entries = 8, forks = 3, tags = 3
        }));
    }

    private static void Read(string root) {
        List<HeadEntry> mainHistory;
        HeadSetTag savedTag;
        HeadEntry savedTagEntry;
        HeadSetTagInfo savedInfo;
        IReadOnlyDictionary<string, FrameAddress> savedRoots;
        HeadStoreAudit audit;
        long dataFrames;
        int dataFiles = 0;
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data"))) {
            using (var heads = Heads.OpenReadOnly(Path.Combine(root, "heads"), data)) {
                Require(data.IsReadOnly && heads.IsReadOnly, "Cold validation must use read-only owners.");
                IReadOnlyList<HeadId> refs = heads.ListHeads().Unwrap();
                Require(refs.Select(id => id.LocalId).Order().SequenceEqual(new uint[] { 1, 2, 3, 4 }), "Unexpected formal ref set.");
                HeadEntry main = heads.ReadHead(1).Unwrap();
                HeadEntry branchTwo = heads.ReadHead(2).Unwrap();
                HeadEntry branchThree = heads.ReadHead(3).Unwrap();
                HeadEntry deepFork = heads.ReadHead(4).Unwrap();
                ValidateGraph(data, NamedFrameMapCodec.Decode(main.Payload).Unwrap(), 4);
                ValidateGraph(data, NamedFrameMapCodec.Decode(branchTwo.Payload).Unwrap(), 6);
                ValidateGraph(data, NamedFrameMapCodec.Decode(branchThree.Payload).Unwrap(), 3);
                ValidateGraph(data, NamedFrameMapCodec.Decode(deepFork.Payload).Unwrap(), 2);

                mainHistory = History(heads, main.Id.HeadId);
                List<HeadEntry> twoHistory = History(heads, branchTwo.Id.HeadId);
                List<HeadEntry> threeHistory = History(heads, branchThree.Id.HeadId);
                List<HeadEntry> deepHistory = History(heads, heads.ListHeads().Unwrap().Single(id => id.LocalId == 4));
                ValidateHistory(data, mainHistory, new[] { 4, 0, 1, 0, 0 }, new uint[] { 1, 1, 1, 1, 1 });
                ValidateHistory(data, twoHistory, new[] { 6, 2, 0, 0 }, new uint[] { 2, 2, 1, 1 });
                ValidateHistory(data, threeHistory, new[] { 3, 0, 0 }, new uint[] { 3, 1, 1 });
                ValidateHistory(data, deepHistory, new[] { 2, 0, 0 }, new uint[] { 2, 1, 1 });
                HeadEntry selected = mainHistory[3];
                heads.EnsureUsable();
                HeadEntry exact = heads.ReadEntry(selected.Id).Unwrap();
                Require(exact.Id == selected.Id && exact.RecordedAtUnixMs == selected.RecordedAtUnixMs
                    && exact.Payload.SequenceEqual(selected.Payload), "Cold exact entryId read differs from history.");
                Require(selected.Id != mainHistory[4].Id && selected.Payload.SequenceEqual(mainHistory[4].Payload), "Same-value entryIds were merged.");
                Require(mainHistory[1].Id != selected.Id && mainHistory[1].Payload.SequenceEqual(selected.Payload), "Rewind did not preserve the old entryId.");
                Require(twoHistory[2].Id == selected.Id && threeHistory[1].Id == selected.Id, "Forks inherited a different source point.");
                Require(twoHistory[3].Id == mainHistory[4].Id && threeHistory[2].Id == mainHistory[4].Id, "Forks lost source initial history.");
                Require(twoHistory[2].Payload.SequenceEqual(selected.Payload) && threeHistory[1].Payload.SequenceEqual(selected.Payload), "Fork initial bytes differ from exact source bytes.");
                Require(deepHistory[0].Payload.SequenceEqual(twoHistory[1].Payload), "Fork-of-fork initial bytes differ from its exact source.");
                for (int i = 1; i < deepHistory.Count; i++) {
                    Require(deepHistory[i].Id == twoHistory[i + 1].Id, "Fork-of-fork inherited prefix changed after a source update.");
                }

                IReadOnlyList<HeadOriginInfo> forks = heads.ListHeadOrigins(4).Unwrap();
                Require(forks.Count == 3, "Unexpected fork declaration count.");
                ValidateFork(forks, 2, selected.Id);
                ValidateFork(forks, 3, selected.Id);
                ValidateFork(forks, 4, twoHistory[1].Id);

                savedTag = heads.ReadTag(SelectedTag).Unwrap();
                savedTagEntry = heads.ReadEntry(savedTag.Entries.Single().Value).Unwrap();
                Require(savedTagEntry.Id == selected.Id, "Tag changed its captured exact Entry identity.");
                Require(savedTagEntry.Payload.SequenceEqual(selected.Payload), "Historical tag changed after source advancement.");
                HeadSetTag rewindTag = heads.ReadTag(RewindTag).Unwrap();
                Require(rewindTag.Entries[main.Id.HeadId] == mainHistory[1].Id, "Rewind tag changed after source advancement.");
                HeadSetTag branchTag = heads.ReadTag(BranchTag).Unwrap();
                Require(branchTag.Entries.Count == 2 && branchTag.Entries[branchTwo.Id.HeadId] == twoHistory[1].Id
                    && branchTag.Entries[heads.ListHeads().Unwrap().Single(id => id.LocalId == 4)] == deepFork.Id, "Anonymous tag changed after branch advancement.");
                savedRoots = NamedFrameMapCodec.Decode(savedTagEntry.Payload).Unwrap();
                ValidateGraph(data, savedRoots, 0);
                ValidateGraph(data, NamedFrameMapCodec.Decode(heads.ReadEntry(branchTag.Entries[branchTwo.Id.HeadId]).Unwrap().Payload).Unwrap(), 2);
                savedInfo = heads.ReadTagInfo(SelectedTag).Unwrap();
                ValidateInfo(savedInfo, 1);
                ValidateInfo(heads.ReadTagInfo(RewindTag).Unwrap(), 1);
                HeadSetTagInfo anonymous = heads.ReadTagInfo(BranchTag).Unwrap();
                ValidateInfo(anonymous, 2);
                var scanned = new Dictionary<string, HeadSetTagInfo>(StringComparer.Ordinal);
                HeadSetTagScanEnd scanEnd = heads.ScanTags(info => {
                    Require(scanned.TryAdd(info.Name, info), "Tag scan returned a duplicate name.");
                    return true;
                }).Unwrap();
                Require(scanEnd == HeadSetTagScanEnd.Complete && scanned.Count == 3, "Tag metadata scan did not complete.");
                Require(scanned.ContainsKey(SelectedTag) && scanned.ContainsKey(RewindTag) && scanned.ContainsKey(BranchTag), "Tag metadata scan missed a name.");
                Require(scanned[SelectedTag].RecordedAtUnixMs == savedInfo.RecordedAtUnixMs, "Tag time changed between metadata queries.");
                ValidateInfo(scanned[SelectedTag], 1);

                audit = heads.Audit().Unwrap();
                Require(audit.HeadCount == 4 && audit.EntryCount == 8 && audit.OriginCount == 3 && audit.TagCount == 3, "Complete HeadStore audit counts changed.");
                Require(audit.TagBucketCount is >= 1 and <= 3, "Unexpected tag bucket count.");
                dataFrames = data.Audit(file => { dataFiles++; }).Unwrap();
                Require(dataFrames == 24 && dataFiles >= 2, "Complete FrameStore audit did not include all graph records.");
            }
        }

        Require(mainHistory[3].Payload.SequenceEqual(savedTagEntry.Payload), "Cold-read objects depend on disposed owners.");
        Require(savedTag.Entries.Count == 1 && savedTag.Entries[mainHistory[3].Id.HeadId] == mainHistory[3].Id,
            "Cold-read tag table depends on disposed owners.");
        Require(NamedFrameMapCodec.Decode(mainHistory[3].Payload).Unwrap()["B"] == savedRoots["B"], "Cold-read root map depends on disposed owners.");
        ValidateInfo(savedInfo, 1);
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", scenario = "graph-read", pid = Environment.ProcessId,
            refs = audit.HeadCount, entries = audit.EntryCount, forks = audit.OriginCount,
            tags = audit.TagCount, tagBuckets = audit.TagBucketCount, dataFrames, dataFiles
        }));
    }

    private static byte[] WriteGraph(DataStore data, int generation, bool reverseAllocation) {
        using var first = data.BeginAppend(NodeSize, 0, out FrameAddress firstAddress);
        using var second = data.BeginAppend(NodeSize, 0, out FrameAddress secondAddress);
        FrameAddress addressA = reverseAllocation ? secondAddress : firstAddress;
        FrameAddress addressB = reverseAllocation ? firstAddress : secondAddress;
        Require(FileId(addressA) != FileId(addressB), "Simultaneous sized builders must allocate different files.");
        byte[] firstBytes = EncodeNode(reverseAllocation ? 2 : 1, generation, secondAddress, firstAddress);
        byte[] secondBytes = EncodeNode(reverseAllocation ? 1 : 2, generation, firstAddress, secondAddress);
        Require(first.PayloadAndMeta.Length == 0 && second.PayloadAndMeta.Length == 0, "User length must start at zero.");
        WriteBytes(first.PayloadAndMeta, firstBytes.AsSpan(0, 16));
        WriteBytes(second.PayloadAndMeta, secondBytes);
        Require(first.PayloadAndMeta.Length == 16 && second.PayloadAndMeta.Length == NodeSize, "Interleaved user lengths differ.");

        // Buffer Append remains usable while both sized builders are still leased.
        FrameAddress counter = data.Append(StateTag, EncodeState(1, generation)).Unwrap();
        Require(second.EndAppend(NodeTag).Unwrap() == secondAddress, "Sized EndAppend changed its early address.");
        using var workspaceBuilder = data.BeginAppend();
        WriteBytes(workspaceBuilder.PayloadAndMeta, EncodeState(2, generation));
        FrameAddress workspace = workspaceBuilder.EndAppend(StateTag).Unwrap();
        WriteBytes(first.PayloadAndMeta, firstBytes.AsSpan(16));
        Require(first.PayloadAndMeta.Length == NodeSize, "Repeated writer access reset its length origin.");
        Require(first.EndAppend(NodeTag).Unwrap() == firstAddress, "Interleaved EndAppend changed its early address.");

        var frames = new Dictionary<string, FrameAddress>(StringComparer.Ordinal) {
            ["A"] = addressA, ["B"] = addressB, ["counter"] = counter, [WorkspaceKey] = workspace
        };
        ValidateGraph(data, frames, generation);
        return NamedFrameMapCodec.Encode(frames).Unwrap();
    }

    private static byte[] EncodeNode(int id, int generation, FrameAddress peer, FrameAddress self) {
        var bytes = new byte[NodeSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RecordVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), generation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), id * 100 + generation);
        Require(peer.TryWrite(bytes.AsSpan(16, FrameAddress.EncodedSize)), "Cannot encode peer address.");
        Require(self.TryWrite(bytes.AsSpan(16 + FrameAddress.EncodedSize, FrameAddress.EncodedSize)), "Cannot encode self address.");
        return bytes;
    }

    private static byte[] EncodeState(int kind, int generation) {
        var bytes = new byte[StateSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RecordVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), generation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 1000 + generation);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 0x1234000000000000L + generation);
        return bytes;
    }

    private static void WriteBytes(FramePayloadWriter writer, ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    private static Node ReadNode(DataStore data, FrameAddress address) {
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        ReadOnlySpan<byte> bytes = frame.Payload;
        Require(frame.Address == address && frame.Tag == NodeTag && frame.TailMetaLength == 0 && !frame.IsTombstone, "Node frame envelope differs.");
        Require(bytes.Length == NodeSize && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == RecordVersion, "Node record schema differs.");
        Require(FrameAddress.TryRead(bytes.Slice(16, FrameAddress.EncodedSize), out FrameAddress peer), "Invalid peer address.");
        Require(FrameAddress.TryRead(bytes.Slice(16 + FrameAddress.EncodedSize, FrameAddress.EncodedSize), out FrameAddress self), "Invalid self address.");
        return new Node(BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]), peer, self);
    }

    private static void ValidateGraph(DataStore data, IReadOnlyDictionary<string, FrameAddress> frames, int generation) {
        Require(frames.Count == 4 && frames.ContainsKey("A") && frames.ContainsKey("B") && frames.ContainsKey("counter") && frames.ContainsKey(WorkspaceKey), "NamedFrameMap keys differ.");
        FrameAddress addressA = frames["A"];
        FrameAddress addressB = frames["B"];
        Require(FileId(addressA) != FileId(addressB), "The cycle must cross data files.");
        Node a = ReadNode(data, addressA);
        Node b = ReadNode(data, addressB);
        Require(a.Id == 1 && b.Id == 2 && a.Generation == generation && b.Generation == generation, "Node identity/generation differs.");
        Require(a.Value == 100 + generation && b.Value == 200 + generation, "Node application values differ.");
        Require(a.Peer == addressB && b.Peer == addressA && a.Self == addressA && b.Self == addressB, "Cycle/self references differ from actual returned addresses.");
        Require(ReadNode(data, a.Peer) == b && ReadNode(data, b.Peer) == a, "Cross-file references cannot be followed.");
        Require(ReadNode(data, a.Self) == a && ReadNode(data, b.Self) == b, "Self references cannot be followed.");
        ValidateState(data, frames["counter"], 1, generation);
        ValidateState(data, frames[WorkspaceKey], 2, generation);
    }

    private static void ValidateState(DataStore data, FrameAddress address, int kind, int generation) {
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        ReadOnlySpan<byte> bytes = frame.Payload;
        Require(frame.Address == address && frame.Tag == StateTag && frame.TailMetaLength == 0 && !frame.IsTombstone, "State frame envelope differs.");
        Require(bytes.Length == StateSize && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == RecordVersion, "State record schema differs.");
        Require(BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) == kind && BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) == generation, "State kind/generation differs.");
        Require(BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) == 1000 + generation
            && BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]) == 0x1234000000000000L + generation, "State application values differ.");
    }

    private static uint FileId(FrameAddress address) {
        Span<byte> encoded = stackalloc byte[FrameAddress.EncodedSize];
        Require(address.TryWrite(encoded), "Cannot encode a returned address.");
        // This is the documented public fixed12 wire, not an internal property or CLR layout.
        return BinaryPrimitives.ReadUInt32LittleEndian(encoded);
    }

    private static List<HeadEntry> History(Heads heads, HeadId headId) {
        var entries = new List<HeadEntry>();
        HeadHistoryEnd end = heads.ReadHeadHistory(headId, Budget, entry => { entries.Add(entry); return true; }).Unwrap();
        Require(end == HeadHistoryEnd.Complete, "Inherited history did not complete.");
        return entries;
    }

    private static void ValidateHistory(DataStore data, List<HeadEntry> entries, int[] generations, uint[] localIds) {
        Require(entries.Count == generations.Length && entries.Count == localIds.Length, "Unexpected inherited history length.");
        Require(entries.Select(entry => entry.Id).Distinct().Count() == entries.Count, "History repeated an actual entryId.");
        for (int i = 0; i < entries.Count; i++) {
            Require(entries[i].Id.HeadId.LocalId == localIds[i], "Inherited history attributed a entry to the wrong ref.");
            ValidateGraph(data, NamedFrameMapCodec.Decode(entries[i].Payload).Unwrap(), generations[i]);
        }
    }

    private static void ValidateFork(IReadOnlyList<HeadOriginInfo> forks, uint childId, HeadEntryId source) {
        HeadOriginInfo fork = forks.Single(item => item.ChildHeadId.LocalId == childId);
        Require(fork.SourceHeadId == source.HeadId && fork.SourceEntryTicket.Packed == source.Ticket.Packed, "Fork header declares a different exact origin.");
    }

    private static void ValidateInfo(HeadSetTagInfo info, int count) {
        Require(info.EntryCount == count, "Tag metadata has a different member count.");
    }

    private static void Confirmed(PublicationOutcome outcome) {
        Require(outcome == PublicationOutcome.Confirmed, "Successful publication did not confirm its output.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private readonly record struct Node(int Id, int Generation, int Value, FrameAddress Peer, FrameAddress Self);
}
