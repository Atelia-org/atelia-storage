using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Atelia.FrameStoreProcessProbe;

internal static class Program {
    internal static async Task<int> Main(string[] args) {
        if (args is ["child"]) { return Child.Run(); }
        string? output = null;
        string? revision = null;
        int timeoutSeconds = 30;
        for (int i = 0; i < args.Length; i += 2) {
            if (i + 1 >= args.Length) { throw new ArgumentException("Options require a value."); }
            switch (args[i]) {
                case "--output-directory": output = args[i + 1]; break;
                case "--revision": revision = args[i + 1]; break;
                case "--timeout-seconds": timeoutSeconds = int.Parse(args[i + 1]); break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (timeoutSeconds is < 5 or > 120) { throw new ArgumentOutOfRangeException(nameof(timeoutSeconds)); }
        string root = Path.GetFullPath(output ?? Path.Combine("artifacts", "framestore-process-probe",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("Output directory must be new: " + root); }
        Directory.CreateDirectory(root);
        var run = new ProbeRun(root, TimeSpan.FromSeconds(timeoutSeconds));
        DateTime started = DateTime.UtcNow;
        ErrorInfo? failure = null;
        try {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) {
                throw new PlatformNotSupportedException("FrameStore only admits Windows/Linux.");
            }
            await run.Execute();
        }
        catch (Exception error) { failure = ErrorInfo.From(error); Console.Error.WriteLine(error); }
        var evidence = new {
            schemaVersion = 1, passed = failure is null, startedUtc = started, finishedUtc = DateTime.UtcNow,
            callerSuppliedRevision = revision,
            environment = new {
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), parentPid = Environment.ProcessId,
                currentDirectory = Environment.CurrentDirectory, outputDirectory = root, timeoutSeconds,
                dotnetSystemIoDisableFileLocking = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING"),
                appContextDisableFileLocking = AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled) && disabled,
                assemblyPath = Assembly.GetExecutingAssembly().Location,
                assemblySha256 = Protocol.Hash(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))
            },
            scope = "Source-only public FrameStore, ProcessCrashOnly; acknowledged public stages, not internal syscall fault windows.",
            builderEvidence = "Advanced logical bytes and actual file lengths; pending HeadLen reservation may retain all unfinished bytes in memory.",
            checks = run.Checks, observations = run.Observations, processes = run.Processes, failure
        };
        string result = Path.Combine(root, "result.json");
        await File.WriteAllTextAsync(result, JsonSerializer.Serialize(evidence, Protocol.Pretty));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = failure is null, checks = run.Checks.Count,
            children = run.Processes.Count, result }, Protocol.Json));
        return failure is null ? 0 : 1;
    }
}

internal sealed record CheckEvidence(string Name, bool Passed);

internal sealed class ProbeRun {
    private readonly string _root;
    private readonly string _logs;
    private readonly string _cases;
    private readonly TimeSpan _timeout;
    private int _serial;
    internal List<CheckEvidence> Checks { get; } = [];
    internal List<object> Observations { get; } = [];
    internal List<ProcessEvidence> Processes { get; } = [];

    internal ProbeRun(string root, TimeSpan timeout) {
        _root = root;
        _timeout = timeout;
        _logs = Path.Combine(root, "logs");
        _cases = Path.Combine(root, "cases");
        Directory.CreateDirectory(_logs);
        Directory.CreateDirectory(_cases);
    }

    internal async Task Execute() {
        foreach (string shape in new[] { "empty", "active", "archive" }) { await OwnerMatrix(shape); }
        foreach (string scenario in new[] {
            "create-return", "append-return", "confirm-return", "known-builder", "unknown-builder", "mixed-builders", "archive-return"
        }) { await Crash(scenario); }
    }

    private void Check(bool passed, string name) {
        Checks.Add(new(name, passed));
        if (!passed) { throw new InvalidDataException("Probe assertion failed: " + name); }
    }

    private Task<ChildProcess> Start(string operation, string root, string scenario,
        FrameExpectation[]? expected = null, string[]? unfinished = null) {
        string name = (++_serial).ToString("D3") + "-" + operation + "-" + scenario;
        return ChildProcess.Start(new(operation, root, scenario, Guid.NewGuid().ToString("N"), expected, unfinished),
            _logs, name, _timeout, Processes);
    }

    private async Task<Observation> TryOwner(string root, string mode, bool expectedOpen, string name) {
        await using var child = await Start("try", root, mode);
        var response = await child.Receive("result");
        await child.Finish();
        var observation = response.Observation ?? throw new InvalidDataException("Missing try observation.");
        Observations.Add(new { name, observation });
        Check(observation.Outcome == (expectedOpen ? "opened" : "blocked"), name);
        return observation;
    }

