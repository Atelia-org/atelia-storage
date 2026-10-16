using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.InternalProcess;

[Collection("HeadStore processes")]
[Trait("Qualification", "Internal+Process")]
public sealed class InternalTailProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-headstore-physical-tail", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ref-body-partial")]
    [InlineData("ref-body-complete")]
    [InlineData("tag-body-partial")]
    [InlineData("tag-body-complete")]
    public async Task RealWriterPrefixThenParentKill_RejectsReadOnlyAndRecoversThroughHeadStore(string scenario) {
        JsonElement seed = await Run("tail.seed", scenario);
        var seededBytes = CaptureFiles();
        JsonElement stop;
        await using (var child = new InternalProcessChild(output, "tail.interrupt", _root, scenario)) {
            JsonElement ready = await child.ReceiveReadyAsync();
            Assert.Equal("tail.interrupt", ready.GetProperty("command").GetString());
            stop = ready.GetProperty("facts").Clone();
            Assert.Equal(scenario, stop.GetProperty("scenario").GetString());
            Assert.True(stop.GetProperty("ownersAlive").GetBoolean());
            Assert.False(stop.GetProperty("appendReturned").GetBoolean());
            Assert.Equal(0, stop.GetProperty("targetFlushes").GetInt32());
            await child.KillAsync();
        }

        // Only the real writer may have altered files before this entry. The parent performs
        // no write/truncate operation on the image in any stage of this experiment.
        string path = stop.GetProperty("path").GetString()!;
        string relative = Path.GetRelativePath(_root, path);
        Assert.DoesNotContain("..", relative.Split(Path.DirectorySeparatorChar));
        var interrupted = CaptureFiles();
        byte[] tailBytes = interrupted[relative]!;
        long frameOffset = stop.GetProperty("frameOffset").GetInt64();
        int frameLength = stop.GetProperty("frameLength").GetInt32();
        long cutLength = stop.GetProperty("cutLength").GetInt64();
        long completeLength = stop.GetProperty("completeLength").GetInt64();
        Assert.Equal(cutLength, tailBytes.LongLength);
        Assert.Equal(cutLength, stop.GetProperty("actualLength").GetInt64());
        Assert.Equal(frameOffset + frameLength + sizeof(uint), completeLength);
        bool completedBody = scenario.EndsWith("body-complete", StringComparison.Ordinal);
        Assert.Equal(completedBody ? frameOffset + frameLength - sizeof(uint) : frameOffset + sizeof(uint) + 7, cutLength);

        JsonElement[] writes = stop.GetProperty("writes").EnumerateArray().ToArray();
        Assert.NotEmpty(writes);
        long nextOffset = frameOffset;
        foreach (JsonElement write in writes) {
            Assert.Equal(path, write.GetProperty("path").GetString());
            Assert.Equal(nextOffset, write.GetProperty("offset").GetInt64());
            int count = write.GetProperty("writtenBytes").GetInt32();
            Assert.InRange(count, 1, write.GetProperty("requestedBytes").GetInt32());
            nextOffset += count;
        }
        Assert.Equal(cutLength, nextOffset);
        Assert.True(writes[^1].GetProperty("writtenBytes").GetInt32() < writes[^1].GetProperty("requestedBytes").GetInt32());
        bool tag = scenario.StartsWith("tag-", StringComparison.Ordinal);
        if (!tag) {
            Assert.Equal(seed.GetProperty("headLength").GetInt64(), frameOffset);
            Assert.Equal(seededBytes[relative], tailBytes.AsSpan(0, checked((int)frameOffset)).ToArray());
        }
        else {
            Assert.False(seededBytes.ContainsKey(relative));
            Assert.True(frameOffset > sizeof(uint), "The real published BucketHeader must precede the first tag record.");
            Assert.False(interrupted.ContainsKey(Path.Combine("heads", "creating", "pending.rbf")));
        }

        JsonElement rejection = await Run("tail.readonly-reject", scenario);
        Assert.True(rejection.GetProperty("rejected").GetBoolean());
        Assert.Equal("InvalidDataException", rejection.GetProperty("errorType").GetString());
        Assert.Equal(tag ? "tag-content-open" : "ref-content-open", rejection.GetProperty("stage").GetString());
        Assert.Equal(cutLength, rejection.GetProperty("physicalLength").GetInt64());
        AssertUnchanged(interrupted);

        JsonElement recovered = await Run("tail.recover", scenario);
        Assert.False(recovered.GetProperty("readOnly").GetBoolean());
        JsonElement[] reports = recovered.GetProperty("reports").EnumerateArray().ToArray();
        Assert.All(reports, report => Assert.True(report.GetProperty("reportAvailable").GetBoolean()));
        JsonElement recovery = Assert.Single(reports, report => report.GetProperty("action").GetString() != "None");
        Assert.Equal(path, recovery.GetProperty("path").GetString());
        Assert.Equal(completedBody ? "CompletedTail" : "Truncated", recovery.GetProperty("action").GetString());
        Assert.Equal(cutLength, recovery.GetProperty("originalLength").GetInt64());
        Assert.Equal(frameOffset, recovery.GetProperty("affectedFrameOffset").GetInt64());
        long finalLength = completedBody ? completeLength : frameOffset;
        Assert.Equal(finalLength, recovery.GetProperty("finalLength").GetInt64());
        Assert.Equal(finalLength, recovered.GetProperty("physicalLength").GetInt64());
        if (completedBody) {
            Assert.Equal(frameOffset, recovery.GetProperty("ticketOffset").GetInt64());
            Assert.Equal(frameLength, recovery.GetProperty("ticketLength").GetInt32());
        }
        else {
            Assert.Equal(JsonValueKind.Null, recovery.GetProperty("ticketOffset").ValueKind);
            Assert.Equal(JsonValueKind.Null, recovery.GetProperty("ticketLength").ValueKind);
        }
        Assert.All(recovered.GetProperty("dataRecoveryActions").EnumerateArray(), action => Assert.Equal("None", action.GetString()));
        AssertRootFacts(recovered, stop, tag, completedBody);
        var repairedBytes = CaptureFiles();
        Assert.Equal(interrupted.Keys.Order(StringComparer.Ordinal), repairedBytes.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in interrupted) {
            if (name != relative) { Assert.Equal(bytes, repairedBytes[name]); }
        }
        byte[] repaired = repairedBytes[relative]!;
        Assert.Equal(finalLength, repaired.LongLength);
        Assert.Equal(tailBytes.AsSpan(0, checked((int)Math.Min(cutLength, finalLength))).ToArray(),
            repaired.AsSpan(0, checked((int)Math.Min(cutLength, finalLength))).ToArray());

        for (int pass = 0; pass < 2; pass++) {
            JsonElement verified = await Run("tail.verify", scenario);
            Assert.True(verified.GetProperty("readOnly").GetBoolean());
            AssertRootFacts(verified, stop, tag, completedBody);
            Assert.Equal(finalLength, verified.GetProperty("physicalLength").GetInt64());
            Assert.All(verified.GetProperty("reports").EnumerateArray(), report => {
                Assert.False(report.GetProperty("reportAvailable").GetBoolean());
                Assert.Equal(JsonValueKind.Null, report.GetProperty("action").ValueKind);
            });
            JsonElement audit = verified.GetProperty("audit");
            Assert.Equal(1, audit.GetProperty("headCount").GetInt64());
            Assert.Equal(!tag && completedBody ? 2 : 1, audit.GetProperty("entryCount").GetInt64());
            Assert.Equal(0, audit.GetProperty("originCount").GetInt64());
            Assert.Equal(tag ? 1 : 0, audit.GetProperty("tagBucketCount").GetInt64());
            Assert.Equal(tag && completedBody ? 1 : 0, audit.GetProperty("tagCount").GetInt64());
            AssertUnchanged(repairedBytes);
        }
    }

    private static void AssertRootFacts(JsonElement facts, JsonElement stop, bool tag, bool completedBody) {
        Assert.Equal(Convert.ToHexString(!tag && completedBody ? InternalTailHost.NewPayload() : InternalTailHost.OldPayload()),
            facts.GetProperty("headPayload").GetString());
        Assert.Equal(!tag && completedBody ? stop.GetProperty("frameOffset").GetInt64() : stop.GetProperty("oldTicketOffset").GetInt64(),
            facts.GetProperty("headTicketOffset").GetInt64());
        Assert.Equal(!tag && completedBody ? stop.GetProperty("frameLength").GetInt32() : stop.GetProperty("oldTicketLength").GetInt32(),
            facts.GetProperty("headTicketLength").GetInt32());
        Assert.Equal(tag && completedBody, facts.GetProperty("tagFound").GetBoolean());
        Assert.Equal(tag && completedBody ? 1 : 0, facts.GetProperty("tagNames").GetArrayLength());
        if (tag && completedBody) {
            Assert.True(facts.GetProperty("metadataRechecked").GetBoolean());
            JsonElement info = facts.GetProperty("tagInfo");
            Assert.Equal(InternalTailHost.TagName, info.GetProperty("name").GetString());
            Assert.Equal(InternalTailHost.RecordedAt, info.GetProperty("recordedAtUnixMs").GetInt64());
            Assert.Equal(1, info.GetProperty("entryCount").GetInt32());
        }
        else { Assert.Equal(JsonValueKind.Null, facts.GetProperty("tagInfo").ValueKind); }
    }

    private async Task<JsonElement> Run(string command, string scenario) {
        ProcessRun run = await InternalProcessChild.RunAsync(output, _root, command, scenario);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Stderr);
        JsonElement result = run.Json;
        Assert.Equal(command, result.GetProperty("command").GetString());
        return result.GetProperty("facts").Clone();
    }

    private Dictionary<string, byte[]?> CaptureFiles() {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(_root, directory) + Path.DirectorySeparatorChar, null);
        }
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(_root, file), File.ReadAllBytes(file));
        }
        return result;
    }

    private void AssertUnchanged(Dictionary<string, byte[]?> before) {
        var after = CaptureFiles();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); }
    }

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
