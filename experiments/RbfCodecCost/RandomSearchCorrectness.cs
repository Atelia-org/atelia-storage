using System.Buffers.Binary;
using System.Text.Json;

namespace RbfCodecCost;

internal static class RandomSearchCorrectness {
    public static object Run(string output) {
        int frames = 0, corruptions = 0;
        var vectors = new List<object>();
        foreach (int length in new[] { 0, 1, 2, 3, 4, 7, 31, 257, 1204, 4093, 65539 })
        foreach (string pattern in new[] { "zero", "random", "marker", "dense301" })
        foreach (int meta in new[] { 0, Math.Min(length, 3), Math.Min(length, 65535) }.Distinct())
        foreach (int chunkSize in new[] { 0, 1, 3, 4093 }) {
            if (length > 4096 && chunkSize is 1 or 3) continue;
            byte[] input = Corpus.Create(length, pattern);
            byte[][] chunks = Corpus.Split(input, chunkSize);
            foreach (KeyStrategy strategy in new[] { KeyStrategy.ZeroThenRandom, KeyStrategy.ZeroThenRandom2Bitmap }) {
                PreparedFrame prepared = PrototypeCodec.Prepare(chunks, meta, PrototypeCodec.Fence, strategy);
                byte[] wire = Check(prepared, input);
                frames++;
                byte[] badKey = wire.AsSpan(0, prepared.FrameLength).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(badKey.AsSpan(badKey.Length - 4), PrototypeCodec.Fence);
                Reject(() => StreamCodec.DecodeAndCheck(badKey, false)); corruptions++;
                byte[] badTrailer = wire.AsSpan(0, prepared.FrameLength).ToArray();
                badTrailer[prepared.FrameLength - 20] ^= 1;
                Reject(() => StreamCodec.DecodeAndCheck(badTrailer, false)); corruptions++;
                if (length > 0) {
                    byte[] badPayload = wire.AsSpan(0, prepared.FrameLength).ToArray(); badPayload[4] ^= 1;
                    Reject(() => StreamCodec.DecodeAndCheck(badPayload, false)); corruptions++;
                }
                if (chunkSize == 0 && length <= 4093 && meta <= 3) vectors.Add(new {
                    PayloadHex = Convert.ToHexString(input.AsSpan(0, length - meta)),
                    MetaHex = Convert.ToHexString(input.AsSpan(length - meta)), Tag = PrototypeCodec.Fence,
                    Key = prepared.Key, WireHex = Convert.ToHexString(wire) });
            }
        }
        // Key0 neither allocates a bitmap nor invokes the RNG.
        PreparedFrame zero = PrototypeCodec.Prepare([new byte[31]], 0, 11, KeyStrategy.ZeroThenRandom,
            () => throw new Exception("Key0 must not consume RNG"));
        Correctness.Require(zero.Key == 0 && zero.RandomDraws == 0 && zero.BitmapBytes == 0, "Zero speculation");
        byte[] dense = Corpus.Create(4096, "dense301"); byte[][] split = Corpus.Split(dense, 3);
        var candidates = new Queue<uint>([0, PrototypeCodec.Fence, 1, 2, 3, 4, uint.MaxValue]);
        PreparedFrame loop = PrototypeCodec.Prepare(split, 3, 11, KeyStrategy.ZeroThenRandom, () => candidates.Dequeue());
        Correctness.Require(loop.Key == uint.MaxValue && loop.RandomDraws == 7 && loop.RandomSearches == 5 &&
            loop.BodyWordScanPasses == 6 && loop.BitmapBytes == 0 && candidates.Count == 0, "Loop forced failures / invalid draws / high Key");
        Check(loop, dense);
        candidates = new Queue<uint>([1, 2]);
        PreparedFrame fallback = PrototypeCodec.Prepare(split, 3, 11, KeyStrategy.ZeroThenRandom2Bitmap, () => candidates.Dequeue());
        Correctness.Require(fallback.Key == 301 && fallback.RandomSearches == 2 && fallback.BodyWordScanPasses == 4 &&
            fallback.BitmapBytes > 0 && candidates.Count == 0, "Two random failures then bitmap");
        Check(fallback, dense);
        // Misaligned Fence bytes are not a structural word. Footer tag remains independent.
        byte[] unaligned = [0, 0x52, 0x42, 0x46, 0x32, 0, 0, 0];
        PreparedFrame aligned = PrototypeCodec.Prepare(Corpus.Split(unaligned, 3), 1, 11, KeyStrategy.ZeroThenRandom,
            () => throw new Exception("Sliding Fence bytes must not prohibit Key0"));
        Correctness.Require(aligned.Key == 0, "Word alignment, not sliding windows"); Check(aligned, unaligned);
        // The final tag in the footer can be the only word forbidding Key0.
        candidates = new Queue<uint>([uint.MaxValue]);
        PreparedFrame footer = PrototypeCodec.Prepare([new byte[7]], 1, PrototypeCodec.Fence,
            KeyStrategy.ZeroThenRandom, () => candidates.Dequeue());
        Correctness.Require(footer.Key == uint.MaxValue && footer.RandomSearches == 1, "Footer included in key search");
        byte[] footerWire = Check(footer, new byte[7]);
        vectors.Add(new { PayloadHex = Convert.ToHexString(new byte[6]), MetaHex = "00", Tag = PrototypeCodec.Fence,
            Key = footer.Key, WireHex = Convert.ToHexString(footerWire) });
        File.WriteAllText(Path.Combine(output, "random-vectors.json"), JsonSerializer.Serialize(vectors, Program.Json));
        return new { Frames = frames, CorruptionRejections = corruptions, PythonVectors = vectors.Count,
            ForcedLoopFailures = 4, InvalidDrawsRejected = 2, ForcedBitmapFallback = true, ZeroRNGCalls = true,
            AlignmentOracle = true, FooterOnlyForbiddenZero = true, HighUintKey = true, MissingKeyReconstruction = true };
    }

    private static byte[] Check(PreparedFrame prepared, byte[] input) {
        // Independent scalar flat-body oracle, not the scan/carry implementation.
        byte[] plaintext = new byte[prepared.FrameLength - 8]; input.CopyTo(plaintext, 0);
        prepared.Footer.CopyTo(plaintext, input.Length);
        Correctness.Require(prepared.Key != PrototypeCodec.Fence, "Raw Key is not Fence");
        for (int i = 0; i < plaintext.Length; i += 4)
            Correctness.Require((BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(i)) ^ prepared.Key) != PrototypeCodec.Fence,
                "Scalar full-body forbidden Key oracle");
        byte[] wire = new byte[prepared.FrameLength + 4]; PrototypeCodec.Serialize(prepared, wire);
        for (int i = 0; i < prepared.FrameLength; i += 4)
            Correctness.Require(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(i)) != PrototypeCodec.Fence, "Marker-free frame");
        Correctness.Require((BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(prepared.FrameLength - 8)) ^
            BinaryPrimitives.ReadUInt32LittleEndian(wire)) == prepared.Key, "Random high Key uniquely reconstructed");
        foreach (bool fused in new[] { false, true }) {
            byte[] owned = wire.AsSpan(0, prepared.FrameLength).ToArray(); StreamCodec.DecodeAndCheck(owned, fused);
            Correctness.Require(owned.AsSpan(4, input.Length).SequenceEqual(input), "Random decoded bytes / CRC");
        }
        return wire;
    }

    private static void Reject(Action action) {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Corruption was not rejected");
    }
}