    private async Task<Observation> Observe(string root, string mode, FrameExpectation[] expected,
        string[] unfinished, string name, bool allowRoReject = false) {
        Manifest before = Protocol.Fingerprint(root);
        await using var child = await Start("inspect", root, mode, expected, unfinished);
        var response = await child.Receive("result");
        await child.Finish();
        var observation = response.Observation ?? throw new InvalidDataException("Missing inspect observation.");
        Manifest after = Protocol.Fingerprint(root);
        Observations.Add(new { name, mode, before, after, observation });
        if (mode == "reader") { Check(Protocol.Same(before, after), name + ": read-only leaves paths/lengths/bytes unchanged"); }
        Check(observation.Outcome == "opened" || allowRoReject && observation.Outcome == "rejected",
            name + ": expected public open outcome");
        return observation;
    }

    private void Validate(Observation observation, Stage stage, string name) {
        Check(observation.Outcome == "opened" && observation.StoreId == stage.StoreId, name + ": persistent StoreId");
        var frames = observation.Frames ?? [];
        var reads = observation.Reads ?? [];
        Check(observation.InventoryCount == frames.LongLength && observation.AuditCount == frames.LongLength,
            name + ": Inventory/Audit agree");
        Check((observation.Audits ?? []).Sum(item => item.UserFrameCount) == frames.LongLength,
            name + ": per-file audit counts");
        foreach (var expected in stage.Required) {
            var frame = frames.SingleOrDefault(item => item.Address == expected.Address);
            Check(Matches(frame, expected), name + ": required frame " + expected.Name);
            Check(Matches(reads.SingleOrDefault(item => item.Address == expected.Address)?.Frame, expected),
                name + ": required address read " + expected.Name);
        }
        foreach (var expected in stage.Optional) {
            var frame = frames.SingleOrDefault(item => item.Address == expected.Address);
            var read = reads.SingleOrDefault(item => item.Address == expected.Address)?.Frame;
            Check(frame is null || Matches(frame, expected), name + ": unconfirmed frame absent or exact " + expected.Name);
            Check(frame is null ? read is null : Matches(read, expected),
                name + ": unconfirmed saved address agrees with inventory " + expected.Name);
        }
        var allowed = stage.Required.Concat(stage.Optional).ToDictionary(item => item.Address, StringComparer.Ordinal);
        Check(frames.All(frame => frame.IsTombstone || allowed.TryGetValue(frame.Address, out var expected) && Matches(frame, expected)),
            name + ": no unfinished Builder becomes a completed user frame");
        foreach (string address in stage.Unfinished) {
            var read = reads.Single(item => item.Address == address);
            Check(read.Frame is null || read.Frame.IsTombstone, name + ": early unfinished address has no ordinary frame");
        }
    }

    private static bool Matches(FrameObserved? frame, FrameExpectation expected) => frame is not null &&
        frame.Address == expected.Address && frame.Tag == expected.Tag && frame.Length == expected.Length &&
        frame.TailMetaLength == expected.TailMetaLength && !frame.IsTombstone && frame.Sha256 == expected.Sha256 &&
        frame.PayloadHeadHex == expected.PayloadHeadHex;

