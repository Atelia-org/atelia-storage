using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Atelia;
using Atelia.Data;
using Atelia.Rbf;

internal static class Program {
    internal const uint Fence = 0x33464252;
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static int Main(string[] args) {
        try {
            if (args.Length > 0 && args[0] == "--worker") { return CrashProbe.Worker(args[1..]); }
            int outputIndex = Array.IndexOf(args, "--output");
            if (outputIndex < 0 || outputIndex + 1 >= args.Length) { throw new ArgumentException("Required: --output <fresh W: directory> [--quick]"); }
            string output = Path.GetFullPath(args[outputIndex + 1]);
            Require(Path.GetPathRoot(output)!.Equals("W:\\", StringComparison.OrdinalIgnoreCase), "All probe artifacts must be on W:.");
            bool quick = args.Contains("--quick");
            Require(!Directory.Exists(Path.Combine(output, "io")) && !File.Exists(Path.Combine(output, "production-results.json")), "Probe output already contains a prior run.");
            Directory.CreateDirectory(Path.Combine(output, "io"));
            Console.WriteLine($"RBF3 production probe started: {output}, quick={quick}");
            var golden = Correctness.VerifyGolden(output);
            Console.WriteLine("Independent Python fixtures verified with production APIs.");
            var cuts = Correctness.VerifyProductionCuts(output);
            Console.WriteLine("All normal writer tail byte-prefix cuts and CRC orthogonality verified.");
            var crashes = CrashProbe.Run(output, quick);
            Console.WriteLine($"Managed child termination checkpoints verified: {crashes.Count}");
            var measurements = MeasurementProbe.Run(output, quick);
            var result = new {
                SchemaVersion = 1, Passed = true, Profile = "RBF3-units-tail-key",
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessId = Environment.ProcessId, StopwatchFrequency = Stopwatch.Frequency,
                Quick = quick, Samples = quick ? 3 : 7,
                Golden = golden, BytePrefixCuts = cuts, ManagedTermination = crashes,
                measurements.Open, measurements.Anomalous, measurements.Performance,
                Scope = "Actual public RBF3 APIs and existing internal logical metrics/write instrumentation; warm OS cache; managed child kills at READY checkpoints. No device-crash/cold-cache/arbitrary syscall interruption/downstream/package acceptance."
            };
            WriteJson(Path.Combine(output, "production-results.json"), result);
            Console.WriteLine("RBF3 production probe passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    internal static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    internal static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));

    internal static SizedPtr WriteFrame(IRbfFile file, ReadOnlySpan<byte> combined, int metaLength, uint tag, bool builder) {
        if (!builder) { return file.Append(tag, combined[..(combined.Length - metaLength)], combined[(combined.Length - metaLength)..]).Unwrap(); }
        using var frame = file.BeginAppend();
        // Deliberately cross owned writer chunks; caller's input is already available.
        for (int i = 0; i < combined.Length;) {
            int count = Math.Min(32761, combined.Length - i);
            combined.Slice(i, count).CopyTo(frame.PayloadAndMeta.GetSpan(count));
            frame.PayloadAndMeta.Advance(count);
            i += count;
        }
        return frame.EndAppend(tag, metaLength).Unwrap();
    }

    internal static byte[] Pattern(int length, bool marker) {
        var value = new byte[length];
        if (marker) {
            for (int i = 0; i < length; i++) { value[i] = (byte)(Fence >> ((i & 3) * 8)); }
        }
        return value;
    }

    internal static void VerifyFrame(IRbfFile file, SizedPtr ticket, ReadOnlySpan<byte> combined, int metaLength, uint tag, bool pooled = true) {
        var buffer = new byte[ticket.Length];
        var frame = file.ReadFrame(ticket, buffer).Unwrap();
        Require(frame.PayloadAndMeta.SequenceEqual(combined) && frame.TailMetaLength == metaLength && frame.Tag == tag, "Caller-buffer frame mismatch.");
        if (pooled) {
            using var owned = file.ReadPooledFrame(ticket).Unwrap();
            Require(owned.PayloadAndMeta.SequenceEqual(combined) && owned.TailMetaLength == metaLength && owned.Tag == tag, "Pooled frame mismatch.");
        }
        var info = file.ReadFrameInfo(ticket).Unwrap();
        Require(info.Ticket == ticket && info.PayloadLength == combined.Length - metaLength && info.TailMetaLength == metaLength && info.Tag == tag, "Metadata mismatch.");
        var infoFrame = info.ReadFrame(buffer).Unwrap();
        Require(infoFrame.PayloadAndMeta.SequenceEqual(combined) && infoFrame.TailMetaLength == metaLength && infoFrame.Tag == tag, "Info caller-buffer frame mismatch.");
        using (var infoPooled = info.ReadPooledFrame().Unwrap()) {
            Require(infoPooled.PayloadAndMeta.SequenceEqual(combined) && infoPooled.TailMetaLength == metaLength && infoPooled.Tag == tag, "Info pooled frame mismatch.");
        }
        var meta = new byte[metaLength];
        Require(info.ReadTailMeta(meta).Unwrap().TailMeta.SequenceEqual(combined[^metaLength..]), "Info metadata phase mismatch.");
        Require(file.ReadTailMeta(ticket, meta).Unwrap().TailMeta.SequenceEqual(combined[^metaLength..]), "Direct metadata phase mismatch.");
        using var pooledMeta = info.ReadPooledTailMeta().Unwrap();
        Require(pooledMeta.TailMeta.SequenceEqual(combined[^metaLength..]), "Pooled info metadata mismatch.");
        using var directMeta = file.ReadPooledTailMeta(ticket).Unwrap();
        Require(directMeta.TailMeta.SequenceEqual(combined[^metaLength..]), "Pooled direct metadata mismatch.");
    }
}
