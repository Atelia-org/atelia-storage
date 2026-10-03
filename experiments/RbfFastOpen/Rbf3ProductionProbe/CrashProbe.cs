using System.Diagnostics;
using Atelia;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

internal static class CrashProbe {
    internal static List<object> Run(string output, bool quick) {
        var rows = new List<object>();
        var canonical = Correctness.MakeCanonical(Path.Combine(output, "io", "crash-source.rbf"), builder: false);
        int[] writePrefixes = quick ? [0, 3, 160, 320, 321, 324, 327, 328] :
            [0, 3, 160, 320, 321, 322, 323, 324, 325, 326, 327, 328];
        foreach (string method in new[] { "append", "builder" }) {
            foreach (int prefix in writePrefixes) {
                string path = Path.Combine(output, "io", $"kill-{method}-{prefix:D3}.rbf");
                File.WriteAllBytes(path, canonical.Bytes[..(int)canonical.Start]);
                string checkpoint = TerminateAtReady(method, path, prefix, output);
                Program.Require(new FileInfo(path).Length == canonical.Start + prefix, "Killed writer prefix is not the injected physical prefix.");
                var report = Correctness.VerifyRecover(path, canonical.Start, prefix, prefix >= Correctness.BodyCompletePrefix);
                rows.Add(new { Operation = method, Prefix = prefix, Checkpoint = checkpoint, Recovery = report.Action.ToString() });
            }
        }
        foreach (int prefix in quick ? new[] { 0, 1, 4, 7, 8 } : Enumerable.Range(0, 9)) {
            string path = Path.Combine(output, "io", $"kill-repair-{prefix}.rbf");
            File.WriteAllBytes(path, canonical.Bytes[..((int)canonical.Start + Correctness.BodyCompletePrefix)]);
            string checkpoint = TerminateAtReady("repair", path, prefix, output);
            int actualPrefix = Correctness.BodyCompletePrefix + prefix;
            Program.Require(new FileInfo(path).Length == canonical.Start + actualPrefix, "Killed repair did not publish the actual Key/Fence prefix.");
            var report = Correctness.VerifyRecover(path, canonical.Start, actualPrefix, verifyTail: true);
            rows.Add(new { Operation = "repair", Prefix = prefix, Checkpoint = checkpoint, Recovery = report.Action.ToString() });
        }
        foreach (string mode in new[] { "truncate-before", "truncate-after" }) {
            string path = Path.Combine(output, "io", $"kill-{mode}.rbf");
            File.WriteAllBytes(path, canonical.Bytes[..((int)canonical.Start + 160)]);
            string checkpoint = TerminateAtReady(mode, path, 0, output);
            int prefix = mode.EndsWith("after") ? 0 : 160;
            Program.Require(new FileInfo(path).Length == canonical.Start + prefix, "SetLength checkpoint physical length mismatch.");
            var report = Correctness.VerifyRecover(path, canonical.Start, prefix, verifyTail: false);
            rows.Add(new { Operation = mode, Prefix = prefix, Checkpoint = checkpoint, Recovery = report.Action.ToString() });
        }
        return rows;
    }

    private static string TerminateAtReady(string mode, string path, int prefix, string output) {
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string arg in new[] { "--worker", mode, path, prefix.ToString(System.Globalization.CultureInfo.InvariantCulture) }) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker start failed.");
        var stderr = process.StandardError.ReadToEndAsync();
        string? ready = null;
        try {
            var lineTask = process.StandardOutput.ReadLineAsync();
            if (!lineTask.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("Worker READY timeout."); }
            ready = lineTask.Result;
            Program.Require(ready is not null && ready.StartsWith("READY ", StringComparison.Ordinal), "Worker exited without a READY checkpoint.");
            process.Kill(entireProcessTree: true);
            Program.Require(process.WaitForExit(30_000), "Killed worker failed to exit.");
            return ready!;
        }
        finally {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(30_000); }
            File.WriteAllText(Path.Combine(output, $"worker-{mode}-{prefix}.log"), $"PID {process.Id}\n{ready}\nExitCode {process.ExitCode}\n{stderr.GetAwaiter().GetResult()}");
        }
    }

    internal static int Worker(string[] args) {
        string mode = args[0], path = args[1];
        int prefix = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        if (mode == "append" || mode == "builder") {
            using var file = RbfFile.OpenExisting(path, out _);
            long target = file.TailOffset + prefix;
            RbfWriteInstrumentation.Current = PrefixHooks(target, mode);
            Program.WriteFrame(file, Correctness.Tail, Correctness.TailMetaLength, Correctness.TailTag, mode == "builder");
            throw new InvalidOperationException("Writer completed without the requested checkpoint.");
        }
        if (mode == "repair") {
            long target = new FileInfo(path).Length + prefix;
            RbfWriteInstrumentation.Current = PrefixHooks(target, mode);
            using var file = RbfFile.OpenExisting(path, out _);
            throw new InvalidOperationException("Recovery completed without the requested repair checkpoint.");
        }
        if (mode == "truncate-before" || mode == "truncate-after") {
            RbfWriteInstrumentation.Current = new RbfWriteHooks {
                BeforeSetLength = mode.EndsWith("before") ? request => Ready($"BeforeSetLength length={request.Length}") : null,
                AfterSetLength = mode.EndsWith("after") ? request => Ready($"AfterSetLength length={request.Length}") : null
            };
            using var file = RbfFile.OpenExisting(path, out _);
            throw new InvalidOperationException("Recovery completed without the requested SetLength checkpoint.");
        }
        throw new ArgumentException("Unknown worker mode.");
    }

    private static RbfWriteHooks PrefixHooks(long target, string mode) => new() {
        BeforeWrite = request => checked((int)Math.Clamp(target - request.Offset, 0, request.RequestedBytes)),
        AfterWrite = request => {
            if (request.Offset + request.WrittenBytes >= target) {
                Ready($"AfterWrite operation={mode} offset={request.Offset} requested={request.RequestedBytes} actual={request.WrittenBytes} target={target}");
            }
        }
    };

    private static void Ready(string checkpoint) {
        Console.WriteLine("READY " + checkpoint);
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
}
