using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Atelia;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

// Only the experiment observes existing private scratch; production has no new hook.
internal static class ThresholdProbe {
    private const uint Tag = 83;
    private static readonly FieldInfo Scratch = typeof(RbfFileImpl).GetField("_appendScratch", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Existing append scratch field was not found.");

    internal sealed record Case(string Name, int TotalBytes, int MetaBytes, bool Marker) {
        internal int PayloadBytes => TotalBytes - 32 - MetaBytes;
        internal byte[] Input => Program.Pattern(TotalBytes - 32, Marker);
    }

    private sealed record Timing(Case Case, string File, int WarmOperations, long FirstAppendTicks, long FirstAllocatedBytes,
        long FirstFlushTicks, long WarmAppendTicks, long WarmAllocatedBytes, long WarmFlushTicks,
        int ScratchBefore, int ScratchAfterFirst, int ScratchAfterWarm, int ScratchAfterDispose,
        uint Key, uint LastKey, string FirstWire, string LastWire);

    private sealed record Count(Case Case, string File, List<RbfWriteObservation> FirstWrites, List<RbfWriteObservation> WarmWrites,
        int ScratchAfterFirst, int ScratchAfterWarm, int ScratchAfterDispose, uint Key, uint LastKey, string FirstWire, string LastWire);

    private static Case[] Cases() {
        var cases = new List<Case>();
        foreach (int total in new[] { 512, 4092, 4096, 4100, 6144, 8188, 8192, 8196, 65536 }) {
            cases.Add(new($"total-{total}-zero-meta-{(total is 512 or 65536 ? 0 : 3)}", total, total is 512 or 65536 ? 0 : 3, false));
        }
        foreach (int total in new[] { 512, 4100, 6144, 8192, 8196, 65536 }) {
            cases.Add(new($"total-{total}-marker-meta-{(total is 6144 or 65536 ? 0 : 3)}", total, total is 6144 or 65536 ? 0 : 3, true));
        }
        return cases.ToArray();
    }

    internal static int Run(string[] args) {
        string Value(string name) {
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException($"Required: {name} <value>"); }
            return args[index + 1];
        }
        string output = Path.GetFullPath(Value("--output"));
        Program.Require(Path.GetPathRoot(output)!.Equals("W:\\", StringComparison.OrdinalIgnoreCase), "Threshold artifacts must be on W:.");
        Program.Require(!Directory.Exists(output), "Threshold sample output must be fresh.");
        string variant = Value("--variant");
        int round = int.Parse(Value("--round"));
        int threshold = int.Parse(Value("--threshold"));
        int batchMiB = int.Parse(Value("--batch-mib"));
        int operationCap = int.Parse(Value("--operation-cap"));
        Program.Require(threshold is 4096 or 8192 && batchMiB is >= 1 and <= 64 && operationCap >= 2, "Invalid bounded measurement settings.");
        Program.Require(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0" &&
            Environment.GetEnvironmentVariable("DOTNET_TC_QuickJitForLoops") == "0", "Both frozen variants require the same fixed optimized JIT.");
        Directory.CreateDirectory(Path.Combine(output, "io"));
        var cases = Cases();
        foreach (var item in cases) {
            var layout = new FrameLayout(RbfProfile.Rbf3, item.PayloadBytes, item.MetaBytes);
            Program.Require(layout.FrameLength + RbfLayout.FenceSize == item.TotalBytes &&
                item.TotalBytes == ((item.PayloadBytes + item.MetaBytes + 3) & ~3) + 32, "RBF3 boundary layout mismatch.");
        }
        // Warm every public path and the process-wide Shared pool before lifecycle timing.
        // 'First' below is a new File's first Append, not process/OS/pool cold.
        foreach (var item in cases) {
            using var file = RbfFile.CreateNew(Path.Combine(output, "io", $"jit-warm-{item.Name}.rbf"), RbfCacheMode.Off);
            byte[] input = item.Input;
            for (int i = 0; i < 8; i++) { Append(file, item, input); }
        }
        bool jitOnly = args.Contains("--jit-only");
        var timings = new List<Timing>();
        var counts = new List<Count>();
        if (!jitOnly) {
            foreach (var item in cases) { timings.Add(Measure(output, item, batchMiB, operationCap)); }
            // No write hook is installed until all timing passes have finished.
            foreach (var item in cases) { counts.Add(CountWrites(output, item, threshold)); }
        }
        var assemblies = new[] { typeof(Program).Assembly, typeof(RbfFile).Assembly, typeof(SizedPtr).Assembly, typeof(AteliaResult<int>).Assembly };
        var identity = assemblies.Distinct().Select(assembly => new {
            Name = assembly.GetName().Name, assembly.Location,
            ProductVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion,
            InformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
        }).ToArray();
        Program.WriteJson(Path.Combine(output, "sample.json"), new {
            SchemaVersion = 1, Passed = true, Variant = variant, Round = round, DeclaredThreshold = threshold,
            ObservedThreshold = jitOnly ? (int?)null : counts.Single(row => row.Case.TotalBytes == 4100 && !row.Case.Marker).FirstWrites.Count == 1 ? 8192 : 4096,
            JitOnly = jitOnly,
            Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OS = RuntimeInformation.OSDescription, ProcessId = Environment.ProcessId, StopwatchFrequency = Stopwatch.Frequency,
            BatchMiB = batchMiB, OperationCap = operationCap, Cases = cases, Timings = timings, Counts = counts, LoadedAssemblies = identity,
            RuntimeEnvironment = new Dictionary<string, string?> {
                ["DOTNET_TieredCompilation"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                ["DOTNET_TC_QuickJitForLoops"] = Environment.GetEnvironmentVariable("DOTNET_TC_QuickJitForLoops"),
                ["DOTNET_JitDisasm"] = Environment.GetEnvironmentVariable("DOTNET_JitDisasm"),
                ["DOTNET_JitStdOutFile"] = Environment.GetEnvironmentVariable("DOTNET_JitStdOutFile")
            },
            Scope = "Public Append with production key selector, no hooks during timing; fixed optimized JIT and Shared pool prewarmed. First means new File first Append, excludes factory/Dispose. Warm uses same File. Flush timed separately; scratch observed outside timing by read-only reflection. Separate library write counts, not device I/O. Python audits actual wires after timing."
        });
        Console.WriteLine($"Threshold sample passed: {variant}, round={round}, cases={timings.Count}");
        return 0;
    }

    private static SizedPtr Append(IRbfFile file, Case item, byte[] input) =>
        file.Append(Tag, input.AsSpan(0, item.PayloadBytes), input.AsSpan(item.PayloadBytes)).Unwrap();

    private static int ScratchBytes(IRbfFile file) => Scratch.GetValue(file) is byte[] buffer ? buffer.Length : 0;

    private static Timing Measure(string output, Case item, int batchMiB, int operationCap) {
        Program.Require(RbfWriteInstrumentation.Current is null, "Timing cannot run with write hooks.");
        string relative = $"io/timing-{item.Name}.rbf";
        string path = Path.Combine(output, relative);
        byte[] input = item.Input;
        int operations = Math.Min(operationCap, Math.Max(2, batchMiB * 1024 * 1024 / item.TotalBytes));
        var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
        SizedPtr first = default, last = default;
        long firstTicks, firstAllocated, firstFlush, warmTicks, warmAllocated, warmFlush;
        int before, afterFirst, afterWarm;
        try {
            before = ScratchBytes(file);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long began = Stopwatch.GetTimestamp();
            first = Append(file, item, input);
            firstTicks = Stopwatch.GetTimestamp() - began;
            firstAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            afterFirst = ScratchBytes(file);
            began = Stopwatch.GetTimestamp();
            file.DurableFlush();
            firstFlush = Stopwatch.GetTimestamp() - began;
            allocated = GC.GetAllocatedBytesForCurrentThread();
            began = Stopwatch.GetTimestamp();
            for (int i = 0; i < operations; i++) { last = Append(file, item, input); }
            warmTicks = Stopwatch.GetTimestamp() - began;
            warmAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            afterWarm = ScratchBytes(file);
            began = Stopwatch.GetTimestamp();
            file.DurableFlush();
            warmFlush = Stopwatch.GetTimestamp() - began;
            Program.VerifyFrame(file, first, input, item.MetaBytes, Tag);
            Program.VerifyFrame(file, last, input, item.MetaBytes, Tag);
        }
        finally { file.Dispose(); }
        Program.Require(ScratchBytes(file) == 0, "Disposed File retained append scratch.");
        Program.Require(new FileInfo(path).Length == 4L + (operations + 1L) * item.TotalBytes, "Measured output length mismatch.");
        var wires = ExportWires(output, path, first, last, $"timing-{item.Name}", item);
        return new(item, relative, operations, firstTicks, firstAllocated, firstFlush, warmTicks, warmAllocated, warmFlush,
            before, afterFirst, afterWarm, ScratchBytes(file), wires.Key, wires.LastKey, wires.First, wires.Last);
    }

    private static Count CountWrites(string output, Case item, int threshold) {
        string relative = $"io/count-{item.Name}.rbf";
        string path = Path.Combine(output, relative);
        byte[] input = item.Input;
        var firstWrites = new List<RbfWriteObservation>();
        var warmWrites = new List<RbfWriteObservation>();
        var active = firstWrites;
        var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
        SizedPtr first = default, last = default;
        int afterFirst, afterWarm;
        try {
            RbfWriteInstrumentation.Current = new() { AfterWrite = observation => active.Add(observation) };
            first = Append(file, item, input);
            afterFirst = ScratchBytes(file);
            active = warmWrites;
            last = Append(file, item, input);
            afterWarm = ScratchBytes(file);
        }
        finally { RbfWriteInstrumentation.Current = null; file.Dispose(); }
        var wires = ExportWires(output, path, first, last, $"count-{item.Name}", item);
        int expectedCount = item.TotalBytes <= threshold || wires.Key != 0 ? 1 : item.MetaBytes == 0 ? 3 : 4;
        foreach (var writes in new[] { firstWrites, warmWrites }) {
            Program.Require(writes.Count == expectedCount && writes.Sum(write => write.WrittenBytes) == item.TotalBytes &&
                writes.All(write => write.WrittenBytes == write.RequestedBytes), "Loaded writer does not match declared threshold/output path.");
            for (int i = 1; i < writes.Count; i++) {
                Program.Require(writes[i].Offset == writes[i - 1].Offset + writes[i - 1].WrittenBytes, "Write calls were not sequential.");
            }
        }
        int expectedScratch = wires.Key != 0 && item.TotalBytes > threshold ? RbfAppendImpl.EscapeScratchSize : 0;
        Program.Require(afterFirst == expectedScratch && afterWarm == expectedScratch && ScratchBytes(file) == 0, "Scratch lifecycle differs from expected loaded path.");
        return new(item, relative, firstWrites, warmWrites, afterFirst, afterWarm, ScratchBytes(file), wires.Key, wires.LastKey, wires.First, wires.Last);
    }

    private static (uint Key, uint LastKey, string First, string Last) ExportWires(string output, string path, SizedPtr first, SizedPtr last, string label, Case item) {
        byte[] Extract(SizedPtr ticket) {
            byte[] wire = new byte[ticket.Length + 8];
            using var stream = File.OpenRead(path);
            stream.ReadExactly(wire.AsSpan(0, 4));
            stream.Position = ticket.Offset;
            stream.ReadExactly(wire.AsSpan(4));
            Program.Require(ticket.Length + 4 == item.TotalBytes, "Actual frame layout differs from case boundary.");
            return wire;
        }
        byte[] firstBytes = Extract(first), lastBytes = Extract(last);
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(firstBytes.AsSpan(firstBytes.Length - 8));
        uint lastKey = BinaryPrimitives.ReadUInt32LittleEndian(lastBytes.AsSpan(lastBytes.Length - 8));
        Program.Require(item.Marker ? key != 0 && lastKey != 0 : key == 0 && lastKey == 0, "Production selector key differs from requested input category.");
        string firstName = $"io/{label}-first.rbf", lastName = $"io/{label}-last.rbf";
        File.WriteAllBytes(Path.Combine(output, firstName), firstBytes);
        File.WriteAllBytes(Path.Combine(output, lastName), lastBytes);
        return (key, lastKey, firstName, lastName);
    }
}
