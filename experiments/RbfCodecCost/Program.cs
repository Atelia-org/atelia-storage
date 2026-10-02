using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RbfCodecCost;

internal static class Program {
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static int Main(string[] args) {
        string? output = null;
        bool quick = args.Contains("--quick"), cpuOnly = args.Contains("--cpu-only"), ioOnly = args.Contains("--io-only");
        bool focused = args.Contains("--focused");
        bool randomSearch = args.Contains("--random-search");
        if (cpuOnly && ioOnly) throw new ArgumentException("Choose at most one of --cpu-only and --io-only.");
        int samples = quick ? 3 : 5;
        for (int i = 0; i < args.Length; i++) if (args[i] == "--output") output = args[++i];
        if (output is null) throw new ArgumentException("--output W:\\<new directory> is required");
        output = Path.GetFullPath(output);
        if (!output.StartsWith("W:\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("All experiment artifacts and real I/O must use W:");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("Output must be new or empty");
        Directory.CreateDirectory(output);
        var environment = new {
            Utc = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount, VectorBytes = Vector<byte>.Count,
            VectorHardware = Vector.IsHardwareAccelerated,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            Output = output, Samples = samples, Quick = quick, CpuOnly = cpuOnly, IoOnly = ioOnly, Focused = focused, RandomSearch = randomSearch,
            FileIO = "Windows buffered RandomAccess, batch FlushToDisk separately timed; OS warm read; RBF1 cache Off"
        };
        object correctness = Correctness.Run(output);
        File.WriteAllText(Path.Combine(output, "correctness.json"), JsonSerializer.Serialize(correctness, Json));
        Console.WriteLine(JsonSerializer.Serialize(correctness));
        List<Measurement> cpu = ioOnly || focused || randomSearch ? [] : CpuProbe.Run(samples, quick);
        File.WriteAllText(Path.Combine(output, "cpu.json"), JsonSerializer.Serialize(cpu, Json));
        List<IoMeasurement> io = cpuOnly || focused || randomSearch ? [] : IoProbe.Run(output, samples, quick);
        List<MetadataMeasurement> metadata = cpuOnly || focused || randomSearch ? [] : MetadataProbe.Run(output, samples);
        object? focusedEvidence = focused ? FocusedProbe.Run(output) : null;
        object? randomSearchEvidence = randomSearch ? RandomSearchProbe.Run(output, quick) : null;
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { Schema = 1, Environment = environment, Correctness = correctness, Cpu = cpu, IO = io, Metadata = metadata, FocusedEvidence = focusedEvidence, RandomSearchEvidence = randomSearchEvidence }, Json));
        Console.WriteLine($"PASS {output}");
        return 0;
    }
}
