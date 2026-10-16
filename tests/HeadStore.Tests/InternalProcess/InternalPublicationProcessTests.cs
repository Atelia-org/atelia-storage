using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.InternalProcess;

[Collection("HeadStore processes")]
[Trait("Qualification", "Internal+Process")]
public sealed class InternalPublicationProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-headstore-internal-process", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("create-auto-private")]
    [InlineData("create-auto-renamed")]
    [InlineData("fork-specified-private")]
    [InlineData("fork-specified-renamed")]
    [InlineData("tag-private")]
    [InlineData("tag-empty")]
    [InlineData("ref-append")]
    [InlineData("tag-append")]
    public async Task ActualKillInsidePublicationHasOnlyTheColdFactsOfItsNamedWindow(string scenario) {
        await InternalProcessChild.RunAsync(output, _root, "seed");
        var baseline = CaptureFiles();
        await using (var child = await InternalProcessChild.StartParkedAsync(output, _root, "interrupt", scenario)) {
            var facts = child.Ready.GetProperty("facts");
            Assert.Equal(scenario, facts.GetProperty("scenario").GetString());
            if (scenario.EndsWith("-private", StringComparison.Ordinal)) {
                Assert.Equal(1, facts.GetProperty("pendingFlushes").GetInt32());
                Assert.True(facts.GetProperty("pendingClosed").GetBoolean());
                Assert.False(facts.GetProperty("renameReturned").GetBoolean());
            }
            else if (scenario.EndsWith("-renamed", StringComparison.Ordinal)) {
                Assert.True(facts.GetProperty("renameReturned").GetBoolean());
                Assert.False(facts.GetProperty("resultDelivered").GetBoolean());
            }
            else if (scenario == "tag-empty") {
                Assert.True(facts.GetProperty("renameReturned").GetBoolean());
                Assert.False(facts.GetProperty("firstAppendStarted").GetBoolean());
            }
            else {
                Assert.True(facts.GetProperty("appendReturned").GetBoolean());
                Assert.Equal(0, facts.GetProperty("targetFlushes").GetInt32());
                Assert.True(facts.GetProperty("physicalLength").GetInt64() > 0);
            }
            await child.KillAsync();
        }

        // Every verification command starts a new process, including both owner layers.
        // There is no outcome returned from the killed mutation and no power-loss inference.
        var killed = CaptureFiles();
        await InternalProcessChild.RunAsync(output, _root, "verify", scenario, "ro-before");
        Assert.Equal(killed, CaptureFiles());
        await InternalProcessChild.RunAsync(output, _root, "verify", scenario, "rw");
        var recovered = CaptureFiles();
        if (scenario.EndsWith("-private", StringComparison.Ordinal)) {
            Assert.Equal(baseline, recovered);
            Assert.DoesNotContain(recovered, row => row.StartsWith("heads/creating/pending.rbf|", StringComparison.Ordinal));
        }
        else { Assert.Equal(killed, recovered); }
        await InternalProcessChild.RunAsync(output, _root, "verify", scenario, "ro-after");
        Assert.Equal(recovered, CaptureFiles());

        if (scenario.EndsWith("-private", StringComparison.Ordinal) || scenario == "tag-empty") {
            await InternalProcessChild.RunAsync(output, _root, "retry", scenario);
        }
    }

    [Fact]
    public async Task CreateEntryRejectsTargetInsertedBeforeRenameWithoutOverwritingOrAdoptingIt() {
        await InternalProcessChild.RunAsync(output, _root, "seed");
        var conflict = (await InternalProcessChild.RunAsync(output, _root, "conflict")).Json.GetProperty("facts");
        Assert.Equal("Unknown", conflict.GetProperty("outcome").GetString());
        Assert.True(conflict.GetProperty("targetUnchanged").GetBoolean());
        Assert.Equal("platform-target-preflight", conflict.GetProperty("rejectionStage").GetString());
        Assert.False(conflict.GetProperty("nativeRenameAttempted").GetBoolean());
        Assert.True(conflict.GetProperty("ownerFaulted").GetBoolean());
        byte[] target = File.ReadAllBytes(InternalProcessHost.HeadPath(_root, InternalProcessHost.AutomaticId));
        var before = CaptureFiles();
        await InternalProcessChild.RunAsync(output, _root, "verify-conflict");
        Assert.Equal(before, CaptureFiles());
        await InternalProcessChild.RunAsync(output, _root, "recover-conflict");
        Assert.Equal(target, File.ReadAllBytes(InternalProcessHost.HeadPath(_root, InternalProcessHost.AutomaticId)));
        Assert.False(File.Exists(InternalProcessHost.PendingPath(_root)));
        var after = CaptureFiles();
        await InternalProcessChild.RunAsync(output, _root, "verify-conflict-recovered");
        Assert.Equal(after, CaptureFiles());
    }

    private string[] CaptureFiles() => Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(_root, path).Replace('\\', '/')}|directory")
        .Concat(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(_root, path).Replace('\\', '/')}|{new FileInfo(path).Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        )
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        string target = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "atelia-headstore-internal-process")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Test cleanup escaped its fixture parent."); }
        if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
    }
}
