using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using VersionOwner = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Smoke;

/// <summary>Only public storage APIs are used at the real process boundary.</summary>
internal static class ProcessScenario {
    private const uint BaselineRef = 1;
    private const uint CreatedRef = 2;
    private const string CrashTag = "crash-tag";
    private const int OpaqueLength = 34;
    private static readonly byte[] OpaquePrefix = [0xf7, 0x00, 0xff, 0x81];
    private static readonly byte[] OpaqueSuffix = [0x00, 0xfe, 0x80, 0xff, 0x00];
    private static readonly string[] Checkpoints = [
        "unpublished-data", "confirmed-a-active-b", "ref-confirmed",
        "create-confirmed", "fork-confirmed", "tag-confirmed"
    ];

    internal static int Run(string[] args) {
        if (args.Length < 2) { throw new ArgumentException("Expected a process command and fixture root."); }
        string root = Path.GetFullPath(args[1]);
        switch (args[0]) {
            case "seed" when args.Length == 2:
                Seed(root);
                break;
            case "hold" when args.Length == 3:
                Hold(root, ReadMode(args[2]));
                break;
            case "probe" when args.Length == 3:
                Probe(root, ReadMode(args[2]));
                break;
            case "crash-worker" when args.Length == 3:
                CrashWorker(root, ReadCheckpoint(args[2]));
                break;
            case "verify" when args.Length == 3:
                Verify(root, ReadCheckpoint(args[2]));
                break;
            default:
                throw new ArgumentException("Unknown process command or invalid argument count.");
        }
        return 0;
    }

    private static void Seed(string root) {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) {
            throw new InvalidOperationException("seed requires a fresh, empty fixture root.");
        }
        Directory.CreateDirectory(root);
        SeedManifest seed;
        using (var data = DataStore.Create(DataPath(root))) {
            using var versions = VersionOwner.Create(VersionPath(root), data, reservedRefIdUpperBound: 8);
            byte[] payload = AppendOpaque(data, generation: 0);
            RefRevision revision = versions.CreateRef(BaselineRef, payload, out PublicationOutcome outcome).Unwrap();
            RequireConfirmed(outcome);
            seed = new SeedManifest(Convert.ToBase64String(data.StoreId),
                Convert.ToBase64String(versions.VersionStoreId), Convert.ToBase64String(payload), revision.Ticket.Packed);
        }

