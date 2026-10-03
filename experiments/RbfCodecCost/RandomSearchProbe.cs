using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace RbfCodecCost;

/// <summary>Isolated comparison: RNG and every search are timed; observation is a separate run.</summary>
internal static class RandomSearchProbe {
    private static readonly KeyStrategy[] Strategies = [KeyStrategy.FullBitmap, KeyStrategy.ZeroFirst,
        KeyStrategy.ZeroThenOne, KeyStrategy.ZeroThenRandom, KeyStrategy.ZeroThenRandom2Bitmap];
    private static ulong _consumer;

    public static object Run(string output, bool quick) {
        const int samples = 7;
        object correctness = RandomSearchCorrectness.Run(output);
        var cpu = new List<object>();
        int[] lengths = quick ? [31, 4096, 1048576] : [0, 31, 4096, 1048576, 16777216, PrototypeCodec.MaxFrameLength - 28];
        foreach (int length in lengths) {
            string[] patterns = length < 4 ? ["zero", "footer-marker"] : length < 1204 ? ["zero", "random", "marker"] :
                ["zero", "random", "marker", "dense301", "dense301-late", "random-fence-first", "random-fence-last"];
            foreach (string pattern in patterns) {
                byte[] input = Create(length, pattern);
                foreach (int chunkSize in (length is 1048576 or 16777216) && (pattern is "dense301-late" or "random-fence-last") ? new[] { 0, 65533 } : new[] { 0 }) {
                    byte[][] chunks = Corpus.Split(input, chunkSize);
                    uint tag = pattern == "footer-marker" ? PrototypeCodec.Fence : 11u;
                    string workload = $"{length}/{pattern}/chunk-{chunkSize}";
                    Console.WriteLine($"RANDOM CPU {workload}");
                    var actions = Strategies.Select(strategy => (strategy.ToString(), (Func<ulong>)(() =>
                        PrototypeCodec.Prepare(chunks, 0, tag, strategy).Key))).ToArray();
                    List<Measurement> measured = MeasureRotated(workload, length, chunks.Length, actions, samples);
                    foreach (var row in measured) {
                        var strategy = Enum.Parse<KeyStrategy>(row.Operation);
                        int observations = length > 16777216 ? 16 : 64;
                        var scanCounts = new SortedDictionary<int, int>();
                        var randomCounts = new SortedDictionary<int, int>();
                        long bitmapBytes = 0, randomDraws = 0;
                        int bitmapFrames = 0, zeros = 0;
                        var keys = new List<uint>();
                        for (int i = 0; i < observations; i++) {
                            PreparedFrame frame = PrototypeCodec.Prepare(chunks, 0, tag, strategy);
                            scanCounts[frame.BodyWordScanPasses] = scanCounts.GetValueOrDefault(frame.BodyWordScanPasses) + 1;
                            randomCounts[frame.RandomSearches] = randomCounts.GetValueOrDefault(frame.RandomSearches) + 1;
                            bitmapBytes += frame.BitmapBytes; randomDraws += frame.RandomDraws;
                            if (frame.BitmapBytes != 0) bitmapFrames++;
                            if (frame.Key == 0) zeros++;
                            keys.Add(frame.Key);
                        }
                        // Keys vary during the timed run; do not serialize Measurement's fixed Key field.
                        cpu.Add(new { Measurement = new { row.Workload, row.Operation, row.Bytes, row.Chunks,
                            row.IterationsPerSample, row.NanosecondsPerOperation, row.MedianNs, row.MinNs,
                            row.MaxNs, row.AllocatedBytesPerOperation, row.PayloadMiBPerSecond }, Observation = new {
                            Scope = "Separate untimed preparations, not the keys used in timing samples",
                            Frames = observations, ZeroKeys = zeros, UniqueKeys = keys.Distinct().Count(),
                            KeyMin = keys.Min(), KeyMax = keys.Max(), FirstKeys = keys.Take(8).ToArray(),
                            ScanPassesStarted = scanCounts, RandomSearches = randomCounts, RandomDraws = randomDraws,
                            BitmapFrames = bitmapFrames, BitmapBytesAllocated = bitmapBytes } });
                    }
                }
                GC.Collect();
            }
        }
        List<Measurement> rng = MeasureRotated("system-rng", 4, 1, [
            ("RandomNumberGenerator.Fill-uint32", (Func<ulong>)(() => PrototypeCodec.NextRandomKey())),
            ("fixed-uint-control", (Func<ulong>)(() => 0xB89E20D7u))], samples);
        var io = RunIO(output, samples, quick);
        object maximum = quick ? new { Skipped = true } : MaximumFrame(output);
        var result = new { Schema = 1, Correctness = correctness, Cpu = cpu, RNG = rng, IO = io, MaximumFrame = maximum,
            Method = "Seven sample rotations; real OS CSPRNG included in Prepare; no observation dictionaries in timed region. Buffered W: I/O with separate FlushToDisk; OS warm checked reads outside writer timing. No production implementation or deterministic random-loop time bound." };
        File.WriteAllText(Path.Combine(output, "random-search.json"), JsonSerializer.Serialize(result, Program.Json));
        return result;
    }

