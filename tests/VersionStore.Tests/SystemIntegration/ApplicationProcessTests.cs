using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.VersionStore.Tests.SystemIntegration;

[Collection("VersionStore processes")]
public sealed class ApplicationProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-versionstore-application", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("started", "uncertain", 0, 0, 2)]
    [InlineData("external-completed", "result", 1, 1, 3)]
    [InlineData("result-published", "result", 1, 1, 3)]
    public async Task ToolRecoveryUsesApplicationPhaseWithoutBlindRedispatch(
        string checkpoint, string phase, int dispatches, int settlements, int snapshots) {
        await StartAndKill(checkpoint);
        var before = Snapshot();
        var cold = await Run("tool-read", _root);
        Assert.Equal(checkpoint == "result-published" ? "result" : "started", cold.GetProperty("storedPhase").GetString());
        Assert.Equal(checkpoint == "result-published" ? 3 : 2, cold.GetProperty("snapshotCount").GetInt32());
        Assert.Equal(before, Snapshot());

        var recovered = await Run("tool-recover", _root);
        AssertToolState(recovered, phase, dispatches, settlements, snapshots);
        Assert.Equal(checkpoint == "result-published" ? 0 : 1, recovered.GetProperty("backendQueryCount").GetInt32());
        var afterRecovery = Snapshot();
        if (checkpoint != "external-completed") { Assert.Equal(before, afterRecovery); }

        var repeated = await Run("tool-recover", _root);
        AssertToolState(repeated, phase, dispatches, settlements, snapshots);
        Assert.Equal(phase == "result" ? 0 : 1, repeated.GetProperty("backendQueryCount").GetInt32());
        Assert.Equal(afterRecovery, Snapshot());
        var read = await Run("tool-read", _root);
        Assert.Equal(phase == "uncertain" ? "started" : "result", read.GetProperty("storedPhase").GetString());
        Assert.Equal(snapshots, read.GetProperty("snapshotCount").GetInt32());
        Assert.Equal(afterRecovery, Snapshot());
    }

    [Fact]
    public async Task PublishedResultDoesNotRequireTheExternalBackendOnRecovery() {
        await StartAndKill("result-published");
        File.Delete(Path.Combine(_root, "tool-backend-receipt.json"));
        var before = Snapshot();
        var recovered = await Run("tool-recover", _root);
        AssertToolState(recovered, "result", 1, 1, 3);
        Assert.Equal(0, recovered.GetProperty("backendQueryCount").GetInt32());
        var read = await Run("tool-read", _root);
        Assert.Equal("result", read.GetProperty("storedPhase").GetString());
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("AttemptId")]
    [InlineData("Request")]
    public async Task ForeignReceiptFailsBeforeAnyRecoveryPublication(string field) {
        await StartAndKill("external-completed");
        string receiptPath = Path.Combine(_root, "tool-backend-receipt.json");
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        Assert.True(receipt.ContainsKey(field));
        receipt[field] = field == "AttemptId" ? Guid.NewGuid().ToString() : "a different frozen request";
        File.WriteAllText(receiptPath, receipt.ToJsonString());
        var before = Snapshot();
        await using (var child = new SmokeProcess(output, "tool-recover", _root)) {
            string error = await child.ExpectFailure();
            Assert.Contains(nameof(InvalidDataException), error, StringComparison.Ordinal);
        }
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task SingleDriverMergesCurrentResultsAndRejectsStaleOrDuplicateReplies() {
        AssertGeneration(await Run("generation-write", _root));
        var before = Snapshot();
        for (int i = 0; i < 2; i++) {
            AssertGeneration(await Run("generation-read", _root));
            Assert.Equal(before, Snapshot());
        }
    }

    private async Task StartAndKill(string checkpoint) {
        await using var worker = new SmokeProcess(output, "tool-worker", _root, checkpoint);
        var ready = await worker.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(checkpoint, ready.GetProperty("checkpoint").GetString());
        Assert.Equal("Confirmed", ready.GetProperty("startedOutcome").GetString());
        Assert.Equal(checkpoint == "result-published" ? "Confirmed" : "NotAttempted", ready.GetProperty("resultOutcome").GetString());
        Assert.Equal(checkpoint == "started" ? 0 : 1, ready.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(checkpoint == "result-published" ? 1 : 0, ready.GetProperty("settlementCount").GetInt32());
        await worker.Kill();
    }

    private static void AssertToolState(JsonElement result, string phase, int dispatches, int settlements, int snapshots) {
        Assert.Equal(phase, result.GetProperty("phase").GetString());
        Assert.Equal(dispatches, result.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(settlements, result.GetProperty("settlementCount").GetInt32());
        Assert.Equal(snapshots, result.GetProperty("snapshotCount").GetInt32());
        Assert.Equal(snapshots, result.GetProperty("publicationCount").GetInt32());
    }

    private static void AssertGeneration(JsonElement result) {
        Assert.Equal(2, result.GetProperty("generation").GetInt32());
        Assert.Equal(3, result.GetProperty("accepted").GetInt32());
        Assert.Equal(6, result.GetProperty("rejected").GetInt32());
        Assert.Equal(4, result.GetProperty("dispatched").GetInt32());
        Assert.Equal(314, result.GetProperty("settled").GetInt32());
        Assert.Equal(101, result.GetProperty("slots").GetProperty("A").GetInt32());
        Assert.Equal(202, result.GetProperty("slots").GetProperty("B").GetInt32());
        foreach (string reason in new[] { "operationId", "generation", "duplicate" }) {
            Assert.Equal(2, result.GetProperty("rejections").GetProperty(reason).GetInt32());
        }
        Assert.Equal(5, result.GetProperty("counts").GetProperty("snapshots").GetInt32());
        Assert.Equal(20, result.GetProperty("counts").GetProperty("dataFrames").GetInt32());
    }

    private async Task<JsonElement> Run(params string[] args) {
        await using var child = new SmokeProcess(output, args);
        var result = await child.Receive();
        Assert.Equal("ok", result.GetProperty("status").GetString());
        await child.Finish();
        return result;
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Directory.Exists(path) ? $"D:{Path.GetRelativePath(_root, path)}" :
            $"F:{Path.GetRelativePath(_root, path)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        // This GUID subtree is allocated by this fixture and is never caller supplied.
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
