using System.Buffers.Binary;
using System.Diagnostics;
using Atelia.Data;
using Atelia.Rbf;

namespace RbfCodecCost;

internal sealed record IoMeasurement(string Workload, string Operation, int UserBytesPerFrame, int FrameCount,
    uint Key, int WorkspaceBytes, double[] AppendMilliseconds, double[] DurableFlushMilliseconds,
    double MedianAppendMs, double MedianBatchDurableMs, long FileLength, long? WriteCallsPerBatch,
    long? ReadCallsPerBatch, long? ReadBytesPerBatch, double[]? ReadMilliseconds = null,
    string? Artifact = null);

internal static class IoProbe {
    public static List<IoMeasurement> Run(string output, int samples, bool quick) {
        var rows = new List<IoMeasurement>();
        string dataRoot = Path.Combine(output, "files"); Directory.CreateDirectory(dataRoot);
        int targetBatch = (quick ? 8 : 64) * 1024 * 1024;
        byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, PrototypeCodec.Fence);
        foreach (int length in quick ? new[] { 4096, 1048576 } : new[] { 4096, 1048576, 16777216 })
        foreach (string pattern in quick ? new[] { "random", "marker" } : new[] { "random", "marker", "dense301" }) {
            byte[] payload = Corpus.Create(length, pattern);
            byte[][] chunks = [payload];
            var prepared = PrototypeCodec.Prepare(chunks, 0, 11, KeyStrategy.ZeroFirst);
            int count = Math.Max(1, targetBatch / length);
            string workload = $"{length}/{pattern}";
            Console.WriteLine($"IO {workload} x {count}");
            var variants = new List<(string Name, KeyStrategy? Strategy, int Workspace)> {
                ("production-rbf1-append", null, 0),
                ("prototype-xor-ZeroFirst-64KiB", KeyStrategy.ZeroFirst, 65536),
                ("prototype-xor-SmallBitmap-64KiB", KeyStrategy.SmallBitmap, 65536),
                ("prototype-xor-ZeroThenOne-64KiB", KeyStrategy.ZeroThenOne, 65536)
            };
            if (!quick && length == 1048576 && pattern == "marker") {
                variants.Add(("prototype-xor-ZeroThenOne-16KiB", KeyStrategy.ZeroThenOne, 16384));
                variants.Add(("prototype-xor-ZeroThenOne-256KiB", KeyStrategy.ZeroThenOne, 262144));
                variants.Add(("prototype-xor-ZeroThenOne-1MiB", KeyStrategy.ZeroThenOne, 1048576));
            }
            if (!quick && length == 1048576) variants.Add(("production-rbf1-builder-64KiB-feeds", null, 0));
            var writes = variants.ToDictionary(v => v.Name, _ => new double[samples]);
            var flushes = variants.ToDictionary(v => v.Name, _ => new double[samples]);
            var paths = new Dictionary<string, string>();
            var calls = new Dictionary<string, long>();
            // Rotate order by sample. Every append is sequential and each sample has a unique file.
            for (int sample = 0; sample < samples; sample++) foreach (var variant in variants.Skip(sample % variants.Count).Concat(variants.Take(sample % variants.Count))) {
                string path = Path.Combine(dataRoot, $"{length}-{pattern}-{variant.Name}-{sample}.rbf");
                paths[variant.Name] = path;
                if (variant.Strategy is null) {
                    using IRbfFile file = RbfFile.CreateNew(path);
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < count; i++) {
                        if (variant.Name.Contains("builder")) {
                            using var builder = file.BeginAppend();
                            var writer = builder.PayloadAndMeta;
                            for (int cursor = 0; cursor < payload.Length; cursor += 65536) {
                                int bytes = Math.Min(65536, payload.Length - cursor);
                                payload.AsSpan(cursor, bytes).CopyTo(writer.GetSpan(bytes)); writer.Advance(bytes);
                            }
                            var result = builder.EndAppend(11); if (!result.IsSuccess) throw new Exception(result.Error!.ToString());
                        } else {
                            var result = file.Append(11, payload); if (!result.IsSuccess) throw new Exception(result.Error!.ToString());
                        }
                    }
                    writes[variant.Name][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = Stopwatch.GetTimestamp(); file.DurableFlush();
                    flushes[variant.Name][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                } else {
                    using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);
                    RandomAccess.Write(handle, header, 0);
                    var codec = new StreamCodec(new byte[variant.Workspace]);
                    long offset = 4, start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < count; i++) codec.Append(handle, PrototypeCodec.Prepare(chunks, 0, 11, variant.Strategy.Value), ref offset);
                    writes[variant.Name][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = Stopwatch.GetTimestamp(); RandomAccess.FlushToDisk(handle);
                    flushes[variant.Name][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Correctness.Require(offset == 4L + (prepared.FrameLength + 4L) * count, "Stream write length");
                    calls[variant.Name] = codec.WriteCalls;
                }
            }
            foreach (var variant in variants) {
                double[] total = writes[variant.Name].Zip(flushes[variant.Name], (a, b) => a + b).ToArray();
                rows.Add(new(workload, variant.Name, length, count, variant.Strategy is null ? 0 : prepared.Key, variant.Workspace,
                    writes[variant.Name], flushes[variant.Name], Median(writes[variant.Name]), Median(total),
                    new FileInfo(paths[variant.Name]).Length, variant.Strategy is null ? null : calls[variant.Name], null, null,
                    Artifact: paths[variant.Name]));
            }
            // Same deterministic shuffled ticket order, OS warm cache, RBF1 cache disabled.
            int[] order = Enumerable.Range(0, count).ToArray(); new Random(731).Shuffle(order);
            byte[] destination = new byte[prepared.FrameLength];
            string productionPath = paths["production-rbf1-append"];
            string prototypePath = paths["prototype-xor-ZeroThenOne-64KiB"];
            using (IRbfFile file = RbfFile.OpenReadOnlyExisting(productionPath, RbfCacheMode.Off))
            using (var handle = File.OpenHandle(prototypePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess)) {
                int oldLength = prepared.FrameLength - 4;
                Action production = () => {
                    foreach (int index in order) {
                        var ticket = SizedPtr.Create(4L + index * (oldLength + 4L), oldLength);
                        var result = file.ReadFrame(ticket, destination);
                        if (!result.IsSuccess) throw new InvalidDataException("Production checked-read");
                    }
                };
                Action Prototype(bool fused) => () => {
                        foreach (int index in order) {
                            StreamCodec.ReadExactly(handle, destination, 4L + index * (prepared.FrameLength + 4L));
                            StreamCodec.DecodeAndCheck(destination, fused);
                        }
                };
                var readVariants = new[] { (Name: "production-rbf1-read-cacheOff-warm", Action: production),
                    (Name: "prototype-xor-read-vector-crc-warm", Action: Prototype(false)),
                    (Name: "prototype-xor-read-fused-warm", Action: Prototype(true)) };
                var times = readVariants.ToDictionary(v => v.Name, _ => new double[samples]);
                foreach (var variant in readVariants) variant.Action(); // Untimed complete-batch OS/JIT warmup.
                for (int sample = 0; sample < samples; sample++)
                    foreach (var variant in readVariants.Skip(sample % 3).Concat(readVariants.Take(sample % 3))) {
                        long start = Stopwatch.GetTimestamp(); variant.Action();
                        times[variant.Name][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        if (!destination.AsSpan(4, payload.Length).SequenceEqual(payload)) throw new InvalidDataException("Prototype round-trip");
                    }
                long productionCalls, productionBytes;
                using (var metrics = RbfReadMetrics.Begin()) {
                    production(); var snapshot = metrics.Snapshot(); productionCalls = snapshot.RawReadCalls; productionBytes = snapshot.RawReturnedBytes;
                }
                long prototypeCalls = 0;
                foreach (int index in order) prototypeCalls += StreamCodec.ReadExactly(handle, destination, 4L + index * (prepared.FrameLength + 4L));
                rows.Add(new(workload, readVariants[0].Name, length, count, 0, 0, [], [], 0, 0,
                    new FileInfo(productionPath).Length, null, productionCalls, productionBytes, times[readVariants[0].Name], productionPath));
                foreach (var variant in readVariants.Skip(1)) rows.Add(new(workload, variant.Name, length, count, prepared.Key, 0, [], [], 0, 0,
                    new FileInfo(prototypePath).Length, null, prototypeCalls, (long)prepared.FrameLength * count, times[variant.Name], prototypePath));
            }
        }
        return rows;
    }

    internal static double Median(double[] input) { var sorted = input.Order().ToArray(); return sorted[sorted.Length / 2]; }
}