        // This is a cold test-fixture mirror, not a storage cloning API. Both seed
        // owners are already closed. Separate data locks expose the VS lock itself.
        CopyColdFixture(DataPath(root), ProbeDataPath(root));
        WriteManifest(Path.Combine(root, "process-seed.json"), seed);
        Emit(new { kind = "completed", command = "seed", pid = Environment.ProcessId,
            coldDataMirror = true, refCount = 1 });
    }

    private static void Hold(string root, string mode) {
        bool readOnly = IsReadOnly(mode);
        // Every holder keeps the original borrowed owner alive until after VS closes.
        using (var data = OpenData(DataPath(root), readOnly)) {
            using (var versions = OpenVersions(VersionPath(root), data, readOnly)) {
                CheckIdentity(data, versions, ReadSeed(root));
                Emit(new { kind = "ready", command = "hold", pid = Environment.ProcessId, mode,
                    dataOpened = true, versionsOpened = true });
                string? input = Console.ReadLine();
                if (input != "release") {
                    throw new InvalidOperationException("hold requires the exact release command; EOF is not release.");
                }
            }
            data.EnsureUsable();
        }
        Emit(new { kind = "completed", command = "hold", pid = Environment.ProcessId, mode });
    }

    private static void Probe(string root, string mode) {
        bool readOnly = IsReadOnly(mode);
        bool isolatedVersions = mode.StartsWith("vs-", StringComparison.Ordinal);
        string dataPath = isolatedVersions ? ProbeDataPath(root) : DataPath(root);
        DataStore data;
        try { data = OpenData(dataPath, readOnly); }
        catch (Exception error) when (TryOwnerLockConflict(error, "framestore.lock", out _)) {
            EmitRejected(mode, "data", dataOpened: false, error, "framestore.lock");
            return;
        }
        using (data) {
            VersionOwner versions;
            try { versions = OpenVersions(VersionPath(root), data, readOnly); }
            catch (Exception error) when (TryOwnerLockConflict(error, "versionstore.lock", out _)) {
                EmitRejected(mode, "versions", dataOpened: true, error, "versionstore.lock");
                return;
            }
            using (versions) { CheckIdentity(data, versions, ReadSeed(root)); }
            data.EnsureUsable();
        }
        Emit(new { kind = "opened", command = "probe", pid = Environment.ProcessId, mode,
            stage = "versions", dataOpened = true, versionsOpened = true });
    }

    private static void CrashWorker(string root, string checkpoint) {
        SeedManifest seed = ReadSeed(root);
        using var data = DataStore.Open(DataPath(root));
        using var versions = VersionOwner.Open(VersionPath(root), data);
        CheckIdentity(data, versions, seed);
        RefBytesSnapshot baseline = versions.ReadRef(BaselineRef).Unwrap();
        RequireEqual(Convert.FromBase64String(seed.BaselinePayload), baseline.Payload, "baseline opaque bytes");
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        byte[] expectedPayload = Convert.FromBase64String(seed.BaselinePayload);
        FrameBuilder activeBuilder = default;
        string? leftAddress = null;
        string? buildingAddress = null;
        string? rightAddress = null;
        try {
            switch (checkpoint) {
                case "unpublished-data":
                    // A complete Append without a root publication need not survive kill.
                    _ = AppendOpaque(data, generation: 1);
                    break;
                case "confirmed-a-active-b":
                    // The first dependency is completed dirty output in the file B then
                    // leases. The second dependency completes in a different active file.
                    FrameAddress left = data.Append(101, DataBytes(1, 0)).Unwrap();
                    activeBuilder = data.BeginAppend(4096, 0, out FrameAddress earlyB);
                    byte[] partial = "unrelated B is still Building"u8.ToArray();
                    var writer = activeBuilder.PayloadAndMeta;
                    partial.CopyTo(writer.GetSpan(partial.Length));
                    writer.Advance(partial.Length);
                    long buildingLength = writer.Length;
                    FrameAddress right = data.Append(102, DataBytes(1, 1)).Unwrap();
                    byte[] leftWire = AddressWire(left);
                    byte[] buildingWire = AddressWire(earlyB);
                    byte[] rightWire = AddressWire(right);
                    // Compare the public fixed12 wire's file component only for this
                    // coverage assertion; application navigation uses the full addresses.
                    Require(leftWire.AsSpan(0, sizeof(uint)).SequenceEqual(buildingWire.AsSpan(0, sizeof(uint))),
                        "The completed dirty prefix was not in B's leased file.");
                    Require(!leftWire.AsSpan(0, sizeof(uint)).SequenceEqual(rightWire.AsSpan(0, sizeof(uint))),
                        "The second dependency did not complete in a separate file.");
                    leftAddress = Convert.ToBase64String(leftWire);
                    buildingAddress = Convert.ToBase64String(buildingWire);
                    rightAddress = Convert.ToBase64String(rightWire);
                    expectedPayload = EncodeOpaque(1, left, right);
                    _ = versions.PublishRef(baseline.Revision.RefId, expectedPayload, out outcome).Unwrap();
                    RequireConfirmed(outcome);
                    // PayloadAndMeta checks the actual live lease. A barrier that
                    // canceled/disposed B cannot reach READY by a boolean annotation.
                    Require(activeBuilder.PayloadAndMeta.Length == buildingLength,
                        "Publishing A ended or changed the unrelated active B builder.");
                    break;
                case "ref-confirmed":
                    expectedPayload = AppendOpaque(data, generation: 1);
                    _ = versions.PublishRef(baseline.Revision.RefId, expectedPayload, out outcome).Unwrap();
                    RequireConfirmed(outcome);
                    break;
                case "create-confirmed":
                    expectedPayload = AppendOpaque(data, generation: 1);
                    _ = versions.CreateRef(CreatedRef, expectedPayload, out outcome).Unwrap();
                    RequireConfirmed(outcome);
                    break;
                case "fork-confirmed":
                    _ = versions.ForkRef(CreatedRef, baseline.Revision, out outcome).Unwrap();
                    RequireConfirmed(outcome);
                    break;
                case "tag-confirmed":
                    expectedPayload = AppendOpaque(data, generation: 1);
                    Require(versions.CreateTag(CrashTag, expectedPayload, baseline.Revision, out outcome).Unwrap(),
                        "CreateTag returned false.");
                    RequireConfirmed(outcome);
                    break;
            }
            WriteManifest(Path.Combine(root, "process-checkpoint.json"),
                new CheckpointManifest(checkpoint, Convert.ToBase64String(expectedPayload)));
            Emit(new { kind = "ready", command = "crash-worker", pid = Environment.ProcessId, checkpoint,
                outcome = outcome.ToString(), activeBuilder = checkpoint == "confirmed-a-active-b",
                leftAddress, buildingAddress, rightAddress,
                publicBoundary = "after-call-before-owner-dispose" });
            // There is no successful graceful worker path: only the parent kill is
            // accepted evidence. EOF or an input line makes this command fail.
            string? input = Console.ReadLine();
            throw new InvalidOperationException(input is null
                ? "crash-worker reached EOF before the parent killed it."
                : "crash-worker must be killed, not released through stdin.");
        }
        finally { activeBuilder.Dispose(); }
    }

    private static void Verify(string root, string checkpoint) {
        SeedManifest seed = ReadSeed(root);
        CheckpointManifest manifest = ReadManifest<CheckpointManifest>(Path.Combine(root, "process-checkpoint.json"));
        Require(manifest.Checkpoint == checkpoint, "Checkpoint manifest does not match the requested scenario.");
        int recoveryReports;
        object[] recoveryDetails;
        // This command runs in a new process. RW opens recover data eagerly and
        // the necessary ref/tag content through their normal public read paths.
        using (var data = DataStore.Open(DataPath(root))) {
            using var versions = VersionOwner.Open(VersionPath(root), data);
            CheckIdentity(data, versions, seed);
            ValidatePublishedState(data, versions, seed, manifest);
            recoveryReports = data.RecoveryReports.Count;
            recoveryDetails = data.RecoveryReports.OrderBy(entry => entry.Key).Select(entry => (object)new {
                fileId = entry.Key, action = entry.Value.Action.ToString(),
                originalLength = entry.Value.OriginalLength, finalLength = entry.Value.FinalLength,
                affectedFrameOffset = entry.Value.AffectedFrameOffset,
                frameTicket = entry.Value.FrameTicket?.Packed
            }).ToArray();
        }

        VersionStoreAudit? audit = null;
        long dataFrames = 0;
        for (int pass = 0; pass < 2; pass++) {
            using var data = DataStore.OpenReadOnly(DataPath(root));
            using var versions = VersionOwner.OpenReadOnly(VersionPath(root), data);
            CheckIdentity(data, versions, seed);
            ValidatePublishedState(data, versions, seed, manifest);
            audit = versions.Audit().Unwrap();
            dataFrames = data.Audit(static _ => { }).Unwrap();
            long expectedRefs = checkpoint is "create-confirmed" or "fork-confirmed" ? 2 : 1;
            long expectedSnapshots = checkpoint is "ref-confirmed" or "confirmed-a-active-b"
                or "create-confirmed" or "fork-confirmed" ? 2 : 1;
            Require(audit.RefCount == expectedRefs && audit.SnapshotCount == expectedSnapshots,
                "Unexpected physical ref or Snapshot count after recovery.");
            Require(audit.ForkCount == (checkpoint == "fork-confirmed" ? 1 : 0), "Unexpected fork count.");
            Require(audit.TagCount == (checkpoint == "tag-confirmed" ? 1 : 0), "Unexpected tag count.");
        }
        Emit(new { kind = "completed", command = "verify", pid = Environment.ProcessId, checkpoint,
            roPasses = 2, recoveryReports, recoveryDetails, dataFrames, refCount = audit!.RefCount,
            snapshotCount = audit.SnapshotCount, forkCount = audit.ForkCount, tagCount = audit.TagCount,
            opaqueAndDataClosure = true, internalFlushRenameWindowsCovered = false });
    }

    private static void ValidatePublishedState(DataStore data, VersionOwner versions,
        SeedManifest seed, CheckpointManifest manifest) {
        string checkpoint = manifest.Checkpoint;
        byte[] baselinePayload = Convert.FromBase64String(seed.BaselinePayload);
        byte[] expectedPayload = Convert.FromBase64String(manifest.ExpectedPayload);
        RefBytesSnapshot current = versions.ReadRef(BaselineRef).Unwrap();
        bool updated = checkpoint is "ref-confirmed" or "confirmed-a-active-b";
        ValidateOpaque(data, current.Payload, updated ? expectedPayload : baselinePayload, updated ? 1 : 0);
        if (!updated) {
            Require(current.Revision.Ticket.Packed == seed.BaselineTicket, "An unrelated operation changed baseline revision.");
        }
        else { Require(current.Revision.Ticket.Packed != seed.BaselineTicket, "Publication did not create a new revision."); }

        var refs = versions.ListRefs().Unwrap();
        uint[] actualIds = refs.Select(id => id.LocalId).Order().ToArray();
        uint[] expectedIds = checkpoint is "create-confirmed" or "fork-confirmed" ? [BaselineRef, CreatedRef] : [BaselineRef];
        Require(actualIds.SequenceEqual(expectedIds), "The formal ref set differs from the public checkpoint.");
        if (checkpoint is "create-confirmed" or "fork-confirmed") {
            RefBytesSnapshot child = versions.ReadRef(CreatedRef).Unwrap();
            ValidateOpaque(data, child.Payload, expectedPayload, checkpoint == "create-confirmed" ? 1 : 0);
        }
        var forks = versions.ListForks(8).Unwrap();
        if (checkpoint == "fork-confirmed") {
            Require(forks.Count == 1, "Expected one declared fork.");
            ForkInfo fork = forks[0];
            Require(fork.ChildRefId.LocalId == CreatedRef && fork.SourceRefId == current.Revision.RefId
                && fork.SourceSnapshotTicket.Packed == seed.BaselineTicket, "The fork source is not the selected exact revision.");
        }
        else { Require(forks.Count == 0, "An unrelated operation created a fork."); }

        if (checkpoint == "tag-confirmed") {
            RootPayload tag = versions.ResolveTag(CrashTag).Unwrap();
            ValidateOpaque(data, tag.Bytes, expectedPayload, 1);
            TagInfo info = versions.ReadTagInfo(CrashTag).Unwrap();
            Require(info.RelatedRefId == current.Revision.RefId
                && info.RelatedTicket?.Packed == seed.BaselineTicket, "Tag Related coordinates differ from the supplied revision.");
        }
        else {
            var tag = versions.ResolveTag(CrashTag);
            Require(tag.IsFailure && tag.Error!.ErrorCode == "VersionStore.TagNotFound", "An unpublished tag became visible.");
        }
    }

    private static byte[] AppendOpaque(DataStore data, int generation) {
        FrameAddress left = data.Append(101, DataBytes(generation, 0)).Unwrap();
        FrameAddress right = data.Append(102, DataBytes(generation, 1)).Unwrap();
        return EncodeOpaque(generation, left, right);
    }

    private static byte[] EncodeOpaque(int generation, FrameAddress left, FrameAddress right) {
        var bytes = new byte[OpaqueLength];
        OpaquePrefix.CopyTo(bytes, 0);
        bytes[4] = checked((byte)generation);
        Require(left.TryWrite(bytes.AsSpan(5, FrameAddress.EncodedSize)), "Cannot encode the first public address.");
        Require(right.TryWrite(bytes.AsSpan(17, FrameAddress.EncodedSize)), "Cannot encode the second public address.");
        OpaqueSuffix.CopyTo(bytes, 29);
        return bytes;
    }

    private static void ValidateOpaque(DataStore data, ReadOnlySpan<byte> actual, byte[] expected, int generation) {
        RequireEqual(expected, actual, "opaque input/output bytes");
        Require(actual.Length == OpaqueLength && actual[..4].SequenceEqual(OpaquePrefix)
            && actual[4] == generation && actual[29..].SequenceEqual(OpaqueSuffix), "Invalid application opaque schema.");
        for (int slot = 0; slot < 2; slot++) {
            Require(FrameAddress.TryRead(actual.Slice(5 + slot * 12, 12), out FrameAddress address),
                "Invalid public application address.");
            using FrameRead frame = data.ReadFrame(address).Unwrap();
            Require(frame.Tag == 101u + (uint)slot && frame.TailMetaLength == 0 && !frame.IsTombstone,
                "The application address resolved to the wrong frame shape.");
            RequireEqual(DataBytes(generation, slot), frame.PayloadAndMeta, "application data closure");
        }
    }

    private static byte[] DataBytes(int generation, int slot) =>
        Encoding.UTF8.GetBytes($"process:{generation}:{slot}:data\0\u007f");

    private static void CheckIdentity(DataStore data, VersionOwner versions, SeedManifest seed) {
        RequireEqual(Convert.FromBase64String(seed.DataStoreId), data.StoreId, "data identity");
        RequireEqual(Convert.FromBase64String(seed.VersionStoreId), versions.VersionStoreId, "VersionStore identity");
    }

    private static void EmitRejected(string mode, string stage, bool dataOpened, Exception error, string lockFile) {
        Require(TryOwnerLockConflict(error, lockFile, out int nativeCode), "Unrecognized owner lock conflict.");
        Emit(new { kind = "rejected", command = "probe", pid = Environment.ProcessId, mode, stage,
            dataOpened, nativeCode, lockFile, errorType = error.GetType().FullName });
    }

    private static bool TryOwnerLockConflict(Exception error, string lockFile, out int nativeCode) {
        nativeCode = 0;
        // Do not accept AggregateException(lock conflict + failed cleanup), wrappers,
        // metadata sharing failures, or arbitrary IOException as lock evidence.
        if (error is not IOException || error.InnerException is not Win32Exception native) { return false; }
        bool ownerOperation = OperatingSystem.IsWindows()
            ? (native.NativeErrorCode is 32 or 33) && error.Message.Contains("CreateFile owner lock", StringComparison.Ordinal)
            : OperatingSystem.IsLinux() && native.NativeErrorCode == 11
                && error.Message.Contains("flock owner lock", StringComparison.Ordinal);
        if (!ownerOperation || !error.Message.Contains(lockFile, StringComparison.Ordinal)) { return false; }
        nativeCode = native.NativeErrorCode;
        return true;
    }

    private static byte[] AddressWire(FrameAddress address) {
        var bytes = new byte[FrameAddress.EncodedSize];
        Require(address.TryWrite(bytes), "Cannot encode a public diagnostic address.");
        return bytes;
    }

    private static void CopyColdFixture(string source, string destination) {
        if (Directory.Exists(destination) || File.Exists(destination)) {
            throw new InvalidOperationException("The cold probe-data destination must not exist.");
        }
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source)) {
            FileAttributes attributes = File.GetAttributes(entry);
            Require((attributes & FileAttributes.ReparsePoint) == 0, "A cold fixture cannot contain reparse points.");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) { CopyColdFixture(entry, target); }
            else { File.Copy(entry, target, overwrite: false); }
        }
    }

    private static void WriteManifest<T>(string path, T value) {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(flushToDisk: true);
    }

    private static T ReadManifest<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing fixture manifest content.");

    private static SeedManifest ReadSeed(string root) => ReadManifest<SeedManifest>(Path.Combine(root, "process-seed.json"));
    private static string DataPath(string root) => Path.Combine(root, "data");
    private static string ProbeDataPath(string root) => Path.Combine(root, "probe-data");
    private static string VersionPath(string root) => Path.Combine(root, "versions");
    private static DataStore OpenData(string path, bool readOnly) => readOnly ? DataStore.OpenReadOnly(path) : DataStore.Open(path);
    private static VersionOwner OpenVersions(string path, DataStore data, bool readOnly) =>
        readOnly ? VersionOwner.OpenReadOnly(path, data) : VersionOwner.Open(path, data);
    private static bool IsReadOnly(string mode) => mode is "read" or "vs-read";
    private static string ReadMode(string mode) => mode is "read" or "write" or "vs-read" or "vs-write"
        ? mode : throw new ArgumentException("Expected read, write, vs-read, or vs-write mode.");
    private static string ReadCheckpoint(string checkpoint) => Checkpoints.Contains(checkpoint, StringComparer.Ordinal)
        ? checkpoint : throw new ArgumentException("Unknown public crash checkpoint.");
    private static void RequireConfirmed(PublicationOutcome outcome) =>
        Require(outcome == PublicationOutcome.Confirmed, "A successful public publication did not report Confirmed.");
    private static void RequireEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string label) =>
        Require(expected.SequenceEqual(actual), $"Mismatch in {label}.");
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
    private static void Emit<T>(T value) {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }

    private sealed record SeedManifest(string DataStoreId, string VersionStoreId, string BaselinePayload, ulong BaselineTicket);
    private sealed record CheckpointManifest(string Checkpoint, string ExpectedPayload);
}
