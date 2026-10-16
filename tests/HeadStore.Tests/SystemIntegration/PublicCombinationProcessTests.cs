using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.SystemIntegration;

[Collection("HeadStore processes")]
public sealed class PublicCombinationProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-headstore-public-combination", Guid.NewGuid().ToString("N"));
    private static readonly string[] GoldenPaths = ["00000106.rbf", "00000174.rbf", "000001aa.rbf", "00000265.rbf", "00000351.rbf"];
    private static readonly string[] GoldenCodeUnits = ["0041", "00520065006C0065006100730065", "00720065006C0065006100730065",
        "D800", "007400610067002D00340038", "007400610067002D00360035"];

    [Fact]
    public async Task ConfirmedDynamicHeadRemainsDiscoverableAfterKillBeforeIndependentDirectoryRegistration() {
        await using var worker = new SmokeProcess(output, "dynamic-head-worker", _root);
        JsonElement ready = await worker.Receive();
        Assert.Equal("ready", ready.GetProperty("kind").GetString());
        Assert.Equal(worker.Id, ready.GetProperty("pid").GetInt32());
        Assert.Equal("created-before-directory-registration", ready.GetProperty("checkpoint").GetString());
        Assert.Equal("Confirmed", ready.GetProperty("createdOutcome").GetString());
        Assert.Equal("Confirmed", ready.GetProperty("directoryOutcome").GetString());
        Assert.False(ready.GetProperty("directoryPublicationAfterCreation").GetBoolean());
        Assert.True(ready.GetProperty("dataOpened").GetBoolean());
        Assert.True(ready.GetProperty("headsOpened").GetBoolean());
        Assert.Equal("after-create-confirmed-before-directory-publish", ready.GetProperty("publicBoundary").GetString());
        AssertDiscovery(ready);
        await worker.Kill();

        string[] before = CaptureFiles();
        for (int pass = 0; pass < 2; pass++) {
            JsonElement read = await Run("dynamic-head-read", _root);
            Assert.Equal("completed", read.GetProperty("kind").GetString());
            AssertDiscovery(read);
            Assert.Equal(ready.GetProperty("DirectoryTicket").GetUInt64(), read.GetProperty("DirectoryTicket").GetUInt64());
            Assert.Equal(ready.GetProperty("CreatedTicket").GetUInt64(), read.GetProperty("CreatedTicket").GetUInt64());
            Assert.True(read.GetProperty("directoryOldValue").GetBoolean());
            Assert.True(read.GetProperty("businessIdentityChecked").GetBoolean());
            Assert.Equal(3, read.GetProperty("HeadCount").GetInt32());
            Assert.Equal(3, read.GetProperty("EntryCount").GetInt32());
            Assert.Equal(before, CaptureFiles());
        }
    }

    [Fact]
    public async Task IndependentTagNameAndBucketGoldensSurviveWriterExitAndFreshReadProcesses() {
        JsonElement write = await Run("tag-golden-write", _root);
        AssertGoldens(write, physical: false);
        Assert.Equal(GoldenPaths, ActualBucketPaths());
        string[] before = CaptureFiles();
        for (int pass = 0; pass < 2; pass++) {
            JsonElement read = await Run("tag-golden-read", _root);
            AssertGoldens(read, physical: true);
            Assert.Equal(write.GetProperty("payloadHex").GetRawText(), read.GetProperty("payloadHex").GetRawText());
            Assert.Equal(write.GetProperty("metadata").GetRawText(), read.GetProperty("metadata").GetRawText());
            Assert.Equal(write.GetProperty("sourceTicket").GetUInt64(), read.GetProperty("sourceTicket").GetUInt64());
            Assert.Equal(GoldenPaths, ActualBucketPaths());
            Assert.Equal(before, CaptureFiles());
        }
    }

    private static void AssertDiscovery(JsonElement result) {
        Assert.Equal(1u, result.GetProperty("DirectoryHeadId").GetUInt32());
        Assert.Equal(3u, result.GetProperty("CreatedHeadId").GetUInt32());
        Assert.Equal(new uint[] { 2 }, result.GetProperty("RegisteredHeadIds").EnumerateArray().Select(value => value.GetUInt32()));
        Assert.Equal(new uint[] { 3 }, result.GetProperty("UnregisteredHeadIds").EnumerateArray().Select(value => value.GetUInt32()));
        Assert.Equal("business-object/created-before-registration", result.GetProperty("UnregisteredObjectId").GetString());
    }

    private static void AssertGoldens(JsonElement result, bool physical) {
        Assert.Equal(6, result.GetProperty("tagCount").GetInt32());
        Assert.Equal(5, result.GetProperty("bucketCount").GetInt32());
        Assert.Equal(GoldenPaths, result.GetProperty("bucketPaths").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(GoldenCodeUnits, result.GetProperty("originalCodeUnits").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.True(result.GetProperty("ordinalNames").GetBoolean());
        Assert.True(result.GetProperty("collisionNamesDistinct").GetBoolean());
        Assert.True(result.GetProperty("scanComplete").GetBoolean());
        Assert.Equal(physical, result.GetProperty("physicalFormatChecked").GetBoolean());
        JsonElement[] payloads = result.GetProperty("payloadHex").EnumerateArray().ToArray();
        Assert.NotEqual(payloads[1].GetString(), payloads[2].GetString());
        Assert.NotEqual(payloads[4].GetString(), payloads[5].GetString());
        JsonElement[] metadata = result.GetProperty("metadata").EnumerateArray().ToArray();
        Assert.Equal(6, metadata.Length);
        for (int index = 0; index < metadata.Length; index++) {
            Assert.Equal(GoldenCodeUnits[index], metadata[index].GetProperty("NameCodeUnits").GetString());
            Assert.Equal(1, metadata[index].GetProperty("EntryCount").GetInt32());
            Assert.Equal(1u, metadata[index].GetProperty("LocalHeadId").GetUInt32());
            Assert.NotEqual(result.GetProperty("sourceTicket").GetUInt64(), metadata[index].GetProperty("Ticket").GetUInt64());
        }
        Assert.Equal(6, metadata.Select(entry => entry.GetProperty("Ticket").GetUInt64()).Distinct().Count());
    }

    private async Task<JsonElement> Run(params string[] args) {
        await using var child = new SmokeProcess(output, args);
        JsonElement result = await child.Receive();
        Assert.Equal("ok", result.GetProperty("status").GetString());
        await child.Finish();
        return result;
    }

    private string[] ActualBucketPaths() => Directory.EnumerateFileSystemEntries(Path.Combine(_root, "heads", "tags"))
        .Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray();

    private string[] CaptureFiles() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Directory.Exists(path) ? $"D:{Path.GetRelativePath(_root, path)}" :
            $"F:{Path.GetRelativePath(_root, path)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        // This GUID subtree is allocated by this fixture and is never caller supplied.
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
