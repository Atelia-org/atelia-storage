using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;
using Store = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.EventJournal.Toolkit.Tests;

public sealed class LegacyCrossVersionTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-cross-version-" + Guid.NewGuid().ToString("N"));
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "LegacyFixtures");

    [Fact]
    public void FixtureProvenance_IsTheUnmodifiedOldBinaryAndHasRealArchiveTimeDifference() {
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "provenance.json")));
        var p = provenance.RootElement;
        Assert.Equal("bb7c4fb3eb6477783c70ee61bc62b832be195d07", Text(p, "sourceRevision"));
        Assert.Equal("10.0.201", Text(p, "sdk"));
        Assert.True(p.GetProperty("oldProductionSourceUnchanged").GetBoolean());
        foreach (var fixture in p.GetProperty("fixtures").EnumerateObject()) {
            foreach (var file in fixture.Value.EnumerateArray()) {
                string path = Path.Combine(Fixtures, fixture.Name, Text(file, "relativePath"));
                Assert.Equal(file.GetProperty("length").GetInt64(), new FileInfo(path).Length);
                Assert.Equal(Text(file, "sha256"), Hash(path));
            }
            Assert.Contains("journal/refs/objects", p.GetProperty("fixtureDirectories").GetProperty(fixture.Name).EnumerateArray().Select(e => e.GetString()));
        }
        using var expected = Expected("complex");
        Assert.Contains(expected.RootElement.GetProperty("archiveEvidence").EnumerateArray(), e => e.GetProperty("deltaMilliseconds").GetInt64() != 0);
        Assert.True(expected.RootElement.GetProperty("casFailureProducedOrphan").GetBoolean());
        Assert.Contains(expected.RootElement.GetProperty("events").EnumerateArray(), e => Text(e, "payloadCodecId") == "Brotli");
        Assert.Contains(expected.RootElement.GetProperty("events").EnumerateArray(), e => Text(e, "payloadCodecId") == "Zlib");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("events-only")]
    [InlineData("complex")]
    [InlineData("empty-active")]
    public void RealOldBinary_UpgradesByteExactlyAndContinuesOnADiscardableCopy(string kind) {
        string source = MaterializeFixture(kind, "source");
        string bundle = Path.Combine(_root, "upgrade");
        var sourceBefore = Snapshot(source);
        UpgradeReport check = LegacyUpgrade.Check(source, LegacyUpgrade.Profile);
        Assert.Equal("Eligible", check.Status);
        Assert.Equal(0, check.ExitCode);
        Assert.True(check.SourceScanCompleted);
        AssertUnchanged(sourceBefore, source);
        UpgradeReport upgraded = LegacyUpgrade.Upgrade(source, LegacyUpgrade.Profile, bundle);
        Assert.Equal("Created", upgraded.Status);
        Assert.Equal(0, upgraded.ExitCode);
        string target = Path.Combine(bundle, "journal");
        AssertUnchanged(sourceBefore, source);
        Assert.False(Directory.Exists(Path.Combine(target, "cache")));
        var oldFacts = sourceBefore.Where(p => p.Key.EndsWith(".rbf", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
        var copiedFacts = Snapshot(target).Where(p => p.Key.EndsWith(".rbf", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(oldFacts.Keys.Order(StringComparer.Ordinal), copiedFacts.Keys.Order(StringComparer.Ordinal));
        foreach (var fact in oldFacts) { Assert.Equal(fact.Value, copiedFacts[fact.Key]); }
        AssertManifest(bundle);
        AssertExactOldSemantics(kind, target);
        AssertHealthy(target);
        var completedTarget = Snapshot(target);
        ContinueOnCopy(target, Path.Combine(_root, "continued"));
        AssertUnchanged(completedTarget, target);
        AssertUnchanged(sourceBefore, source);
    }

    [Fact]
    public void UpgradedLegacy_PublicTagChurnExecutesCheckpointOnDiscardableTmpfsCopy() {
        string source = MaterializeFixture("complex", "source");
        string bundle = Path.Combine(_root, "upgrade");
        Assert.Equal("Created", LegacyUpgrade.Upgrade(source, LegacyUpgrade.Profile, bundle).Status);
        string target = Path.Combine(bundle, "journal");
        var before = Snapshot(target);
        string temporaryBase = Path.GetTempPath();
        if (OperatingSystem.IsLinux() && File.ReadAllLines("/proc/mounts").Any(l => l.Split(' ') is var fields && fields.Length > 2 && fields[1] == "/dev/shm" && fields[2] == "tmpfs")) {
            temporaryBase = "/dev/shm";
        }
        string copy = Path.Combine(temporaryBase, "legacy-public-checkpoint-" + Guid.NewGuid().ToString("N"));
        try {
            CopyTree(target, copy);
            string snapshot = Path.Combine(copy, "refs", "catalog.snapshot");
            string initial = Hash(snapshot);
            using (var journal = Journal.OpenExisting(copy)) {
                EventAddress tagged = journal.ResolveTag("saved").Unwrap();
                for (int i = 0; i < 1025; i++) { journal.CreateTag($"checkpoint-{i:D4}", tagged).Unwrap(); }
            }
            Assert.NotEqual(initial, Hash(snapshot));
            using (var journal = Journal.OpenReadOnlyExisting(copy)) {
                EventAddress tagged = journal.ResolveTag("saved").Unwrap();
                Assert.Equal(tagged, journal.ResolveTag("checkpoint-1024").Unwrap());
                Assert.Equal(tagged, journal.ResolveTag("checkpoint-0000").Unwrap());
            }
            AssertHealthy(copy);
            AssertUnchanged(before, target);
        }
        finally { if (Directory.Exists(copy)) { Directory.Delete(copy, recursive: true); } }
    }

    private string MaterializeFixture(string kind, string name) {
        string output = Path.Combine(_root, name);
        Directory.CreateDirectory(output);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "provenance.json")));
        string stage = Path.Combine(_root, "fixture-stage");
        Directory.CreateDirectory(stage);
        foreach (var directory in provenance.RootElement.GetProperty("fixtureDirectories").GetProperty(kind).EnumerateArray()) {
            string relative = directory.GetString()!;
            Assert.False(Path.IsPathRooted(relative));
            Assert.DoesNotContain("..", relative.Split('/'));
            Directory.CreateDirectory(Path.Combine(stage, relative));
        }
        CopyTree(Path.Combine(Fixtures, kind, "journal"), Path.Combine(stage, "journal"));
        CopyTree(Path.Combine(stage, "journal"), output);
        return output;
    }

    private static void AssertExactOldSemantics(string kind, string target) {
        using var expected = Expected(kind);
        var e = expected.RootElement;
        using var journal = Journal.OpenReadOnlyExisting(target);
        Assert.Equal(e.GetProperty("branches").EnumerateArray().Select(b => Text(b, "name")).Order(StringComparer.Ordinal), journal.ListBranches().Order(StringComparer.Ordinal));
        foreach (var branch in e.GetProperty("branches").EnumerateArray()) {
            RefId id = Ref(Text(branch, "refId"));
            Assert.Equal(id, journal.OpenBranch(Text(branch, "name")).Unwrap());
            Assert.Equal(Address(branch.GetProperty("head")), journal.GetHead(id));
        }
        foreach (var tag in e.GetProperty("tags").EnumerateArray()) {
            Assert.Equal(Address(tag.GetProperty("target")), journal.ResolveTag(Text(tag, "name")).Unwrap());
        }
        foreach (var reference in e.GetProperty("refs").EnumerateArray()) {
            RefId id = Ref(Text(reference, "refId"));
            var moves = journal.ReadReflog(id).Unwrap();
            var oldMoves = reference.GetProperty("moves").EnumerateArray().ToArray();
            Assert.Equal(oldMoves.Length, moves.Count);
            for (int i = 0; i < moves.Count; i++) {
                var old = oldMoves[i]; var move = moves[i];
                Assert.Equal(id, move.RefId);
                Assert.Equal(old.GetProperty("MoveSequenceNumber").GetUInt64(), move.MoveSequenceNumber);
                Assert.Equal(old.GetProperty("UtcUnixTimeMilliseconds").GetInt64(), move.UtcUnixTimeMilliseconds);
                Assert.Equal(Text(old, "operation"), move.Operation.ToString());
                Assert.Equal(Address(old.GetProperty("expectedOldTarget")), move.ExpectedOldTarget);
                Assert.Equal(Address(old.GetProperty("oldTarget")), move.OldTarget);
                Assert.Equal(Address(old.GetProperty("newTarget")), move.NewTarget);
                Assert.Equal(old.GetProperty("ReasonKind").GetUInt32(), move.ReasonKind);
            }
            if (reference.GetProperty("closed").GetBoolean()) { Assert.Throws<InvalidOperationException>(() => journal.GetHead(id)); }
        }
        foreach (var old in e.GetProperty("events").EnumerateArray()) {
            EventAddress address = Address(old.GetProperty("address"))!.Value;
            using var frame = journal.ReadEvent(address).Unwrap();
            Assert.Equal(Address(old.GetProperty("parent")), frame.Header.Parent);
            Assert.Equal(old.GetProperty("SequenceNumber").GetUInt64(), frame.Header.SequenceNumber);
            Assert.Equal(old.GetProperty("UtcUnixTimeMilliseconds").GetInt64(), frame.Header.UtcUnixTimeMilliseconds);
            Assert.Equal(old.GetProperty("OpaqueEventKind").GetUInt32(), frame.Header.OpaqueEventKind);
            Assert.Equal(old.GetProperty("PayloadLength").GetUInt32(), frame.Header.PayloadLength);
            Assert.Equal(Text(old, "payloadCodecId"), frame.Header.PayloadCodecId.ToString());
            Assert.Equal(Text(old, "payloadSha256"), Convert.ToHexStringLower(SHA256.HashData(frame.Payload)));
        }
        using var store = Store.OpenReadOnlyExisting(Path.Combine(target, "events"));
        var eventFiles = e.GetProperty("stores").EnumerateArray().Single(s => Text(s, "directory").StartsWith("events/", StringComparison.Ordinal)).GetProperty("files").EnumerateArray().ToArray();
        uint highest = eventFiles.Select(f => Convert.ToUInt32(Path.GetFileNameWithoutExtension(Text(f, "name")), 16)).Max();
        Assert.Equal(highest, store.ActiveSegmentNumber);
        using var reader = store.OpenReader(highest);
        Assert.Equal(eventFiles.Single(f => Convert.ToUInt32(Path.GetFileNameWithoutExtension(Text(f, "name")), 16) == highest).GetProperty("length").GetInt64(), reader.File.TailOffset);
    }

    private static void ContinueOnCopy(string target, string copy) {
        CopyTree(target, copy);
        uint originalActive;
        ulong originalSequence = MaxEventSequence(copy);
        using (var store = Store.OpenReadOnlyExisting(Path.Combine(copy, "events"))) { originalActive = store.ActiveSegmentNumber; }
        var options = new EventJournalOptions {
            EventSegmentStoreOptions = new() { SegmentSizeThresholdBytes = 128 },
            RefSegmentStoreOptions = new() { SegmentSizeThresholdBytes = 128 },
            RefStoreCacheCapacity = 0
        };
        using (var journal = Journal.OpenExisting(copy, options)) {
            EventAddress? parent = null;
            foreach (var name in journal.ListBranches()) {
                if (journal.GetHead(journal.OpenBranch(name).Unwrap()) is { } head) { parent = head; break; }
            }
            RefId branch = journal.CreateBranch("continued-validation", parent).Unwrap();
            for (int i = 0; i < 4; i++) {
                EventAddress next = journal.CommitToRef(branch, parent, "continued"u8, hint: new AddressHint(0x0BADF00D)).Unwrap().EventAddress;
                EventFrameHeader header = journal.ReadEventHeaderChecked(next).Unwrap();
                Assert.Equal(parent, header.Parent);
                Assert.Equal(originalSequence + (ulong)i + 1, header.SequenceNumber);
                parent = next;
            }
            journal.CreateTag("continued-validation", parent!.Value).Unwrap();
            journal.MoveRef(branch, parent, parent).Unwrap();
            journal.ArchiveRef(branch, parent, reasonKind: 73).Unwrap();
            Assert.True(journal.OpenBranch("continued-validation").IsFailure);
        }
        using (var store = Store.OpenReadOnlyExisting(Path.Combine(copy, "events"))) { Assert.True(store.ActiveSegmentNumber > originalActive); }
        using (var journal = Journal.OpenReadOnlyExisting(copy)) { journal.ResolveTag("continued-validation").Unwrap(); }
        AssertHealthy(copy);
    }

    private static ulong MaxEventSequence(string root) {
        ulong maximum = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "events"), "*.rbf", SearchOption.AllDirectories)) {
            using var reader = RbfFile.OpenReadOnlyExisting(path);
            foreach (var info in reader.ScanForward()) {
                using var frame = reader.ReadPooledFrame(info.Ticket).ToDisposable();
                var value = frame.Unwrap();
                var header = EventFrameHeaderCodec.Decode(value.PayloadAndMeta[^value.TailMetaLength..]).Unwrap();
                maximum = Math.Max(maximum, header.SequenceNumber);
            }
        }
        return maximum;
    }

    private static void AssertManifest(string bundle) {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "manifest.json")));
        var m = manifest.RootElement;
        Assert.Equal("EventJournalLegacyUpgrade", Text(m, "kind"));
        Assert.True(m.GetProperty("completed").GetBoolean());
        Assert.True(m.GetProperty("factsCopiedByteExactly").GetBoolean());
        Assert.Equal(LegacyUpgrade.ProfileBaselineRevision, Text(m, "profileBaselineRevision"));
        foreach (var check in m.GetProperty("validation").EnumerateObject()) { Assert.Equal("Passed", check.Value.GetString()); }
        foreach (var file in m.GetProperty("outputs").EnumerateArray()) {
            string path = Path.Combine(bundle, "journal", Text(file, "relativePath"));
            Assert.Equal(file.GetProperty("length").GetInt64(), new FileInfo(path).Length);
            Assert.Equal(Text(file, "sha256"), Hash(path));
        }
    }
    private static void AssertHealthy(string root) {
        var report = JournalToolkit.Audit(root);
        Assert.True(report.Completed);
        Assert.Equal("Healthy", report.FactsStatus);
        Assert.Equal("Consistent", report.IndexesStatus);
    }
    private static JsonDocument Expected(string kind) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, kind, "expected.json")));
    private static EventAddress? Address(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : new EventAddress(
        SizedPtr.FromPacked(ulong.Parse(Text(value, "ticketPacked"), NumberStyles.HexNumber)), value.GetProperty("segmentNumber").GetUInt32(), new AddressHint(uint.Parse(Text(value, "hint"), NumberStyles.HexNumber)));
    private static RefId Ref(string value) => new(ulong.Parse(value, NumberStyles.HexNumber));
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), File.ReadAllBytes);
    private static void AssertUnchanged(Dictionary<string, byte[]> before, string root) {
        var after = Snapshot(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var file in before) { Assert.Equal(file.Value, after[file.Key]); }
    }
    private static void CopyTree(string source, string output) {
        Directory.CreateDirectory(output);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(Path.Combine(output, Path.GetRelativePath(source, directory))); }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            string target = Path.Combine(output, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
