using System.Security.Cryptography;
using Atelia.VersionStore.Tests.InternalProcess;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.VersionStore.Tests.ConditionalUpdateFaults;

[Collection("VersionStore processes")]
[Trait("Qualification", "Internal+Process")]
public sealed class ConditionalUpdateProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-versionstore-cu-process", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("first-end", 1, 0)]
    [InlineData("all-ends", 3, 0)]
    [InlineData("flush-1", 3, 1)]
    [InlineData("flush-2", 3, 2)]
    [InlineData("flush-3", 3, 3)]
    [InlineData("confirmed", 3, 3)]
    public async Task RealKillAfterCompleteEndsNativeFlushesAndConfirmationHasConsistentColdCurrentHistoryForkAndAudit(
        string scenario, int completeEnds, int durableFlushes) {
        await InternalProcessChild.RunAsync(output, _root, "cu-seed");
        await using (var child = await InternalProcessChild.StartParkedAsync(output, _root, "cu-interrupt", scenario)) {
            var facts = child.Ready.GetProperty("facts");
            Assert.Equal(scenario, facts.GetProperty("scenario").GetString());
            Assert.Equal(completeEnds, facts.GetProperty("completeEnds").GetInt32());
            Assert.Equal(durableFlushes, facts.GetProperty("durableFlushes").GetInt32());
            Assert.False(facts.GetProperty("resultDelivered").GetBoolean());
            Assert.Equal(scenario == "confirmed" ? "Confirmed" : "Unknown", facts.GetProperty("observedOutcome").GetString());
            if (scenario.StartsWith("flush-", StringComparison.Ordinal)) { Assert.True(facts.GetProperty("nativeFlushReturned").GetBoolean()); }
            await child.KillAsync();
        }
        var killed = Snapshot();
        var cold = (await InternalProcessChild.RunAsync(output, _root, "cu-verify", scenario)).Json.GetProperty("facts");
        Assert.Equal(scenario == "first-end", cold.GetProperty("allOld").GetBoolean());
        Assert.Equal(scenario != "first-end", cold.GetProperty("allNew").GetBoolean());
        Assert.True(cold.GetProperty("historyChecked").GetBoolean());
        Assert.True(cold.GetProperty("auditChecked").GetBoolean());
        Assert.Equal(killed, Snapshot()); // The first cold verifier is read-only at both owner layers.
        await InternalProcessChild.RunAsync(output, _root, "cu-fork", scenario);
        var forked = Snapshot();
        await InternalProcessChild.RunAsync(output, _root, "cu-verify", scenario);
        Assert.Equal(forked, Snapshot());
        // Only actual process termination is established here. No returned result from the killed
        // request, native device failure or power-loss qualification is inferred.
    }

    private string[] Snapshot() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(_root, path).Replace('\\', '/')}|{new FileInfo(path).Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "atelia-versionstore-cu-process")) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("CU process cleanup escaped its fixture parent."); }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
