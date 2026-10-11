using System.Buffers.Binary;
using System.Text.Json;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Versions = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Smoke;

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
    private static readonly HistoryBudget Budget = new(64, 512);

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

        RefBytesSnapshot selected;
        RootPayload savedTag;
        TagInfo savedInfo;
        IReadOnlyDictionary<string, FrameAddress> savedRoots;
        RefRevision initialRevision;
        RefRevision selectedRevision;
        RefRevision branchTwoRevision;
        using (var data = DataStore.Create(Path.Combine(root, "data"), rotationThresholdBytes: 128)) {
            using (var versions = Versions.Create(Path.Combine(root, "versions"), data, reservedRefIdUpperBound: 2)) {
                byte[] initialBytes = WriteGraph(data, 0, reverseAllocation: false);
                initialRevision = versions.CreateRef(1, initialBytes, out var outcome).Unwrap();
                Confirmed(outcome);
                RefRevision duplicate = versions.PublishRef(initialRevision.RefId, initialBytes, out outcome).Unwrap();
                Confirmed(outcome);
                Require(duplicate != initialRevision, "Same bytes must create a distinct revision.");

                byte[] nextBytes = WriteGraph(data, 1, reverseAllocation: true);
                RefRevision next = versions.PublishRef(initialRevision.RefId, nextBytes, out outcome).Unwrap();
                Confirmed(outcome);
                int visited = 0;
                RefBytesSnapshot? selectedCandidate = null;
                HistoryEnd end = versions.ReadRefHistory(initialRevision.RefId, Budget, snapshot => {
                    visited++;
                    if (visited == 2) { selectedCandidate = snapshot; return false; }
                    return true;
                }).Unwrap();
                Require(end == HistoryEnd.VisitorStopped && visited == 2, "History must stop after selecting a nonhead point.");
                selected = selectedCandidate ?? throw new InvalidDataException("History did not deliver a selected snapshot.");
                selectedRevision = selected.Revision;
                Require(selectedRevision == duplicate && selectedRevision != next, "Fork source must be the exact historical revision.");
                Require(selected.Payload.SequenceEqual(initialBytes), "Selected bytes changed during later publication.");
                RefBytesSnapshot exact = versions.ReadRevision(selectedRevision).Unwrap();
                Require(exact.Revision == selectedRevision && exact.RecordedAtUnixMs == selected.RecordedAtUnixMs
                    && exact.Payload.SequenceEqual(selected.Payload), "Exact revision read changed identity, time or bytes.");
                savedRoots = RootMapCodec.Decode(selected.Payload).Unwrap();
                versions.EnsureUsable();
                ValidateGraph(data, savedRoots, 0);

                RefRevision branchTwo = versions.ForkRef(2, selectedRevision, out outcome).Unwrap();
                Confirmed(outcome);
                RefRevision branchThree = versions.ForkRef(selectedRevision, out outcome).Unwrap();
                Confirmed(outcome);
                Require(branchTwo.RefId.LocalId == 2 && branchThree.RefId.LocalId == 3, "Reserved and automatic fork allocation changed.");
                Require(versions.ReadRef(branchTwo.RefId).Unwrap().Payload.SequenceEqual(selected.Payload), "Reserved fork did not clone source bytes.");
                Require(versions.ReadRef(branchThree.RefId).Unwrap().Payload.SequenceEqual(selected.Payload), "Automatic fork did not clone source bytes.");

                byte[] branchTwoBytes = WriteGraph(data, 2, reverseAllocation: false);
                branchTwoRevision = versions.PublishRef(branchTwo.RefId, branchTwoBytes, out outcome).Unwrap();
                Confirmed(outcome);
                byte[] branchThreeBytes = WriteGraph(data, 3, reverseAllocation: true);
                versions.PublishRef(branchThree.RefId, branchThreeBytes, out outcome).Unwrap();
                Confirmed(outcome);
                RefRevision deepFork = versions.ForkRef(branchTwoRevision, out outcome).Unwrap();
                Confirmed(outcome);
                Require(deepFork.RefId.LocalId == 4, "Fork-of-fork automatic allocation changed.");

                RefRevision rewind = versions.PublishRef(initialRevision.RefId, selected.Payload, out outcome).Unwrap();
                Confirmed(outcome);
                Require(rewind != selectedRevision && rewind != initialRevision, "Rewind must append a new revision.");
                Require(versions.CreateTag(SelectedTag, selected.Payload, selectedRevision, out outcome).Unwrap(), "Tag creation must return true.");
                Confirmed(outcome);
                Require(versions.CreateTag(RewindTag, selected.Payload, rewind, out outcome).Unwrap(), "Rewind tag creation must return true.");
                Confirmed(outcome);
                Require(versions.CreateTag(BranchTag, branchTwoBytes, out outcome).Unwrap(), "Anonymous tag creation must return true.");
                Confirmed(outcome);
                savedTag = versions.ResolveTag(SelectedTag).Unwrap();
                savedInfo = versions.ReadTagInfo(SelectedTag).Unwrap();

                // Later source updates must not widen either exact inherited prefix.
                byte[] finalMainBytes = WriteGraph(data, 4, reverseAllocation: false);
                versions.PublishRef(initialRevision.RefId, finalMainBytes, out outcome).Unwrap();
                Confirmed(outcome);
                byte[] finalBranchBytes = WriteGraph(data, 6, reverseAllocation: true);
                versions.PublishRef(branchTwo.RefId, finalBranchBytes, out outcome).Unwrap();
                Confirmed(outcome);
                Require(selected.Payload.SequenceEqual(savedTag.Bytes), "Later updates changed retained snapshot/tag bytes.");
            }
        }

        // These objects retain their values after both borrowed and owning stores close.
        Require(selected.Revision == selectedRevision && selectedRevision != initialRevision, "Retained revision changed after Dispose.");
        Require(selected.Payload.SequenceEqual(savedTag.Bytes), "Retained bytes depend on owner lifetime.");
        Require(RootMapCodec.Decode(savedTag.Bytes).Unwrap()["A"] == savedRoots["A"], "Retained decoded roots changed after Dispose.");
        ValidateRelated(savedInfo, selectedRevision);
        Require(branchTwoRevision.RefId.LocalId == 2, "Retained branch revision changed after Dispose.");
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", scenario = "graph-write", pid = Environment.ProcessId,
            refs = 4, snapshots = 11, forks = 3, tags = 3
        }));
    }

    private static void Read(string root) {
        List<RefBytesSnapshot> mainHistory;
        RootPayload savedTag;
        TagInfo savedInfo;
        IReadOnlyDictionary<string, FrameAddress> savedRoots;
        VersionStoreAudit audit;
        long dataFrames;
        int dataFiles = 0;
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data"))) {
            using (var versions = Versions.OpenReadOnly(Path.Combine(root, "versions"), data)) {
                Require(data.IsReadOnly && versions.IsReadOnly, "Cold validation must use read-only owners.");
                IReadOnlyList<RefId> refs = versions.ListRefs().Unwrap();
                Require(refs.Select(id => id.LocalId).Order().SequenceEqual(new uint[] { 1, 2, 3, 4 }), "Unexpected formal ref set.");
                RefBytesSnapshot main = versions.ReadRef(1).Unwrap();
                RefBytesSnapshot branchTwo = versions.ReadRef(2).Unwrap();
                RefBytesSnapshot branchThree = versions.ReadRef(3).Unwrap();
                RefBytesSnapshot deepFork = versions.ReadRef(4).Unwrap();
                ValidateGraph(data, RootMapCodec.Decode(main.Payload).Unwrap(), 4);
                ValidateGraph(data, RootMapCodec.Decode(branchTwo.Payload).Unwrap(), 6);
                ValidateGraph(data, RootMapCodec.Decode(branchThree.Payload).Unwrap(), 3);
                ValidateGraph(data, RootMapCodec.Decode(deepFork.Payload).Unwrap(), 2);

                mainHistory = History(versions, main.Revision.RefId);
                List<RefBytesSnapshot> twoHistory = History(versions, branchTwo.Revision.RefId);
                List<RefBytesSnapshot> threeHistory = History(versions, branchThree.Revision.RefId);
                List<RefBytesSnapshot> deepHistory = History(versions, deepFork.Revision.RefId);
                ValidateHistory(data, mainHistory, new[] { 4, 0, 1, 0, 0 }, new uint[] { 1, 1, 1, 1, 1 });
                ValidateHistory(data, twoHistory, new[] { 6, 2, 0, 0, 0 }, new uint[] { 2, 2, 2, 1, 1 });
                ValidateHistory(data, threeHistory, new[] { 3, 0, 0, 0 }, new uint[] { 3, 3, 1, 1 });
                ValidateHistory(data, deepHistory, new[] { 2, 2, 0, 0, 0 }, new uint[] { 4, 2, 2, 1, 1 });
                RefBytesSnapshot selected = mainHistory[3];
                versions.EnsureUsable();
                RefBytesSnapshot exact = versions.ReadRevision(selected.Revision).Unwrap();
                Require(exact.Revision == selected.Revision && exact.RecordedAtUnixMs == selected.RecordedAtUnixMs
                    && exact.Payload.SequenceEqual(selected.Payload), "Cold exact revision read differs from history.");
                Require(selected.Revision != mainHistory[4].Revision && selected.Payload.SequenceEqual(mainHistory[4].Payload), "Same-value revisions were merged.");
                Require(mainHistory[1].Revision != selected.Revision && mainHistory[1].Payload.SequenceEqual(selected.Payload), "Rewind did not preserve the old revision.");
                Require(twoHistory[3].Revision == selected.Revision && threeHistory[2].Revision == selected.Revision, "Forks inherited a different source point.");
                Require(twoHistory[4].Revision == mainHistory[4].Revision && threeHistory[3].Revision == mainHistory[4].Revision, "Forks lost source initial history.");
                Require(twoHistory[2].Payload.SequenceEqual(selected.Payload) && threeHistory[1].Payload.SequenceEqual(selected.Payload), "Fork initial bytes differ from exact source bytes.");
                Require(deepHistory[0].Payload.SequenceEqual(twoHistory[1].Payload), "Fork-of-fork initial bytes differ from its exact source.");
                for (int i = 1; i < deepHistory.Count; i++) {
                    Require(deepHistory[i].Revision == twoHistory[i].Revision, "Fork-of-fork inherited prefix changed after a source update.");
                }

                IReadOnlyList<ForkInfo> forks = versions.ListForks(4).Unwrap();
                Require(forks.Count == 3, "Unexpected fork declaration count.");
                ValidateFork(forks, 2, selected.Revision);
                ValidateFork(forks, 3, selected.Revision);
                ValidateFork(forks, 4, twoHistory[1].Revision);

                savedTag = versions.ResolveTag(SelectedTag).Unwrap();
                Require(savedTag.Bytes.SequenceEqual(selected.Payload), "Historical tag changed after source advancement.");
                RootPayload rewindTag = versions.ResolveTag(RewindTag).Unwrap();
                Require(rewindTag.Bytes.SequenceEqual(mainHistory[1].Payload), "Rewind tag changed after source advancement.");
                RootPayload branchTag = versions.ResolveTag(BranchTag).Unwrap();
                Require(branchTag.Bytes.SequenceEqual(twoHistory[1].Payload), "Anonymous tag changed after branch advancement.");
                savedRoots = RootMapCodec.Decode(savedTag.Bytes).Unwrap();
                ValidateGraph(data, savedRoots, 0);
                ValidateGraph(data, RootMapCodec.Decode(branchTag.Bytes).Unwrap(), 2);
                savedInfo = versions.ReadTagInfo(SelectedTag).Unwrap();
                ValidateRelated(savedInfo, selected.Revision);
                ValidateRelated(versions.ReadTagInfo(RewindTag).Unwrap(), mainHistory[1].Revision);
                TagInfo anonymous = versions.ReadTagInfo(BranchTag).Unwrap();
                Require(anonymous.RelatedRefId is null && anonymous.RelatedTicket is null, "Anonymous tag acquired a related coordinate.");
                var scanned = new Dictionary<string, TagInfo>(StringComparer.Ordinal);
                TagScanEnd scanEnd = versions.ScanTags(info => {
                    Require(scanned.TryAdd(info.Name, info), "Tag scan returned a duplicate name.");
                    return true;
                }).Unwrap();
                Require(scanEnd == TagScanEnd.Complete && scanned.Count == 3, "Tag metadata scan did not complete.");
                Require(scanned.ContainsKey(SelectedTag) && scanned.ContainsKey(RewindTag) && scanned.ContainsKey(BranchTag), "Tag metadata scan missed a name.");
                Require(scanned[SelectedTag].RecordedAtUnixMs == savedInfo.RecordedAtUnixMs, "Tag time changed between metadata queries.");
                ValidateRelated(scanned[SelectedTag], selected.Revision);

                audit = versions.Audit().Unwrap();
                Require(audit.RefCount == 4 && audit.SnapshotCount == 11 && audit.ForkCount == 3 && audit.TagCount == 3, "Complete VersionStore audit counts changed.");
                Require(audit.TagBucketCount is >= 1 and <= 3, "Unexpected tag bucket count.");
                dataFrames = data.Audit(file => { dataFiles++; }).Unwrap();
                Require(dataFrames == 24 && dataFiles >= 2, "Complete FrameStore audit did not include all graph records.");
            }
        }

        Require(mainHistory[3].Payload.SequenceEqual(savedTag.Bytes), "Cold-read objects depend on disposed owners.");
        Require(RootMapCodec.Decode(mainHistory[3].Payload).Unwrap()["B"] == savedRoots["B"], "Cold-read root map depends on disposed owners.");
        ValidateRelated(savedInfo, mainHistory[3].Revision);
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", scenario = "graph-read", pid = Environment.ProcessId,
            refs = audit.RefCount, snapshots = audit.SnapshotCount, forks = audit.ForkCount,
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

        var roots = new Dictionary<string, FrameAddress>(StringComparer.Ordinal) {
            ["A"] = addressA, ["B"] = addressB, ["counter"] = counter, [WorkspaceKey] = workspace
        };
        ValidateGraph(data, roots, generation);
        return RootMapCodec.Encode(roots).Unwrap();
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

    private static void ValidateGraph(DataStore data, IReadOnlyDictionary<string, FrameAddress> roots, int generation) {
        Require(roots.Count == 4 && roots.ContainsKey("A") && roots.ContainsKey("B") && roots.ContainsKey("counter") && roots.ContainsKey(WorkspaceKey), "RootMap keys differ.");
        FrameAddress addressA = roots["A"];
        FrameAddress addressB = roots["B"];
        Require(FileId(addressA) != FileId(addressB), "The cycle must cross data files.");
        Node a = ReadNode(data, addressA);
        Node b = ReadNode(data, addressB);
        Require(a.Id == 1 && b.Id == 2 && a.Generation == generation && b.Generation == generation, "Node identity/generation differs.");
        Require(a.Value == 100 + generation && b.Value == 200 + generation, "Node application values differ.");
        Require(a.Peer == addressB && b.Peer == addressA && a.Self == addressA && b.Self == addressB, "Cycle/self references differ from actual returned addresses.");
        Require(ReadNode(data, a.Peer) == b && ReadNode(data, b.Peer) == a, "Cross-file references cannot be followed.");
        Require(ReadNode(data, a.Self) == a && ReadNode(data, b.Self) == b, "Self references cannot be followed.");
        ValidateState(data, roots["counter"], 1, generation);
        ValidateState(data, roots[WorkspaceKey], 2, generation);
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

    private static List<RefBytesSnapshot> History(Versions versions, RefId refId) {
        var snapshots = new List<RefBytesSnapshot>();
        HistoryEnd end = versions.ReadRefHistory(refId, Budget, snapshot => { snapshots.Add(snapshot); return true; }).Unwrap();
        Require(end == HistoryEnd.Complete, "Inherited history did not complete.");
        return snapshots;
    }

    private static void ValidateHistory(DataStore data, List<RefBytesSnapshot> snapshots, int[] generations, uint[] localIds) {
        Require(snapshots.Count == generations.Length && snapshots.Count == localIds.Length, "Unexpected inherited history length.");
        Require(snapshots.Select(snapshot => snapshot.Revision).Distinct().Count() == snapshots.Count, "History repeated an actual revision.");
        for (int i = 0; i < snapshots.Count; i++) {
            Require(snapshots[i].Revision.RefId.LocalId == localIds[i], "Inherited history attributed a snapshot to the wrong ref.");
            ValidateGraph(data, RootMapCodec.Decode(snapshots[i].Payload).Unwrap(), generations[i]);
        }
    }

    private static void ValidateFork(IReadOnlyList<ForkInfo> forks, uint childId, RefRevision source) {
        ForkInfo fork = forks.Single(item => item.ChildRefId.LocalId == childId);
        Require(fork.SourceRefId == source.RefId && fork.SourceSnapshotTicket.Packed == source.Ticket.Packed, "Fork header declares a different exact origin.");
    }

    private static void ValidateRelated(TagInfo info, RefRevision revision) {
        Require(info.RelatedRefId == revision.RefId && info.RelatedTicket?.Packed == revision.Ticket.Packed, "Tag metadata has a different related coordinate.");
    }

    private static void Confirmed(PublicationOutcome outcome) {
        Require(outcome == PublicationOutcome.Confirmed, "Successful publication did not confirm its output.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private readonly record struct Node(int Id, int Generation, int Value, FrameAddress Peer, FrameAddress Self);
}