    private async Task OwnerMatrix(string shape) {
        string root = Path.Combine(_cases, "owners-" + shape);
        Stage setup;
        await using (var child = await Start("setup", root, shape)) {
            setup = (await child.Receive("result")).Stage ?? throw new InvalidDataException("Missing setup stage.");
            await child.Finish();
        }
        var healthy = await Observe(root, "reader", setup.Required, [], shape + " healthy baseline");
        Validate(healthy, setup, shape + " baseline");
        var lengths = Protocol.Lengths(root);
        Check(shape switch {
            "empty" => lengths.Length == 0,
            "archive" => lengths.Length > 0 && lengths.All(item => item.RelativePath.StartsWith("archive/", StringComparison.Ordinal)),
            _ => lengths.Length > 0 && lengths.All(item => item.RelativePath.StartsWith("active/", StringComparison.Ordinal))
        }, shape + ": fixture data layout");

        await using (var writer = await Start("hold", root, "writer")) {
            var ready = await writer.Receive("ready");
            Check(ready.Stage?.StoreId == setup.StoreId, shape + ": writer ready owns baseline store");
            await TryOwner(root, "writer", false, shape + ": writer/writer blocked");
            await TryOwner(root, "reader", false, shape + ": writer/reader blocked");
            await writer.Kill();
        }
        await TryOwner(root, "writer", true, shape + ": fresh writer after writer kill");
        await TryOwner(root, "reader", true, shape + ": fresh reader after writer kill");

        await using (var first = await Start("hold", root, "reader")) {
            _ = await first.Receive("ready");
            await using var second = await Start("hold", root, "reader");
            _ = await second.Receive("ready");
            Check(true, shape + ": two public readers simultaneously hold owners");
            await TryOwner(root, "writer", false, shape + ": reader/writer blocked with two readers");
            await TryOwner(root, "reader", true, shape + ": reader/reader permitted");
            await first.DisposeOwner();
            await TryOwner(root, "writer", false, shape + ": remaining reader still excludes writer");
            await second.Kill();
        }
        await TryOwner(root, "writer", true, shape + ": fresh writer after final reader kill");
        await TryOwner(root, "reader", true, shape + ": fresh reader after final reader kill");
        var after = await Observe(root, "reader", setup.Required, [], shape + " after owner matrix");
        Validate(after, setup, shape + " owner matrix preserves data");
        Check(new FileInfo(Path.Combine(root, "framestore.lock")).Length == 0, shape + ": control file remains empty");
    }

    private async Task Crash(string scenario) {
        string root = Path.Combine(_cases, "crash-" + scenario);
        Stage stage;
        await using (var child = await Start("crash", root, scenario)) {
            stage = (await child.Receive("ready")).Stage ?? throw new InvalidDataException("Missing crash stage.");
            Observations.Add(new { name = scenario + " acknowledged public stage", pid = child.Evidence.Pid, stage });
            if (stage.ActiveBuilders > 0) {
                Check(stage.BeforeBuilderWrite.Length == stage.ActiveBuilders && stage.AfterBuilderWrite.Length == stage.ActiveBuilders,
                    scenario + ": one distinct active file per unfinished Builder");
            }
            await TryOwner(root, "writer", false, scenario + ": holder excludes competing writer before kill");
            await child.Kill();
        }
        bool dirtyBuilderOutput = stage.ActiveBuilders > 0 && !stage.BeforeBuilderWrite.SequenceEqual(stage.AfterBuilderWrite);
        var beforeRecovery = await Observe(root, "reader", stage.Required.Concat(stage.Optional).ToArray(), stage.Unfinished,
            scenario + " pre-recovery RO", allowRoReject: dirtyBuilderOutput);
        if (beforeRecovery.Outcome == "opened") { Validate(beforeRecovery, stage, scenario + " pre-recovery RO"); }
        var writable = await Observe(root, "writer", stage.Required.Concat(stage.Optional).ToArray(), stage.Unfinished,
            scenario + " fresh writable recovery");
        Validate(writable, stage, scenario + " recovered");
        Check(writable.IsReadOnly == false, scenario + ": writable owner after process kill");
        Check((writable.RecoveryReports ?? []).Length == stage.AfterBuilderWrite.Length || stage.ActiveBuilders == 0,
            scenario + ": independent report for every retained active");
        if (stage.ActiveBuilders > 0 && !dirtyBuilderOutput) {
            Check((writable.RecoveryReports ?? []).All(item => item.Action == "None" && item.OriginalLength == item.FinalLength),
                scenario + ": unpublished Building bytes require no physical tail recovery");
            Check((writable.Frames ?? []).LongLength == stage.Required.LongLength,
                scenario + ": only completed prefixes were physically published");
        }
        var afterRecovery = await Observe(root, "reader", stage.Required.Concat(stage.Optional).ToArray(), stage.Unfinished,
            scenario + " cold read-only after recovery");
        Validate(afterRecovery, stage, scenario + " cold RO");
        Check((afterRecovery.RecoveryReports ?? []).Length == 0, scenario + ": RO has no recovery reports");
        if (scenario == "create-return") {
            Check(Protocol.Lengths(root).Length == 0, scenario + ": successful Create/reopen remains data-free");
        }
        if (scenario == "archive-return") {
            var lengths = Protocol.Lengths(root);
            Check(lengths.Length > 0 && lengths.All(item => item.RelativePath.StartsWith("archive/", StringComparison.Ordinal)),
                scenario + ": archive complete with no active file");
            Check(stage.BeforeArchive.Length == 1 && Matches(stage.BeforeArchive[0], stage.Required[^1]),
                scenario + ": same encoded address/payload before archive and cold reopen");
        }
    }
}
