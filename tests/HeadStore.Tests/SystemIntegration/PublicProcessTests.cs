using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.SystemIntegration;

[CollectionDefinition("HeadStore processes", DisableParallelization = true)]
public sealed class HeadStoreProcessCollection;

[Collection("HeadStore processes")]
public sealed class PublicProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-headstore-process", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublicGraphWritesAndReadsInSeparateColdProcesses() {
        var write = await Run("graph-write", _root);
        Assert.Equal("ok", write.GetProperty("status").GetString());
        var before = CaptureFiles();
        for (int i = 0; i < 2; i++) {
            var read = await Run("graph-read", _root);
            Assert.Equal("ok", read.GetProperty("status").GetString());
        }
        Assert.Equal(before, CaptureFiles());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    public async Task ResourceQueriesRemainReadOnlyAcrossColdProcesses(int size) {
        var write = await Run("resource-write", _root, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("ok", write.GetProperty("status").GetString());
        Assert.Equal(size, write.GetProperty("size").GetInt32());
        var before = CaptureFiles();
        for (int i = 0; i < 2; i++) {
            var read = await Run("resource-read", _root);
            Assert.Equal("ok", read.GetProperty("status").GetString());
            Assert.Equal(size, read.GetProperty("size").GetInt32());
            Assert.NotEmpty(read.GetProperty("measurements").EnumerateArray());
            // The child validates the graph/counts and resource ownership. The parent independently
            // proves a fresh process consumed the files without altering their names or bytes.
            Assert.Equal(before, CaptureFiles());
        }
    }

    [Theory]
    [InlineData("read", "read", false)]
    [InlineData("read", "write", true)]
    [InlineData("write", "read", true)]
    [InlineData("write", "write", true)]
    [InlineData("hs-read", "hs-read", false)]
    [InlineData("hs-read", "hs-write", true)]
    [InlineData("hs-write", "hs-read", true)]
    [InlineData("hs-write", "hs-write", true)]
    public async Task OwnerMatrixRejectsAtTheIntendedLayerAndReleases(string heldMode, string probeMode, bool blocked) {
        await Run("seed", _root);
        await using var holder = new SmokeProcess(output, "hold", _root, heldMode);
        var ready = await holder.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(holder.Id, ready.GetProperty("pid").GetInt32());
        var probe = await Run("probe", _root, probeMode);
        Assert.Equal(blocked ? "rejected" : "opened", probe.GetProperty("kind").GetString());
        if (blocked) {
            bool isolated = heldMode.StartsWith("hs-", StringComparison.Ordinal);
            Assert.Equal(isolated ? "heads" : "data",
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
    [InlineData("hs-read")]
    [InlineData("hs-write")]
    public async Task OperatingSystemReleasesOwnerLockAfterActualKill(string mode) {
        await Run("seed", _root);
        await using var holder = new SmokeProcess(output, "hold", _root, mode);
        var ready = await holder.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(holder.Id, ready.GetProperty("pid").GetInt32());
        await holder.Kill();
        Assert.Equal("opened", (await Run("probe", _root, mode.StartsWith("hs-", StringComparison.Ordinal) ? "hs-write" : "write"))
            .GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("unpublished-data")]
    [InlineData("confirmed-a-active-b")]
    [InlineData("head-confirmed")]
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

    private string[] CaptureFiles() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Directory.Exists(path) ? $"D:{Path.GetRelativePath(_root, path)}" :
            $"F:{Path.GetRelativePath(_root, path)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        // This GUID subtree was allocated by this fixture, never supplied by the caller.
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
