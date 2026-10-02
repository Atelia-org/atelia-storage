using System.Diagnostics;

namespace RbfCodecCost;

internal sealed record Measurement(string Workload, string Operation, int Bytes, int Chunks, uint Key,
    int IterationsPerSample, double[] NanosecondsPerOperation, double MedianNs,
    double MinNs, double MaxNs, double AllocatedBytesPerOperation, long ScratchBytes = 0) {
    public double? PayloadMiBPerSecond => Bytes == 0 ? null : Bytes / (1024d * 1024) / (MedianNs / 1e9);
}

internal static class Timing {
    private static ulong _consumer;
    public static Measurement Measure(string workload, string operation, int bytes, int chunks, uint key,
        Func<ulong> action, int samples, long scratchBytes = 0) {
        // Tiered compilation is disabled by the runner. Calibrate independently of recorded samples.
        for (int i = 0; i < 3; i++) _consumer ^= action();
        long start = Stopwatch.GetTimestamp();
        int calibrated = 0;
        do { _consumer ^= action(); calibrated++; }
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 25 && calibrated < 262144);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        int count = Math.Clamp((int)Math.Ceiling(calibrated * 0.055 / seconds), 1, 262144);
        double[] values = new double[samples];
        long allocated = 0;
        for (int sample = 0; sample < samples; sample++) {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            ulong checksum = 0;
            for (int i = 0; i < count; i++) checksum ^= action();
            long ticks = Stopwatch.GetTimestamp() - start;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            _consumer ^= checksum;
            values[sample] = ticks * (1e9 / Stopwatch.Frequency) / count;
        }
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return new(workload, operation, bytes, chunks, key, count, values, sorted[sorted.Length / 2],
            sorted[0], sorted[^1], allocated / (double)(samples * count), scratchBytes);
    }
}

internal static class Corpus {
    public static byte[] Create(int length, string pattern) {
        byte[] bytes = new byte[length];
        if (pattern == "zero") return bytes;
        uint state = 0xA71393F5;
        for (int i = 0; i + 4 <= length; i += 4) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            uint word = pattern switch {
                "marker" => PrototypeCodec.Fence,
                "dense301" => (i / 4 < 301) ? PrototypeCodec.Fence ^ (uint)(i / 4) : 0u,
                "dense301-late" => (i >= (length & ~3) - 1204) ? PrototypeCodec.Fence ^ (uint)((i - ((length & ~3) - 1204)) / 4) : 0u,
                "random" => state,
                _ => throw new ArgumentException(pattern)
            };
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i), word);
        }
        for (int i = length & ~3; i < length; i++) bytes[i] = (byte)(state >> ((i & 3) * 8));
        return bytes;
    }

    public static byte[][] Split(byte[] bytes, int chunkSize) {
        if (chunkSize <= 0 || chunkSize >= bytes.Length) return [bytes];
        var chunks = new List<byte[]>();
        for (int i = 0; i < bytes.Length; i += chunkSize) chunks.Add(bytes.AsSpan(i, Math.Min(chunkSize, bytes.Length - i)).ToArray());
        return chunks.ToArray();
    }
}
