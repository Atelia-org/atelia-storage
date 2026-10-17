using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.SystemIntegration;

[Collection("HeadStore processes")]
public sealed class PersistentHandleProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-headstore-persistent-handles", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EncodedSelectedHeadAndExactEntrySurviveWriterExitAndResumeInFreshProcess() {
        JsonElement write = await Run("handle-write"); // Finish and disposal precede creating the reader process.
        Assert.Equal(4, write.GetProperty("headBytes").GetInt32());
        Assert.Equal(12, write.GetProperty("entryBytes").GetInt32());
        Assert.True(write.GetProperty("encodedAfterDispose").GetBoolean());
        byte[] selected = File.ReadAllBytes(Path.Combine(_root, "selected-head.v1.bin"));
        byte[] bookmark = File.ReadAllBytes(Path.Combine(_root, "bookmarked-entry.v1.bin"));
        Assert.Equal(4, selected.Length);
        Assert.Equal(12, bookmark.Length);
        Assert.Equal(write.GetProperty("headHex").GetString(), Convert.ToHexString(selected));
        Assert.Equal(write.GetProperty("entryHex").GetString(), Convert.ToHexString(bookmark));
        Assert.False(selected.SequenceEqual(bookmark.AsSpan(0, 4).ToArray()));

        JsonElement read = await Run("handle-read");
        Assert.Equal(write.GetProperty("headHex").GetString(), read.GetProperty("headHex").GetString());
        Assert.Equal(write.GetProperty("entryHex").GetString(), read.GetProperty("entryHex").GetString());
        foreach (string evidence in new[] { "decodedBeforeOpen", "selectedDiffersFromEntryOwner", "samePayloadDistinctEntries",
            "sourceUnchanged", "oldEntryStable" }) {
            Assert.True(read.GetProperty(evidence).GetBoolean(), evidence);
        }
        Assert.Equal(5, read.GetProperty("pages").GetInt32());
        Assert.Equal(1, read.GetProperty("emptyPages").GetInt32());
        Assert.Equal(4, read.GetProperty("exactHistoryEntries").GetInt32());
        Assert.Equal(4, read.GetProperty("headCount").GetInt32());
        Assert.Equal(7, read.GetProperty("entryCount").GetInt32());
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(_root, "selected-head.v1.bin")));
        Assert.Equal(bookmark, File.ReadAllBytes(Path.Combine(_root, "bookmarked-entry.v1.bin")));
    }

    private async Task<JsonElement> Run(string command) {
        await using var child = new SmokeProcess(output, command, _root);
        JsonElement result = await child.Receive(); // SmokeProcess verifies the actual child PID.
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal(command, result.GetProperty("scenario").GetString());
        await child.Finish(); // Requires exit 0, empty stderr and no extra messages.
        return result;
    }

    public void Dispose() {
        // Only this fixture's fresh GUID subtree is removed.
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
