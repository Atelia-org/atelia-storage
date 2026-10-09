using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This probe qualifies Windows FileShare only; Linux needs checked native locking.");
if (args.Length > 0 && args[0] == "child") {
    try {
        using var lease = Open(args[1], args[2], FileMode.Open);
        Console.WriteLine("ACQUIRED");
        if (args[3] == "hold") Console.ReadLine();
        return;
    }
    catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) {
        Console.WriteLine("BLOCKED");
        return;
    }
}

var evidence = Path.GetFullPath(args.Length == 1 ? args[0] : "experiments/StoreOwnerLockProbe/evidence/2026-10-09-result.json");
var workspace = Path.GetFullPath("artifacts/store-owner-lock-probe");
var root = Path.Combine(workspace, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "framestore.lock");
var checks = new List<string>();
void Check(bool condition, string name) {
    if (!condition) throw new InvalidOperationException(name);
    checks.Add(name);
}
using (var bootstrap = Open(path, "w", FileMode.OpenOrCreate)) {
    Check(bootstrap.Length == 0, "bootstrap is zero bytes before any gate");
    Check(!File.Exists(Path.Combine(root, "framestore.format")), "bootstrap does not require a gate");
}

foreach (var held in new[] { "w", "r" }) {
    using var lease = Open(path, held, FileMode.Open);
    foreach (var requested in new[] { "w", "r" }) {
        var permitted = held == "r" && requested == "r";
        var acquired = false;
        try { using var other = Open(path, requested, FileMode.Open); acquired = true; }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
        Check(acquired == permitted, $"same process {held}/{requested}");
        using var child = Start(path, requested, "try");
        var response = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.WaitForExit(10000) && child.ExitCode == 0, "child try exits normally");
        Check(response == (permitted ? "ACQUIRED" : "BLOCKED"), $"cross process {held}/{requested}");
    }
    var deleted = false;
    try { File.Delete(path); deleted = true; }
    catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
    Check(!deleted, $"Windows denies control-file deletion during {held} lease");
}

foreach (var held in new[] { "w", "r" }) {
    using var child = Start(path, held, "hold");
    try {
        var response = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(response == "ACQUIRED", $"child holds {held}");
        var acquired = false;
        try { using var other = Open(path, "w", FileMode.Open); acquired = true; }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
        Check(!acquired, $"child {held} excludes writer before kill");
        child.Kill();
        Check(child.WaitForExit(10000), "killed child exits");
        using var reopened = Open(path, "w", FileMode.Open);
        Check(reopened.Length == 0, $"writer reacquires unchanged file after killed {held}");
    }
    finally {
        if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
    }
}

using (var create = Open(path, "w", FileMode.OpenOrCreate)) {
    var acquired = false;
    try { using var second = Open(path, "w", FileMode.OpenOrCreate); acquired = true; }
    catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
    Check(!acquired && create.Length == 0, "second bootstrap cannot bypass or truncate held inode");
}
var missing = Path.Combine(root, "missing.lock");
var missingRejected = false;
try { using var unexpected = Open(missing, "r", FileMode.Open); }
catch (FileNotFoundException) { missingRejected = true; }
Check(missingRejected && !File.Exists(missing), "read-only missing lock rejects without creating");
File.WriteAllBytes(path, new byte[] { 0x71 });
foreach (var mode in new[] { "w", "r" }) {
    var invalidRejected = false;
    try { using var invalid = Open(path, mode, FileMode.Open); }
    catch (InvalidDataException) { invalidRejected = true; }
    Check(invalidRejected && File.ReadAllBytes(path).SequenceEqual(new byte[] { 0x71 }), $"{mode} rejects nonzero control file without truncation");
}
Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
File.WriteAllText(evidence, JsonSerializer.Serialize(new {
    designBaseline = "3d776b1", runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, root,
    scope = "Windows FileShare admission primitive only; no FrameStore, root/no-follow, Linux, rename, power-loss or package acceptance",
    count = checks.Count, checks
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {checks.Count} checks; evidence: {evidence}; fixture retained: {root}");

static FileStream Open(string path, string mode, FileMode fileMode) {
    var stream = new FileStream(path, fileMode,
        mode == "w" ? FileAccess.ReadWrite : FileAccess.Read,
        mode == "w" ? FileShare.None : FileShare.Read);
    try {
        if (stream.Length != 0) throw new InvalidDataException("Control file must remain exactly empty.");
        return stream;
    }
    catch { stream.Dispose(); throw; }
}

static Process Start(string path, string mode, string behavior) {
    var start = new ProcessStartInfo("dotnet") {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true
    };
    foreach (var value in new[] { Assembly.GetExecutingAssembly().Location, "child", path, mode, behavior }) start.ArgumentList.Add(value);
    return Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
}