    internal static List<Measurement> MeasureRotated(string workload, int bytes, int chunks,
        (string Name, Func<ulong> Action)[] variants, int samples) {
        var counts = new int[variants.Length];
        for (int index = 0; index < variants.Length; index++) {
            var action = variants[index].Action;
            for (int i = 0; i < 3; i++) _consumer ^= action();
            long start = Stopwatch.GetTimestamp(); int calibrated = 0;
            do { _consumer ^= action(); calibrated++; }
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 20 && calibrated < 131072);
            counts[index] = Math.Clamp((int)Math.Ceiling(calibrated * 0.040 / Stopwatch.GetElapsedTime(start).TotalSeconds), 1, 131072);
        }
        double[][] times = variants.Select(_ => new double[samples]).ToArray();
        long[] allocated = new long[variants.Length];
        for (int sample = 0; sample < samples; sample++)
            for (int position = 0; position < variants.Length; position++) {
                int index = (sample + position) % variants.Length;
                long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp(); ulong checksum = 0;
                for (int i = 0; i < counts[index]; i++) checksum ^= variants[index].Action();
                long ticks = Stopwatch.GetTimestamp() - start;
                allocated[index] += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                _consumer ^= checksum;
                times[index][sample] = ticks * (1e9 / Stopwatch.Frequency) / counts[index];
            }
        return variants.Select((variant, index) => {
            double[] sorted = times[index].Order().ToArray();
            return new Measurement(workload, variant.Name, bytes, chunks, 0, counts[index], times[index],
                sorted[samples / 2], sorted[0], sorted[^1], allocated[index] / (double)(samples * counts[index]));
        }).ToList();
    }

    private static byte[] Create(int length, string pattern) {
        if (pattern == "footer-marker") return new byte[length];
        if (!pattern.StartsWith("random-fence-", StringComparison.Ordinal)) return Corpus.Create(length, pattern);
        byte[] bytes = Corpus.Create(length, "random");
        int offset = pattern.EndsWith("first", StringComparison.Ordinal) ? 0 : (length & ~3) - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), PrototypeCodec.Fence);
        return bytes;
    }

    private static List<object> RunIO(string output, int samples, bool quick) {
        var rows = new List<object>();
        int batchBytes = (quick ? 8 : 32) * 1048576;
        var workloads = quick ? new[] { (1048576, "dense301-late") } : new[] {
            (1048576, "marker"), (1048576, "dense301-late"), (1048576, "random-fence-last"), (16777216, "dense301-late") };
        byte[] header = BitConverter.GetBytes(PrototypeCodec.Fence);
        foreach (var (length, pattern) in workloads) {
            byte[] input = Create(length, pattern); byte[][] chunks = [input];
            int frames = batchBytes / length;
            var append = Strategies.ToDictionary(v => v, _ => new double[samples]);
            var flush = Strategies.ToDictionary(v => v, _ => new double[samples]);
            var calls = new Dictionary<KeyStrategy, long>();
            var paths = new Dictionary<KeyStrategy, string>();
            var selectedKeys = Strategies.ToDictionary(v => v, _ => new List<uint>());
            for (int sample = 0; sample < samples; sample++)
                for (int position = 0; position < Strategies.Length; position++) {
                    KeyStrategy strategy = Strategies[(sample + position) % Strategies.Length];
                    string path = Path.Combine(output, $"random-{length}-{pattern}-{strategy}-{sample}.rbf");
                    paths[strategy] = path;
                    using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);
                    RandomAccess.Write(handle, header, 0);
                    var codec = new StreamCodec(new byte[1048576]); long offset = 4;
                    // All per-frame Prepare allocations, RNG, scans, XOR and writes are included.
                    long start = Stopwatch.GetTimestamp();
                    for (int frame = 0; frame < frames; frame++) codec.Append(handle, PrototypeCodec.Prepare(chunks, 0, 11, strategy), ref offset);
                    append[strategy][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = Stopwatch.GetTimestamp(); RandomAccess.FlushToDisk(handle);
                    flush[strategy][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    calls[strategy] = codec.WriteCalls;
                    byte[] target = new byte[length + 28];
                    for (int frame = 0; frame < frames; frame++) {
                        StreamCodec.ReadExactly(handle, target, 4L + frame * (target.Length + 4L));
                        selectedKeys[strategy].Add(BinaryPrimitives.ReadUInt32LittleEndian(target.AsSpan(target.Length - 4)));
                        StreamCodec.DecodeAndCheck(target, false);
                        Correctness.Require(target.AsSpan(4, length).SequenceEqual(input), "Random I/O checked-read bytes");
                    }
                }
            foreach (KeyStrategy strategy in Strategies) rows.Add(new {
                Workload = $"{length}/{pattern}", Strategy = strategy.ToString(), FramesPerBatch = frames,
                UserBytesPerBatch = batchBytes, WorkspaceBytes = 1048576,
                AppendMilliseconds = append[strategy], FlushMilliseconds = flush[strategy],
                MedianAppendMs = IoProbe.Median(append[strategy]),
                MedianBatchDurableMs = IoProbe.Median(append[strategy].Zip(flush[strategy], (a, b) => a + b).ToArray()),
                WriteCallsPerBatch = calls[strategy], FileBytes = new FileInfo(paths[strategy]).Length,
                CheckedReadFrames = selectedKeys[strategy].Count, UniqueKeys = selectedKeys[strategy].Distinct().Count(),
                FirstKeys = selectedKeys[strategy].Take(8).ToArray(), Artifact = paths[strategy] });
            Console.WriteLine($"RANDOM IO {length}/{pattern}");
        }
        return rows;
    }

    private static object MaximumFrame(string output) {
        int length = PrototypeCodec.MaxFrameLength - 28;
        byte[] input = Create(length, "random-fence-last");
        PreparedFrame prepared = PrototypeCodec.Prepare([input], 65535, uint.MaxValue, KeyStrategy.ZeroThenRandom);
        string path = Path.Combine(output, "max-frame-random-key.rbf");
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);
        RandomAccess.Write(handle, BitConverter.GetBytes(PrototypeCodec.Fence), 0);
        var codec = new StreamCodec(new byte[1048576]); long offset = 4;
        codec.Append(handle, prepared, ref offset); RandomAccess.FlushToDisk(handle);
        byte[] target = new byte[prepared.FrameLength];
        StreamCodec.ReadExactly(handle, target, 4);
        uint recovered = BinaryPrimitives.ReadUInt32LittleEndian(target.AsSpan(target.Length - 8)) ^ (uint)target.Length;
        Correctness.Require(recovered == prepared.Key, "Maximum frame unique missing random Key");
        foreach (bool fused in new[] { false, true }) {
            StreamCodec.ReadExactly(handle, target, 4); StreamCodec.DecodeAndCheck(target, fused);
            Correctness.Require(target.AsSpan(4, length).SequenceEqual(input), "Maximum random frame full read");
        }
        return new { Artifact = path, FrameBytes = prepared.FrameLength, FileBytes = offset, Key = prepared.Key,
            prepared.RandomDraws, prepared.RandomSearches, prepared.BodyWordScanPasses, prepared.BitmapBytes,
            MissingKeyRecovered = recovered, FullReadModes = 2, Evidence = "Single maximum W: round-trip, not latency ranking or production recovery acceptance" };
    }
}
