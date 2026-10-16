using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.InternalProcess;

[Collection("HeadStore processes")]
[Trait("Qualification", "Internal+Process")]
public sealed class HeadSetTagProcessTests(ITestOutputHelper output) : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-headsettag-process", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("dependencies-confirmed")]
    [InlineData("record-appended")]
    [InlineData("confirmed")]
    public async Task ActualKillAtTagPublicationWindowsPreservesExactCuAndForkMembers(string scenario) {
        await InternalProcessChild.RunAsync(output, _root, "set-tag.seed");
        await using (var child = await InternalProcessChild.StartParkedAsync(output, _root, "set-tag.interrupt", scenario)) {
            var facts = child.Ready.GetProperty("facts");
            Assert.Equal(scenario, facts.GetProperty("scenario").GetString());
            Assert.True(facts.GetProperty("dependenciesConfirmed").GetBoolean());
            Assert.Equal(new uint[] { 1, 2, 3 }, facts.GetProperty("confirmedHeadIds").EnumerateArray().Select(value => value.GetUInt32()));
            Assert.Equal(new[] { 1, 1, 1, 0 }, facts.GetProperty("dependencyFlushCounts").EnumerateArray().Select(value => value.GetInt32()));
            Assert.Equal(scenario != "dependencies-confirmed", facts.GetProperty("recordAppended").GetBoolean());
            Assert.Equal(scenario == "confirmed" ? 1 : 0, facts.GetProperty("bucketFlushes").GetInt32());
            Assert.Equal(scenario switch { "dependencies-confirmed" => "NotAttempted", "record-appended" => "Unknown", _ => "Confirmed" },
                facts.GetProperty("outcome").GetString());
            Assert.False(facts.GetProperty("resultDelivered").GetBoolean());
            await child.KillAsync();
        }
        // Every reader starts a fresh process with fresh owners for both layers.
        // A completed but unflushed frame survives this process termination; no
        // assertion below implies survival of power loss or failed device writes.
        string[] killed = CaptureFiles();
        await InternalProcessChild.RunAsync(output, _root, "set-tag.verify", scenario, "ro");
        Assert.Equal(killed, CaptureFiles());
        await InternalProcessChild.RunAsync(output, _root, "set-tag.verify", scenario, "rw");
        Assert.Equal(killed, CaptureFiles());
        await InternalProcessChild.RunAsync(output, _root, "set-tag.advance");
        string[] advanced = CaptureFiles();
        for (int pass = 0; pass < 2; pass++) {
            var result = (await InternalProcessChild.RunAsync(output, _root, "set-tag.verify", scenario, "ro")).Json.GetProperty("facts");
            Assert.True(result.GetProperty("advanced").GetBoolean());
            Assert.True(result.GetProperty("audit").GetBoolean());
            Assert.Equal(scenario != "dependencies-confirmed", result.GetProperty("exactMembers").GetBoolean());
            Assert.Equal(advanced, CaptureFiles());
        }
    }

    private string[] CaptureFiles() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Directory.Exists(path) ? $"D:{Path.GetRelativePath(_root, path)}" :
            $"F:{Path.GetRelativePath(_root, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() {
        string target = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "atelia-headsettag-process")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Cleanup escaped the fixture parent."); }
        if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
    }
}
