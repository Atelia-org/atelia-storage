using System.Buffers.Binary;
using System.Text.Json;

namespace RbfCodecCost;

/// <summary>CPU-only qualification of the 256B framing interval, including split words.</summary>
internal static class TinyKeyProbe {
    private static readonly KeyStrategy[] Strategies = [KeyStrategy.FullBitmap, KeyStrategy.ZeroThenOne,
        KeyStrategy.ZeroThenRandom, KeyStrategy.ZeroThenTinyBitmapRandom];

    internal static object Run(string output) {
        object correctness = Check(output);
        var rows = new List<object>();
        var workloads = new List<(int Length, string Pattern, int Chunk)> { (0, "zero", 0), (0, "footer-marker", 0) };
        foreach (int length in new[] { 31, 128, 232, 233, 4096 })
        foreach (string pattern in new[] { "zero", "random", "marker", "dense301" }) workloads.Add((length, pattern, 0));
        foreach (int length in new[] { 31, 232 })
        foreach (string pattern in new[] { "marker", "dense301" }) workloads.Add((length, pattern, 3));
        foreach (var (length, pattern, chunk) in workloads) {
            byte[] input = Corpus.Create(length, pattern == "footer-marker" ? "zero" : pattern);
            byte[][] chunks = Corpus.Split(input, chunk);
            uint tag = pattern == "footer-marker" ? PrototypeCodec.Fence : 11u;
            string workload = $"{length}/{pattern}/chunk-{chunk}";
            Console.WriteLine($"TINY CPU {workload}");
            var actions = Strategies.Select(strategy => (strategy.ToString(), (Func<ulong>)(() =>
                PrototypeCodec.Prepare(chunks, 0, tag, strategy).Key))).ToArray();
            foreach (Measurement row in RandomSearchProbe.MeasureRotated(workload, length, chunks.Length, actions, 7)) {
                PreparedFrame observation = PrototypeCodec.Prepare(chunks, 0, tag, Enum.Parse<KeyStrategy>(row.Operation));
                rows.Add(new { Measurement = new { row.Workload, row.Operation, row.Bytes, row.Chunks,
                    row.IterationsPerSample, row.NanosecondsPerOperation, row.MedianNs, row.MinNs, row.MaxNs,
                    row.AllocatedBytesPerOperation }, Observation = new { Scope = "One separate untimed preparation",
                    EscapePayloadBytes = observation.FrameLength - 4, EncodedBodyBytes = observation.FrameLength - 8,
                    observation.Key, observation.RandomDraws, observation.RandomSearches,
                    observation.BodyWordScanPasses, observation.BitmapBytes, observation.ScratchBytes } });
            }
        }
        var result = new { Schema = 1, Correctness = correctness, Cpu = rows,
            Method = "Seven rotated samples, tiering disabled; full Prepare including CRC/footer, common allocations and real RNG. CPU-only: no storage throughput claim. EscapePayload includes raw HeadLen but excludes both Fence and raw EscapeKey. Small branch only after Zero fails and FrameLength-4<=256." };
        File.WriteAllText(Path.Combine(output, "tiny-key.json"), JsonSerializer.Serialize(result, Program.Json));
        return result;
    }

    private static object Check(string output) {
        int frames = 0, small = 0, large = 0;
        var vectors = new List<object>();
        foreach (int length in new[] { 0, 1, 3, 4, 31, 64, 128, 229, 230, 231, 232, 233, 256, 4096 })
        foreach (string pattern in new[] { "zero", "random", "marker", "dense301" })
        foreach (int chunk in new[] { 0, 1, 3, 7 })
        foreach (uint tag in new[] { 11u, PrototypeCodec.Fence }) {
            byte[] input = Corpus.Create(length, pattern);
            byte[][] chunks = Corpus.Split(input, chunk);
            bool tiny = ((length + 3) & ~3) + 24 <= 256;
            Func<uint>? rng = tiny ? () => throw new Exception("Tiny path must not draw random keys") : null;
            PreparedFrame frame = PrototypeCodec.Prepare(chunks, Math.Min(length, 3), tag,
                KeyStrategy.ZeroThenTinyBitmapRandom, rng);
            Correctness.Require(frame.BitmapBytes == 0 && frame.ScratchBytes == 0, "No bitmap array in hybrid");
            if (tiny) {
                PreparedFrame expected = PrototypeCodec.Prepare(chunks, Math.Min(length, 3), tag, KeyStrategy.FullBitmap);
                Correctness.Require(frame.Key == expected.Key && frame.Key < 64 && frame.RandomDraws == 0,
                    "Tiny picks the independently generated minimum Key");
                small++;
            } else large++;
            byte[] body = new byte[frame.FrameLength - 8]; input.CopyTo(body, 0); frame.Footer.CopyTo(body, input.Length);
            for (int offset = 0; offset < body.Length; offset += 4)
                Correctness.Require((BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) ^ frame.Key) != PrototypeCodec.Fence,
                    "Independent flat aligned-word oracle");
            byte[] wire = new byte[frame.FrameLength + 4]; PrototypeCodec.Serialize(frame, wire);
            for (int offset = 0; offset < frame.FrameLength; offset += 4)
                Correctness.Require(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(offset)) != PrototypeCodec.Fence,
                    "Raw HeadLen / body / raw Key are marker-free");
            foreach (bool fused in new[] { false, true }) {
                byte[] decoded = wire.AsSpan(0, frame.FrameLength).ToArray();
                StreamCodec.DecodeAndCheck(decoded, fused);
                Correctness.Require(decoded.AsSpan(4, length).SequenceEqual(input), "Two checked-read CRC/bytes modes");
            }
            if (chunk == 0) vectors.Add(new { PayloadHex = Convert.ToHexString(input.AsSpan(0, length - frame.MetaLength)),
                MetaHex = Convert.ToHexString(input.AsSpan(length - frame.MetaLength)), Tag = tag, Key = frame.Key,
                WireHex = Convert.ToHexString(wire) });
            frames++;
        }
        // Large forbidden keys must not alias bits through C#'s masked shift count.
        byte[] high = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(high, PrototypeCodec.Fence);
        BinaryPrimitives.WriteUInt32LittleEndian(high.AsSpan(4), PrototypeCodec.Fence ^ 65u);
        PreparedFrame alias = PrototypeCodec.Prepare([high], 0, 11, KeyStrategy.ZeroThenTinyBitmapRandom,
            () => throw new Exception("Alias fixture must be tiny"));
        Correctness.Require(alias.Key == 1, "Forbidden Key65 must not mark Key1");
        // Exact interval boundary: 232 input bytes -> 256B, 233 -> 260B.
        foreach (int length in new[] { 232, 233 }) {
            int draws = 0;
            PreparedFrame frame = PrototypeCodec.Prepare([new byte[length]], 0, PrototypeCodec.Fence,
                KeyStrategy.ZeroThenTinyBitmapRandom, () => { draws++; return uint.MaxValue; });
            Correctness.Require(length == 232 ? draws == 0 && frame.Key < 64 : draws == 1 && frame.Key == uint.MaxValue,
                "Inclusive 256B interval cutoff and unchanged large random path");
        }
        File.WriteAllText(Path.Combine(output, "random-vectors.json"), JsonSerializer.Serialize(vectors, Program.Json));
        return new { Frames = frames, TinyFrames = small, LargerFrames = large, PythonVectors = vectors.Count,
            SplitWordOracle = true, MinimumKeyOracle = true, TwoCheckedReadModes = true,
            InclusiveInterval256Boundary = true, HighShiftAliasRejected = true, ZeroAndTinyNoRandomDraw = true };
    }
}
