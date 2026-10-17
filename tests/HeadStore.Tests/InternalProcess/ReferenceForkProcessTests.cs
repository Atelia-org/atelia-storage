using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.InternalProcess;

[Collection("HeadStore processes")]
[Trait("Qualification", "Internal+Process")]
public sealed class ReferenceForkProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-reference-fork-process", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("first-end", 1, 0)]
    [InlineData("all-ends", 3, 0)]
    [InlineData("flush-1", 3, 1)]
    [InlineData("flush-2", 3, 2)]
    [InlineData("flush-3", 3, 3)]
    [InlineData("confirmed", 3, 3)]
    public async Task ParentAndHeaderOnlyForks_FirstCuKillRetainsOnlyAcceptedEntries(string scenario, int ends, int flushes) {
        await InternalProcessChild.RunAsync(output, _root, "reference-fork.seed");
        await using (var child = await InternalProcessChild.StartParkedAsync(output, _root, "reference-fork.interrupt", scenario)) {
            var facts = child.Ready.GetProperty("facts");
            Assert.Equal(ends, facts.GetProperty("completeEnds").GetInt32());
            Assert.Equal(flushes, facts.GetProperty("durableFlushes").GetInt32());
            Assert.False(facts.GetProperty("resultDelivered").GetBoolean());
            Assert.Equal(scenario == "confirmed" ? "Confirmed" : "Unknown", facts.GetProperty("observedOutcome").GetString());
            await child.KillAsync();
        }
        string[] killed = CaptureFiles();
        var cold = (await InternalProcessChild.RunAsync(output, _root, "reference-fork.verify", scenario, "ro")).Json.GetProperty("facts");
        Assert.Equal(scenario == "first-end", cold.GetProperty("allOld").GetBoolean());
        Assert.True(cold.GetProperty("historyChecked").GetBoolean());
        Assert.True(cold.GetProperty("auditChecked").GetBoolean());
        Assert.Equal(killed, CaptureFiles());
        await InternalProcessChild.RunAsync(output, _root, "reference-fork.verify", scenario, "rw");
        Assert.Equal(killed, CaptureFiles());
        await InternalProcessChild.RunAsync(output, _root, "reference-fork.verify", scenario, "ro");
        Assert.Equal(killed, CaptureFiles());
        // Real process termination only; no power-loss or device-failure claim.
    }

    private string[] CaptureFiles() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(_root, path)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        string target = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "atelia-reference-fork-process")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Cleanup escaped the fixture parent."); }
        if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
    }
}
