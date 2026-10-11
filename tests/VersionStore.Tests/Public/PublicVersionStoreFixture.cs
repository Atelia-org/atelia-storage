using System.Diagnostics;
using Atelia.Data;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Public;

/// <summary>真实 public 工厂夹具；手工截尾/改字节不作为进程终止或断电证据。</summary>
internal sealed class PublicVersionStoreFixture : IDisposable {
    private readonly string _parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
    internal string Sandbox { get; }
    internal string Root { get; }
    internal string DataRoot { get; }
    internal string GatePath => Path.Combine(Root, "versionstore.format");
    internal string LockPath => Path.Combine(Root, "versionstore.lock");
    internal string PendingPath => Path.Combine(Root, "creating", "pending.rbf");
    internal DataStore Data { get; }

    internal PublicVersionStoreFixture() {
        Sandbox = Path.Combine(_parent, "VersionStore-public-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Sandbox);
        Root = Path.Combine(Sandbox, "store");
        DataRoot = Path.Combine(Sandbox, "data");
        Data = DataStore.Create(DataRoot);
    }

    internal string RefPath(uint localId) => Path.Combine(Root, "refs", $"{localId:x8}.rbf");

    internal RefRevision SeedClosed(uint reservedUpper = 256, uint localId = 7, byte[]? payload = null) {
        using var store = Store.Create(Root, Data, reservedUpper);
        RefRevision revision = store.CreateRef(localId, payload ?? [0xff], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return revision;
    }

    /// <summary>在 owner 之间调用；保留锁目录项但不打开 FileShare.None 的控制文件。</summary>
    internal Dictionary<string, byte[]?> Snapshot() {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (!Directory.Exists(Root)) { return result; }
        foreach (string directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(Root, directory) + Path.DirectorySeparatorChar, null);
        }
        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(Root, file);
            result.Add(relative, relative == "versionstore.lock" ? [] : File.ReadAllBytes(file));
        }
        return result;
    }

    internal void AssertUnchanged(Dictionary<string, byte[]?> before) {
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); }
    }

    internal static void FlipPayloadByte(string path, SizedPtr ticket) {
        // A 128B test payload keeps the midpoint inside content, away from framing and its trailer.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long offset = ticket.Offset + ticket.Length / 2;
        stream.Position = offset;
        int original = stream.ReadByte();
        Assert.True(original >= 0);
        stream.Position = offset;
        stream.WriteByte((byte)(original ^ 1));
    }

    internal static void CreateDirectoryLink(string link, string target) {
        if (OperatingSystem.IsLinux()) { Directory.CreateSymbolicLink(link, target); return; }
        // Junctions need no symlink privilege; both paths belong to this fixture.
        var start = new ProcessStartInfo("cmd.exe") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Junction probe did not start.");
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose() {
        Data.Dispose();
        string resolved = Path.GetFullPath(Sandbox);
        string parent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("VersionStore-public-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
