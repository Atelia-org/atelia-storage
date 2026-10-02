using System.Buffers.Binary;
using System.Numerics;
using Atelia.Data.Hashing;

namespace RbfCodecCost;

internal static class Correctness {
    public static object Run(string output) {
        int transformCases = 0, frames = 0, corruptions = 0, additionFrames = 0;
        uint[] keys = [0, 1, 301, 0x01020304, PrototypeCodec.Fence, uint.MaxValue];
        var vectors = new List<object>();
        foreach (uint key in keys) foreach (int phase in new[] { 0, 1, 2, 3, int.MaxValue })
        foreach (int length in Enumerable.Range(0, Vector<byte>.Count * 2 + 8)) {
            byte[] source = Corpus.Create(length, "random");
            byte[] expected = source.Select((value, i) => (byte)(value ^ (byte)(key >> (int)(((phase + (long)i) & 3) * 8)))).ToArray();
            foreach (XorMode mode in Enum.GetValues<XorMode>()) {
                byte[] guarded = Enumerable.Repeat((byte)0xC7, length + 11).ToArray();
                XorTransform.Copy(source, guarded.AsSpan(5, length), key, phase, mode);
                Require(guarded.AsSpan(5, length).SequenceEqual(expected), "XOR scalar oracle");
                Require(guarded.AsSpan(0, 5).IndexOfAnyExcept((byte)0xC7) < 0 && guarded.AsSpan(5 + length).IndexOfAnyExcept((byte)0xC7) < 0, "XOR guards");
                XorTransform.InPlace(guarded.AsSpan(5, length), key, phase, mode);
                Require(guarded.AsSpan(5, length).SequenceEqual(source), "XOR round-trip");
                foreach (int shift in new[] { -3, -1, 0, 1, 3 }) {
                    byte[] overlap = new byte[length + 16]; source.CopyTo(overlap, 6);
                    XorTransform.Copy(overlap.AsSpan(6, length), overlap.AsSpan(6 + shift, length), key, phase, mode);
                    Require(overlap.AsSpan(6 + shift, length).SequenceEqual(expected), "XOR overlap");
                }
                transformCases++;
            }
            byte[] fused = expected.ToArray();
            uint crc = XorTransform.DecodeCoverageAndCrc(fused, key, phase);
            Require(fused.AsSpan().SequenceEqual(source) && crc == RollingCrc.CrcForward(source), "Fused XOR oracle");
        }
        foreach (uint key in keys) foreach (int length in Enumerable.Range(0, 35).Select(i => i * 4)) {
            byte[] source = Corpus.Create(length, "random");
            if (length >= 8) { BinaryPrimitives.WriteUInt32LittleEndian(source, uint.MaxValue); BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(4), 0xFF); }
            byte[] expected = new byte[length];
            for (int i = 0; i < length; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(i), unchecked(BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(i)) + key));
            foreach (XorMode mode in Enum.GetValues<XorMode>()) {
                byte[] guarded = Enumerable.Repeat((byte)0xC7, length + 11).ToArray();
                AddTransform.CopyWords(source, guarded.AsSpan(5, length), key, false, mode);
                Require(guarded.AsSpan(5, length).SequenceEqual(expected), "Add lane oracle");
                Require(guarded.AsSpan(0, 5).IndexOfAnyExcept((byte)0xC7) < 0 && guarded.AsSpan(5 + length).IndexOfAnyExcept((byte)0xC7) < 0, "Add guards");
                AddTransform.InPlaceWords(guarded.AsSpan(5, length), key, true, mode);
                Require(guarded.AsSpan(5, length).SequenceEqual(source), "Add round-trip");
                foreach (int shift in new[] { -3, -1, 0, 1, 3 }) {
                    byte[] overlap = new byte[length + 16]; source.CopyTo(overlap, 6);
                    AddTransform.CopyWords(overlap.AsSpan(6, length), overlap.AsSpan(6 + shift, length), key, false, mode);
                    Require(overlap.AsSpan(6 + shift, length).SequenceEqual(expected), "Add overlap");
                }
                transformCases++;
            }
            byte[] fused = expected.ToArray();
            uint crc = AddTransform.DecodeCoverageAndCrcWords(fused, key);
            Require(fused.AsSpan().SequenceEqual(source) && crc == RollingCrc.CrcForward(source), "Fused sub oracle");
        }
        foreach (int length in new[] { 0, 1, 2, 3, 4, 7, 31, 257, 1204, 4093, 65539, 131073 })
        foreach (string pattern in new[] { "zero", "random", "marker", "dense301" })
        foreach (int meta in new[] { 0, Math.Min(length, 3), Math.Min(length, 65535) }.Distinct())
        foreach (int chunkSize in new[] { 0, 1, 3, 4093 }) {
            // Tiny chunks are important at boundaries but deliberately bounded for large frames.
            if (length > 4096 && chunkSize is 1 or 3) continue;
            byte[] payloadAndMeta = Corpus.Create(length, pattern);
            byte[][] chunks = Corpus.Split(payloadAndMeta, chunkSize);
            PreparedFrame canonical = PrototypeCodec.Prepare(chunks, meta, uint.MaxValue, KeyStrategy.FullBitmap);
            byte[] wire = new byte[canonical.FrameLength + 4]; PrototypeCodec.Serialize(canonical, wire);
            foreach (KeyStrategy strategy in new[] { KeyStrategy.FullBitmap, KeyStrategy.SmallBitmap, KeyStrategy.ZeroFirst, KeyStrategy.ZeroThenOne }) {
                PreparedFrame candidate = PrototypeCodec.Prepare(chunks, meta, uint.MaxValue, strategy);
                byte[] compare = new byte[wire.Length]; PrototypeCodec.Serialize(candidate, compare);
                Require(compare.AsSpan().SequenceEqual(wire), "Canonical wire across strategies/chunks");
            }
            Require(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(canonical.FrameLength)) == PrototypeCodec.Fence, "Final Fence");
            for (int i = 0; i < canonical.FrameLength; i += 4) Require(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(i)) != PrototypeCodec.Fence, "Marker-free aligned frame");
            uint encodedTailLength = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(canonical.FrameLength - 8));
            Require((encodedTailLength ^ (uint)canonical.FrameLength) == canonical.Key, "XOR unique missing Key");
            foreach (bool fused in new[] { false, true }) {
                byte[] owned = wire.AsSpan(0, canonical.FrameLength).ToArray(); StreamCodec.DecodeAndCheck(owned, fused);
                Require(owned.AsSpan(4, length).SequenceEqual(payloadAndMeta), "Checked-read original bytes");
                if (length > 0) {
                    byte[] broken = wire.AsSpan(0, canonical.FrameLength).ToArray(); broken[4] ^= 1;
                    Reject(() => StreamCodec.DecodeAndCheck(broken, fused), "Payload mutation"); corruptions++;
                }
                byte[] badTrailer = wire.AsSpan(0, canonical.FrameLength).ToArray(); badTrailer[canonical.FrameLength - 20] ^= 1;
                Reject(() => StreamCodec.DecodeAndCheck(badTrailer, fused), "Trailer mutation"); corruptions++;
            }
            byte[] addWire = EncodeAddition(canonical);
            additionFrames++;
            frames++;
            if (chunkSize == 0 && length <= 4093 && meta <= 3) vectors.Add(new {
                PayloadHex = Convert.ToHexString(payloadAndMeta.AsSpan(0, length - meta)),
                MetaHex = Convert.ToHexString(payloadAndMeta.AsSpan(length - meta)), Tag = uint.MaxValue,
                XorWireHex = Convert.ToHexString(wire), AdditionWireHex = Convert.ToHexString(addWire)
            });
        }
        uint suffixA = unchecked(0x000000FFu + 1u) >> 8, suffixB = unchecked(0x00000100u + 1u) >> 8;
        Require(suffixA == suffixB && (0xFFu >> 8) != (0x100u >> 8), "Addition partial-word ambiguity");
        int parameterRejections = CheckParameterGuards();
        File.WriteAllText(Path.Combine(output, "vectors.json"), System.Text.Json.JsonSerializer.Serialize(vectors, Program.Json));
        return new { TransformCases = transformCases, Frames = frames, CorruptionRejections = corruptions,
            AdditionFrames = additionFrames, PythonVectors = vectors.Count, ParameterRejections = parameterRejections,
            AdditionPartialWordAmbiguity = true };
    }

    private static byte[] EncodeAddition(PreparedFrame frame) {
        byte[] wire = new byte[frame.FrameLength + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)frame.FrameLength);
        int cursor = 4;
        foreach (byte[] chunk in frame.Chunks) { chunk.CopyTo(wire, cursor); cursor += chunk.Length; }
        frame.Footer.CopyTo(wire, cursor);
        int words = (frame.FrameLength - 8) / 4;
        var forbidden = new HashSet<uint>();
        for (int i = 4; i < frame.FrameLength - 4; i += 4) forbidden.Add(unchecked(PrototypeCodec.Fence - BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(i))));
        uint key = 0; while (forbidden.Contains(key)) key++;
        Require(key <= words, "Addition existence bound");
        AddTransform.InPlaceWords(wire.AsSpan(4, frame.FrameLength - 8), key, false);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(frame.FrameLength - 4), key);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(frame.FrameLength), PrototypeCodec.Fence);
        for (int i = 0; i < frame.FrameLength; i += 4) Require(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(i)) != PrototypeCodec.Fence, "Addition marker-free");
        Require(unchecked(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(frame.FrameLength - 8)) - (uint)frame.FrameLength) == key, "Addition unique missing Key");
        byte[] decoded = wire.AsSpan(4, frame.FrameLength - 8).ToArray();
        AddTransform.InPlaceWords(decoded, key, true);
        int coverage = frame.FrameLength - 28;
        Require(BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(coverage)) == RollingCrc.CrcForward(decoded.AsSpan(0, coverage)), "Addition PayloadCRC");
        Require(BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(coverage + 4)) == RollingCrc.CrcBackward(decoded.AsSpan(coverage + 8, 12)), "Addition TrailerCRC");
        return wire;
    }

    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception($"Did not reject: {message}"); }
    private static int CheckParameterGuards() {
        int count = 0;
        foreach (Action action in new Action[] {
            () => XorTransform.Copy(new byte[4], new byte[3], 1, 0),
            () => XorTransform.Copy(new byte[4], new byte[4], 0, -1),
            () => XorTransform.InPlace(new byte[4], 0, 0, (XorMode)999),
            () => XorTransform.DecodeCoverageAndCrc(new byte[4], 0, -1),
            () => AddTransform.CopyWords(new byte[4], new byte[3], 1, false),
            () => AddTransform.InPlaceWords(new byte[4], 0, false, (XorMode)999),
            () => AddTransform.InPlaceWords(new byte[1], 0, false),
            () => AddTransform.InPlaceWords(new byte[2], 1, false),
            () => AddTransform.DecodeCoverageAndCrcWords(new byte[3], 1)
        }) { try { action(); } catch (ArgumentException) { count++; continue; } throw new Exception("Missing parameter guard"); }
        foreach (uint key in new uint[] { 0, 1, 0x01020304 }) {
            byte[] guarded = Enumerable.Repeat((byte)0xC7, 51).ToArray();
            XorTransform.DecodeCoverageAndCrc(guarded.AsSpan(7, 35), key, 3);
            Require(guarded.AsSpan(0, 7).IndexOfAnyExcept((byte)0xC7) < 0 && guarded.AsSpan(42).IndexOfAnyExcept((byte)0xC7) < 0, "Fusion outer guards");
        }
        return count;
    }
    internal static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
