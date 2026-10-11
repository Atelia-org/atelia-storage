using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.VersionStore.Tests.SystemIntegration;

[CollectionDefinition("VersionStore processes", DisableParallelization = true)]
public sealed class VersionStoreProcessCollection;

[Collection("VersionStore processes")]
public sealed class PublicProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-versionstore-process", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublicGraphWritesAndReadsInSeparateColdProcesses() {
        var write = await Run("graph-write", _root);
        Assert.Equal("ok", write.GetProperty("status").GetString());
        var before = Snapshot();
        for (int i = 0; i < 2; i++) {
            var read = await Run("graph-read", _root);
            Assert.Equal("ok", read.GetProperty("status").GetString());
        }
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    public async Task ResourceQueriesRemainReadOnlyAcrossColdProcesses(int size) {
        var write = await Run("resource-write", _root, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("ok", write.GetProperty("status").GetString());
        Assert.Equal(size, write.GetProperty("size").GetInt32());
        var before = Snapshot();
        for (int i = 0; i < 2; i++) {
            var read = await Run("resource-read", _root);
            Assert.Equal("ok", read.GetProperty("status").GetString());
            Assert.Equal(size, read.GetProperty("size").GetInt32());
            Assert.NotEmpty(read.GetProperty("measurements").EnumerateArray());
            // The child validates the graph/counts and resource ownership. The parent independently
            // proves a fresh process consumed the files without altering their names or bytes.
            Assert.Equal(before, Snapshot());
        }
    }

    [Theory]
    [InlineData("read", "read", false)]
    [InlineData("read", "write", true)]
    [InlineData("write", "read", true)]
    [InlineData("write", "write", true)]
    [InlineData("vs-read", "vs-read", false)]
    [InlineData("vs-read", "vs-write", true)]
    [InlineData("vs-write", "vs-read", true)]
    [InlineData("vs-write", "vs-write", true)]
    public async Task OwnerMatrixRejectsAtTheIntendedLayerAndReleases(string heldMode, string probeMode, bool blocked) {
        await Run("seed", _root);
        await using var holder = new SmokeProcess(output, "hold", _root, heldMode);
        var ready = await holder.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(holder.Id, ready.GetProperty("pid").GetInt32());
        var probe = await Run("probe", _root, probeMode);
        Assert.Equal(blocked ? "rejected" : "opened", probe.GetProperty("kind").GetString());
        if (blocked) {
            bool isolated = heldMode.StartsWith("vs-", StringComparison.Ordinal);
            Assert.Equal(isolated ? "versions" : "data",
                probe.GetProperty("stage").GetString());
            Assert.Equal(isolated, probe.GetProperty("dataOpened").GetBoolean());
            Assert.Equal(isolated ? "versionstore.lock" : "framestore.lock", probe.GetProperty("lockFile").GetString());
            Assert.Contains(probe.GetProperty("nativeCode").GetInt32(), OperatingSystem.IsWindows() ? new[] { 32, 33 } : new[] { 11 });
        }
        await holder.Release();
        Assert.Equal("opened", (await Run("probe", _root, probeMode)).GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("vs-read")]
    [InlineData("vs-write")]
    public async Task OperatingSystemReleasesOwnerLockAfterActualKill(string mode) {
        await Run("seed", _root);
        await using var holder = new SmokeProcess(output, "hold", _root, mode);
        var ready = await holder.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(holder.Id, ready.GetProperty("pid").GetInt32());
        await holder.Kill();
        Assert.Equal("opened", (await Run("probe", _root, mode.StartsWith("vs-", StringComparison.Ordinal) ? "vs-write" : "write"))
            .GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("unpublished-data")]
    [InlineData("confirmed-a-active-b")]
    [InlineData("ref-confirmed")]
    [InlineData("create-confirmed")]
    [InlineData("fork-confirmed")]
    [InlineData("tag-confirmed")]
    public async Task RealKillAtPublicBoundaryRetainsExactlyThePublishedState(string checkpoint) {
        await Run("seed", _root);
        await using var worker = new SmokeProcess(output, "crash-worker", _root, checkpoint);
        var ready = await worker.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(checkpoint, ready.GetProperty("checkpoint").GetString());
        Assert.Equal(worker.Id, ready.GetProperty("pid").GetInt32());
        Assert.Equal(checkpoint == "unpublished-data" ? "NotAttempted" : "Confirmed", ready.GetProperty("outcome").GetString());
        Assert.Equal(checkpoint == "confirmed-a-active-b", ready.GetProperty("activeBuilder").GetBoolean());
        await worker.Kill();
        Assert.Equal("completed", (await Run("verify", _root, checkpoint)).GetProperty("kind").GetString());
        Assert.Equal("completed", (await Run("verify", _root, checkpoint)).GetProperty("kind").GetString());
    }

    private async Task<JsonElement> Run(params string[] args) {
        await using var child = new SmokeProcess(output, args);
        var result = await child.Receive();
        await child.Finish();
        return result;
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Directory.Exists(path) ? $"D:{Path.GetRelativePath(_root, path)}" :
            $"F:{Path.GetRelativePath(_root, path)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        // This GUID subtree was allocated by this fixture, never supplied by the caller.
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
