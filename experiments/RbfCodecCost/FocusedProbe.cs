using System.Diagnostics;
using System.Buffers.Binary;
using Atelia.Data.Hashing;

namespace RbfCodecCost;

internal static class FocusedProbe {
    private static ulong _consumer;
    public static object Run(string output) {
        var reader = new List<Measurement>();
        foreach (int length in new[] { 4096, 1048576, 16777216, PrototypeCodec.MaxFrameLength - 28 }) {
            byte[] plaintext = Corpus.Create(length, "random"), xor = new byte[length], add = new byte[length], target = new byte[length];
            uint expected = RollingCrc.CrcForward(plaintext);
            XorTransform.Copy(plaintext, xor, 1, 0); AddTransform.CopyWords(plaintext, add, 1, false);
            var variants = new[] {
                (Name: "copy-crc-key0", Action: (Func<ulong>)(() => { plaintext.CopyTo(target, 0); return RollingCrc.CrcForward(target); })),
                (Name: "copy-xor-vector-crc-key1", Action: (Func<ulong>)(() => { xor.CopyTo(target, 0); XorTransform.InPlace(target, 1, 0); return RollingCrc.CrcForward(target); })),
                (Name: "copy-xor-fused-crc-key1", Action: (Func<ulong>)(() => { xor.CopyTo(target, 0); return XorTransform.DecodeCoverageAndCrc(target, 1); })),
                (Name: "copy-sub-vector-crc-key1", Action: (Func<ulong>)(() => { add.CopyTo(target, 0); AddTransform.InPlaceWords(target, 1, true); return RollingCrc.CrcForward(target); })),
                (Name: "copy-sub-fused-crc-key1", Action: (Func<ulong>)(() => { add.CopyTo(target, 0); return AddTransform.DecodeCoverageAndCrcWords(target, 1); }))
            };
            foreach (var variant in variants) for (int i = 0; i < 3; i++) Correctness.Require(variant.Action() == expected, "Paired reader CRC");
            int iterations = Math.Max(1, 67108864 / length);
            var times = variants.ToDictionary(v => v.Name, _ => new double[7]);
            var allocated = variants.ToDictionary(v => v.Name, _ => 0L);
            for (int sample = 0; sample < 7; sample++)
                foreach (var variant in variants.Skip(sample % 5).Concat(variants.Take(sample % 5))) {
                    long allocationStart = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                    ulong checksum = 0;
                    for (int i = 0; i < iterations; i++) checksum ^= variant.Action();
                    long ticks = Stopwatch.GetTimestamp() - start;
                    allocated[variant.Name] += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                    _consumer ^= checksum;
                    times[variant.Name][sample] = ticks * (1e9 / Stopwatch.Frequency) / iterations;
                    Correctness.Require(target.AsSpan().SequenceEqual(plaintext), "Paired reader decoded bytes");
                }
            foreach (var variant in variants) {
                double[] values = times[variant.Name]; double[] sorted = values.Order().ToArray();
                reader.Add(new($"paired-reader-{length}", variant.Name, length, 1, variant.Name.EndsWith("key0") ? 0u : 1u,
                    iterations, values, sorted[3], sorted[0], sorted[^1], allocated[variant.Name] / (double)(7 * iterations)));
            }
            Console.WriteLine($"PAIRED READER {length}");
            GC.Collect();
        }
        File.WriteAllText(Path.Combine(output, "paired-reader.json"), System.Text.Json.JsonSerializer.Serialize(reader, Program.Json));
        object large = LargeFrame(output);
        return new { Reader = reader, LargeFrame = large };
    }

    private static object LargeFrame(string output) {
        int length = PrototypeCodec.MaxFrameLength - 28;
        byte[] combined = Corpus.Create(length, "dense301-late");
        const int metaLength = 65535;
        string path = Path.Combine(output, "max-frame-key301.rbf");
        long start = Stopwatch.GetTimestamp();
        var prepared = PrototypeCodec.Prepare([combined], metaLength, uint.MaxValue, KeyStrategy.ZeroThenOne);
        double prepareMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Correctness.Require(prepared.FrameLength == PrototypeCodec.MaxFrameLength && prepared.Key == 301, "Maximum frame with high Key");
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);
        byte[] header = BitConverter.GetBytes(PrototypeCodec.Fence); RandomAccess.Write(handle, header, 0);
        long offset = 4; var codec = new StreamCodec(new byte[1048576]);
        start = Stopwatch.GetTimestamp(); codec.Append(handle, prepared, ref offset);
        double writeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp(); RandomAccess.FlushToDisk(handle);
        double flushMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        byte[] target = new byte[prepared.FrameLength];
        var reads = new List<object>();
        foreach (bool fused in new[] { false, true }) {
            start = Stopwatch.GetTimestamp(); int calls = StreamCodec.ReadExactly(handle, target, 4); StreamCodec.DecodeAndCheck(target, fused);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Correctness.Require(target.AsSpan(4, length).SequenceEqual(combined), "Maximum frame full round-trip");
            reads.Add(new { Fused = fused, Milliseconds = ms, ReadCalls = calls, Bytes = prepared.FrameLength });
        }
        Span<byte> closure = stackalloc byte[8]; StreamCodec.ReadExactly(handle, closure, prepared.FrameLength);
        Correctness.Require(BinaryPrimitives.ReadUInt32LittleEndian(closure) == prepared.Key && BinaryPrimitives.ReadUInt32LittleEndian(closure[4..]) == PrototypeCodec.Fence, "Maximum-frame raw closure");
        Console.WriteLine($"MAX FRAME PASS {prepared.FrameLength} Key={prepared.Key}");
        return new { Artifact = path, PayloadAndMetaBytes = length, MetaBytes = metaLength, MetaPhase = (length - metaLength) & 3,
            FrameBytes = prepared.FrameLength, FileBytes = offset, Key = prepared.Key, BitmapBytes = prepared.BitmapBytes,
            WorkspaceBytes = 1048576, PrepareMilliseconds = prepareMs, WriteMilliseconds = writeMs,
            DurableFlushMilliseconds = flushMs, WriteCalls = codec.WriteCalls, Reads = reads,
            Evidence = "One real W: maximum-length full round-trip, not a repeated latency benchmark or production Builder/recovery acceptance" };
    }
}
