using Atelia.Data.Hashing;

namespace RbfCodecCost;

internal static class CpuProbe {
    public static List<Measurement> Run(int samples, bool quick) {
        var results = new List<Measurement>();
        int[] lengths = quick ? [31, 4096, 1024 * 1024] : [0, 31, 4096, 1024 * 1024, 16 * 1024 * 1024, PrototypeCodec.MaxFrameLength - 28];
        foreach (int length in lengths) {
            string[] patterns = length < 1204 ? ["zero", "random"] : ["zero", "random", "marker", "dense301", "dense301-late"];
            // Only one near-limit allocation at a time. Chunk copy cases use 16MiB, not a second max-size corpus.
            foreach (string pattern in patterns) {
                byte[] payload = Corpus.Create(length, pattern);
                foreach (int chunkSize in length is > 65533 and <= 16777216 ? new[] { 0, 65533 } : new[] { 0 }) {
                    byte[][] chunks = Corpus.Split(payload, chunkSize);
                    string workload = $"{length}/{pattern}/chunk-{chunkSize}";
                    Console.WriteLine($"CPU {workload}");
                    PreparedFrame prepared = PrototypeCodec.Prepare(chunks, 0, 11, KeyStrategy.FullBitmap);
                    results.Add(Timing.Measure(workload, "existing-crc-only", length, chunks.Length, prepared.Key, () => {
                        uint crc = RollingCrc.DefaultInitValue;
                        foreach (byte[] chunk in chunks) crc = RollingCrc.CrcForward(crc, chunk);
                        Span<byte> padding = stackalloc byte[3]; padding.Clear();
                        return RollingCrc.CrcForward(crc, padding[..prepared.PaddingLength]) ^ RollingCrc.DefaultFinalXor;
                    }, samples));
                    foreach (KeyStrategy strategy in new[] { KeyStrategy.FullBitmap, KeyStrategy.SmallBitmap, KeyStrategy.ZeroFirst, KeyStrategy.ZeroThenOne }) {
                        var reference = PrototypeCodec.Prepare(chunks, 0, 11, strategy);
                        if (reference.Key != prepared.Key) throw new InvalidDataException("Key strategy divergence");
                        results.Add(Timing.Measure(workload, $"prepare-{strategy}", length, chunks.Length, prepared.Key,
                            () => PrototypeCodec.Prepare(chunks, 0, 11, strategy).Key, samples, reference.ScratchBytes));
                    }
                    byte[] destination = new byte[prepared.FrameLength + 4];
                    results.Add(Timing.Measure(workload, "serialize-xor-vector", length, chunks.Length, prepared.Key,
                        () => { PrototypeCodec.Serialize(prepared, destination); return destination[4]; }, samples));
                    // Compare transformation kernels on equal, aligned spans. These are not whole codecs.
                    int aligned = payload.Length & ~3;
                    foreach (XorMode mode in Enum.GetValues<XorMode>()) {
                        results.Add(Timing.Measure(workload, $"xor-copy-{mode}-key1", aligned, 1, 1,
                            () => { XorTransform.Copy(payload.AsSpan(0, aligned), destination, 1, 0, mode); return destination[0]; }, samples));
                        results.Add(Timing.Measure(workload, $"add-copy-{mode}-key1", aligned, 1, 1,
                            () => { AddTransform.CopyWords(payload.AsSpan(0, aligned), destination, 1, false, mode); return destination[0]; }, samples));
                    }
                    // Owned-buffer acquisition cost is intentionally included equally in all three checked-read CPU paths.
                    results.Add(Timing.Measure(workload, "copy-plus-crc-key0", aligned, 1, 0, () => {
                        payload.AsSpan(0, aligned).CopyTo(destination);
                        return RollingCrc.CrcForward(destination.AsSpan(0, aligned));
                    }, samples));
                    results.Add(Timing.Measure(workload, "copy-plus-xor-vector-plus-crc-key1", aligned, 1, 1, () => {
                        payload.AsSpan(0, aligned).CopyTo(destination);
                        XorTransform.InPlace(destination.AsSpan(0, aligned), 1, 0);
                        return RollingCrc.CrcForward(destination.AsSpan(0, aligned));
                    }, samples));
                    results.Add(Timing.Measure(workload, "copy-plus-xor-fused-crc-key1", aligned, 1, 1, () => {
                        payload.AsSpan(0, aligned).CopyTo(destination);
                        return XorTransform.DecodeCoverageAndCrc(destination.AsSpan(0, aligned), 1);
                    }, samples));
                    results.Add(Timing.Measure(workload, "copy-plus-sub-vector-plus-crc-key1", aligned, 1, 1, () => {
                        payload.AsSpan(0, aligned).CopyTo(destination);
                        AddTransform.InPlaceWords(destination.AsSpan(0, aligned), 1, true);
                        return RollingCrc.CrcForward(destination.AsSpan(0, aligned));
                    }, samples));
                    results.Add(Timing.Measure(workload, "copy-plus-sub-fused-crc-key1", aligned, 1, 1, () => {
                        payload.AsSpan(0, aligned).CopyTo(destination);
                        return AddTransform.DecodeCoverageAndCrcWords(destination.AsSpan(0, aligned), 1);
                    }, samples));
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        return results;
    }
}
