using System.Globalization;

namespace FrameStoreResourceProbe;

internal sealed record ProbeOptions {
    public string WorkRoot { get; init; } = Path.GetFullPath(Path.Combine("artifacts", "FrameStoreResourceProbe", DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")));
    public string? Output { get; init; }
    public string SourceRevision { get; init; } = "not-supplied";
    public int[] BuilderLimits { get; init; } = [1, 4, 16];
    public int[] ArchiveScales { get; init; } = [16, 128, 1024];
    public int[] SizeHints { get; init; } = [1 << 20, 4 << 20, 8 << 20];
    public int Repeats { get; init; } = 3;
    public int HintIterations { get; init; } = 4;
    public int AppendIterations { get; init; } = 8;
    public int LargePayloadBytes { get; init; } = 256 << 10;
    public int ArchivePayloadBytes { get; init; } = 256;
    public int RandomReads { get; init; } = 64;
    public int MaxSeconds { get; init; } = 180;
    public long MaxDiskBytes { get; init; } = 96L << 20;

    // Reserve at least one 4KiB allocation unit per small file, and 8KiB overhead per large frame.
    // This is input admission, not an assertion about an unknown filesystem's allocation unit.
    public long EstimatedDataBytes => checked(
        4096L * (16 + Repeats * BuilderLimits.Sum(m => (long)m + 1) + ArchiveScales.Sum(n => (long)n))
        + 2L * Repeats * AppendIterations * (LargePayloadBytes + 8192L));

    public static ProbeOptions Parse(string[] args) {
        var options = new ProbeOptions();
        for (int i = 0; i < args.Length; i++) {
            if (args[i] is "--help" or "-h") {
                throw new HelpRequestedException();
            }
            if (++i >= args.Length) { throw new ArgumentException("Each option requires a value."); }
            string value = args[i];
            options = args[i - 1] switch {
                "--work-root" => options with { WorkRoot = Path.GetFullPath(value) },
                "--output" => options with { Output = Path.GetFullPath(value) },
                "--source-revision" => options with { SourceRevision = value },
                "--builder-limits" => options with { BuilderLimits = Numbers(value) },
                "--archive-scales" => options with { ArchiveScales = Numbers(value) },
                "--size-hints" => options with { SizeHints = Numbers(value) },
                "--repeats" => options with { Repeats = Number(value) },
                "--hint-iterations" => options with { HintIterations = Number(value) },
                "--append-iterations" => options with { AppendIterations = Number(value) },
                "--large-payload-bytes" => options with { LargePayloadBytes = Number(value) },
                "--archive-payload-bytes" => options with { ArchivePayloadBytes = Number(value) },
                "--random-reads" => options with { RandomReads = Number(value) },
                "--max-seconds" => options with { MaxSeconds = Number(value) },
                "--max-disk-mib" => options with { MaxDiskBytes = checked((long)Number(value) << 20) },
                _ => throw new ArgumentException("Unknown option: " + args[i - 1])
            };
        }
        options.Validate();
        return options;
    }

    private void Validate() {
        if (BuilderLimits.Length > 8 || BuilderLimits.Any(m => m > 128)
            || ArchiveScales.Length < 2 || ArchiveScales.Length > 8 || ArchiveScales.Any(n => n > 16384)
            || SizeHints.Length > 8 || SizeHints.Any(n => n > 32 << 20)
            || Repeats is < 2 or > 10 || HintIterations > 32 || AppendIterations > 128
            || LargePayloadBytes is < 16384 or > 4 << 20 || ArchivePayloadBytes is < 256 or > 4096
            || RandomReads > 4096 || MaxSeconds > 600 || MaxDiskBytes > 256L << 20) {
            throw new ArgumentException("Input exceeds the finite probe limits; see README.md.");
        }
        if (EstimatedDataBytes > MaxDiskBytes) {
            throw new ArgumentException($"Estimated data {EstimatedDataBytes} bytes exceeds declared disk budget {MaxDiskBytes}.");
        }
        if (Directory.Exists(WorkRoot) || File.Exists(WorkRoot)) {
            throw new ArgumentException("Work root must not already exist: " + WorkRoot);
        }
        if (Output is not null && (Directory.Exists(Output) || File.Exists(Output))) {
            throw new ArgumentException("Output must not already exist: " + Output);
        }
    }

    private static int Number(string value) {
        int result = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        return result > 0 ? result : throw new ArgumentException("All numeric inputs must be positive.");
    }

    private static int[] Numbers(string value) => value.Split(',').Select(Number).Distinct().Order().ToArray();
}

internal sealed class HelpRequestedException : Exception;
internal sealed class BudgetExceededException(string message) : Exception(message);
