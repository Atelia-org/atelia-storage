using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Atelia.Data;
using Atelia.Data.Binary;
using Atelia.Data.Hashing;

namespace RbfCodecCost;

/// <summary>
/// Public Data input qualification. Legacy byte-length framing is a fixture only:
/// this probe does not implement or qualify the new units-based RBF profile.
/// </summary>
internal static class DataEscapeProbe {
    private const int Samples = 7;
    private static ulong _consumer;
    private sealed record Workload(string Cohort, int Length, string Pattern, int Chunk);
    private sealed record CpuRow(string Cohort, string Workload, string Operation, int InputBytes,
        int BodyBytes, int OriginalChunks, int MetaBytes, int IterationsPerSample,
        double[] NanosecondsPerOperation, double MedianNs, double MinNs, double MaxNs,
        double AllocatedBytesPerOperation);
    private sealed record WriterRow(string Workload, string Operation, int BodyBytes, int FeedBytes,
        int IterationsPerSample, double[] NanosecondsPerOperation, double MedianNs,
        double MinNs, double MaxNs, double AllocatedBytesPerOperation,
        long Rents, long Returns, long RentedBufferBytes, long PushCalls, string CounterScope);
    private sealed record WireVector(string PayloadHex, string MetaHex, uint Tag, uint Key, string WireHex);

    internal static object Run(string output, bool quick) {
        var cpu = new List<CpuRow>();
        var vectors = new List<WireVector>();
        int qualified = 0;
        foreach (Workload workload in Workloads()) {
            if (quick && workload.Length > 1048576) continue;
            using var fixture = new Fixture(workload);
            Console.WriteLine($"DATA CPU {workload.Cohort} {fixture.Name}");
            // Same payload/meta buffers for matched-source comparisons. OriginalChunks is a
            // separate historical topology comparison, not a claim that 3 spans represent N chunks.
            var actions = new[] {
                (Name: "prototype-prepare-original-chunks", Action: (Func<ulong>)(() =>
                    PrototypeCodec.Prepare(fixture.OriginalChunks, fixture.Meta, fixture.Tag,
                        KeyStrategy.ZeroThenTinyBitmapRandom).Key)),
                (Name: "prototype-prepare-matched-buffers", Action: (Func<ulong>)(() =>
                    PrototypeCodec.Prepare(fixture.MatchedChunks, fixture.Meta, fixture.Tag,
                        KeyStrategy.ZeroThenTinyBitmapRandom).Key)),
                (Name: "data-prepare-borrowed-three-spans", Action: (Func<ulong>)(() => DataPrepare(fixture)))
            };
            foreach (var row in Measure(actions)) cpu.Add(new(workload.Cohort, fixture.Name, row.Name,
                workload.Length, fixture.Body.Length, fixture.OriginalChunks.Length, fixture.Meta, row.Count,
                row.Values, row.Median, row.Min, row.Max, row.Allocation));
            WireVector? vector = CheckFixture(fixture);
            if (vector is not null) vectors.Add(vector);
            qualified++;
            GC.Collect();
        }
        var writers = new List<WriterRow>();
        int writerCases = 0;
        foreach (Workload workload in WriterWorkloads()) {
            if (quick && workload.Length > 1048576) continue;
            using var fixture = new Fixture(workload);
            Console.WriteLine($"DATA WRITER {fixture.Name}");
            writers.AddRange(MeasureFrozenWriter(fixture));
            using var legacy = new WriterFixture(fixture);
            using var production = new WriterFixture(fixture);
            var actions = new[] {
                (Name: "prototype-prepare-copy-xor-writer-lifecycle", Action: (Func<ulong>)(() => legacy.LegacyLifecycle())),
                (Name: "data-owned-writer-crc-select-xor-lifecycle", Action: (Func<ulong>)(() => production.DataLifecycle()))
            };
            foreach (var row in Measure(actions)) {
                WriterFixture owner = row.Name.StartsWith("prototype", StringComparison.Ordinal) ? legacy : production;
                writers.Add(new(fixture.Name, row.Name, fixture.Body.Length, owner.Feed,
                    row.Count, row.Values, row.Median, row.Min, row.Max, row.Allocation,
                    owner.Pool.Rents, owner.Pool.Returns, owner.Pool.RentedBufferBytes,
                    owner.Sink.PushCalls, "Aggregate across warmup, calibration and timed samples; pool wrapper counters included equally"));
                Correctness.Require(owner.Pool.Rents == owner.Pool.Returns && owner.Writer.IsIdle,
                    "Lifecycle all pool rentals returned");
            }
            // Full byte-by-byte scalar verification is an extra untimed lifecycle.
            legacy.Sink.Validate = true; legacy.LegacyLifecycle();
            production.Sink.Validate = true; production.DataLifecycle();
            writerCases++;
            GC.Collect();
        }
        var evidence = new {
            Schema = 1, Samples, Quick = quick, QualifiedAppendCases = qualified, QualifiedWriterCases = writerCases,
            Cpu = cpu, Writer = writers,
            Correctness = "Every workload: scalar forbidden-key/XOR oracle, matched old plaintext CRC/footer, deterministic tiny minimum, borrowed buffers unchanged. Writer witnesses: exact scalar encoded output, no Push/Commit/Length change in fused call, all rentals returned.",
            Method = "Seven rotations. OS CSPRNG included in actual SelectKey. Matched Prepare reads the same payload/meta buffers, computes the same CRC and legacy byte-length footer; Data keeps footer on stack, old wrapper allocation remains timed. Original chunk topology is a separate baseline. Frozen writer Reset/refill and publication/verification are outside its one-call timing. Lifecycle includes Reset, Shared ArrayPool rent/return, fill, CRC/footer, selection/XOR, Commit/recycle and cheap synchronous sink consumption. Legacy lifecycle encodes while copying from frozen source; Data lifecycle transforms already owned bytes, so their memory topology differs.",
            Scope = "Data foundation and public input/pool costs only; no new RBF profile, units CRC, production RBF2 append/open/recovery or SSD throughput claim. All output artifacts use W:. Near-limit fixture/body copies are untimed oracle material, not production allocations."
        };
        File.WriteAllText(Path.Combine(output, "data-escape.json"), JsonSerializer.Serialize(evidence, Program.Json));
        File.WriteAllText(Path.Combine(output, "data-vectors.json"), JsonSerializer.Serialize(vectors, Program.Json));
        return evidence;
    }

    private static List<Workload> Workloads() {
        var values = new List<Workload> { new("tiny", 0, "zero", 0), new("tiny", 0, "footer-marker", 0) };
        foreach (int length in new[] { 31, 128, 232, 233, 4096 })
        foreach (string pattern in new[] { "zero", "random", "marker", "dense301" })
            values.Add(new("tiny", length, pattern, 0));
        foreach (int length in new[] { 31, 232 })
        foreach (string pattern in new[] { "marker", "dense301" })
            values.Add(new("tiny", length, pattern, 3));
        foreach (int length in new[] { 0, 31, 4096, 1048576, 16777216, PrototypeCodec.MaxFrameLength - 28 }) {
            string[] patterns = length < 4 ? ["zero", "footer-marker"] : length < 1204 ? ["zero", "random", "marker"] :
                ["zero", "random", "marker", "dense301", "dense301-late", "random-fence-first", "random-fence-last"];
            foreach (string pattern in patterns)
            foreach (int chunk in (length is 1048576 or 16777216) && (pattern is "dense301-late" or "random-fence-last") ?
                new[] { 0, 65533 } : new[] { 0 })
                values.Add(new("random", length, pattern, chunk));
        }
        return values;
    }

    private static Workload[] WriterWorkloads() => [
        new("writer", 0, "footer-marker", 3), new("writer", 31, "zero", 3),
        new("writer", 31, "marker", 3), new("writer", 232, "dense301", 3),
        new("writer", 233, "marker", 3), new("writer", 4096, "dense301", 1021),
        new("writer", 1048576, "zero", 65533), new("writer", 1048576, "marker", 65533),
        new("writer", 1048576, "random-fence-last", 65533),
        new("writer", PrototypeCodec.MaxFrameLength - 28, "random-fence-last", 65533)
    ];

    private sealed class Fixture : IDisposable {
        internal readonly Workload Workload;
        internal readonly byte[][] OriginalChunks, MatchedChunks;
        internal readonly byte[] Payload, MetaBytes, Body;
        internal readonly int Meta;
        internal readonly uint Tag;
        internal readonly string Name;
        internal Fixture(Workload workload) {
            Workload = workload; Name = $"{workload.Length}/{workload.Pattern}/chunk-{workload.Chunk}";
            byte[] input = Create(workload.Length, workload.Pattern);
            Meta = Math.Min(workload.Length, 3);
            Tag = workload.Pattern == "footer-marker" ? PrototypeCodec.Fence : 11u;
            Payload = input.AsSpan(0, input.Length - Meta).ToArray();
            MetaBytes = input.AsSpan(input.Length - Meta).ToArray();
            MatchedChunks = [Payload, MetaBytes];
            OriginalChunks = Corpus.Split(input, workload.Chunk);
            PreparedFrame old = PrototypeCodec.Prepare(MatchedChunks, Meta, Tag, KeyStrategy.ZeroThenTinyBitmapRandom);
            Body = new byte[old.FrameLength - 8];
            input.CopyTo(Body, 0); old.Footer.CopyTo(Body, input.Length);
        }
        public void Dispose() { }
    }

    private static byte[] Create(int length, string pattern) {
        if (pattern == "footer-marker") return new byte[length];
        if (!pattern.StartsWith("random-fence-", StringComparison.Ordinal)) return Corpus.Create(length, pattern);
        byte[] bytes = Corpus.Create(length, "random");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pattern.EndsWith("first", StringComparison.Ordinal) ?
            0 : (length & ~3) - 4), PrototypeCodec.Fence);
        return bytes;
    }

    private static uint DataPrepare(Fixture fixture) {
        Span<byte> footer = stackalloc byte[23];
        int padding = (-fixture.Workload.Length) & 3;
        footer[..padding].Clear();
        uint raw = RollingCrc.CrcForward(RollingCrc.DefaultInitValue, fixture.Payload);
        raw = RollingCrc.CrcForward(raw, fixture.MetaBytes);
        raw = RollingCrc.CrcForward(raw, footer[..padding]);
        WriteTail(footer.Slice(padding, 20), fixture, raw ^ RollingCrc.DefaultFinalXor);
        return XorEscape.SelectKey(PrototypeCodec.Fence, fixture.Payload, fixture.MetaBytes, footer[..(padding + 20)]);
    }

    private static void WriteTail(Span<byte> footer, Fixture fixture, uint crc) {
        int padding = (-fixture.Workload.Length) & 3;
        BinaryPrimitives.WriteUInt32LittleEndian(footer, crc);
        Span<byte> trailer = footer[4..];
        // The three LE fields cover the last 12B; sealing overwrites the first 4B.
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], ((uint)padding << 29) | (uint)fixture.Meta);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], fixture.Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], (uint)(fixture.Body.Length + 8));
        RollingCrc.SealCodewordBackward(trailer);
    }

    private static WireVector? CheckFixture(Fixture fixture) {
        byte[] payloadBefore = (byte[])fixture.Payload.Clone(), metaBefore = (byte[])fixture.MetaBytes.Clone();
        uint key = DataPrepare(fixture);
        CheckKey(fixture.Body, key);
        uint selected = XorEscape.SelectKey(PrototypeCodec.Fence, fixture.Payload, fixture.MetaBytes,
            fixture.Body.AsSpan(fixture.Workload.Length));
        CheckKey(fixture.Body, selected);
        if (fixture.Body.Length <= 252)
            Correctness.Require(key == ScalarMinimumKey(fixture.Body) && selected == key, "Data tiny scalar minimum");
        Span<byte> footer = stackalloc byte[23];
        int pad = (-fixture.Workload.Length) & 3; footer[..pad].Clear();
        uint raw = RollingCrc.CrcForward(RollingCrc.DefaultInitValue, fixture.Payload);
        raw = RollingCrc.CrcForward(raw, fixture.MetaBytes);
        raw = RollingCrc.CrcForward(raw, footer[..pad]);
        WriteTail(footer.Slice(pad, 20), fixture, raw ^ RollingCrc.DefaultFinalXor);
        Correctness.Require(footer[..(pad + 20)].SequenceEqual(fixture.Body.AsSpan(fixture.Workload.Length)),
            "Data stack footer exactly matches old plaintext CRC/footer bytes");
        Correctness.Require(fixture.Payload.AsSpan().SequenceEqual(payloadBefore) &&
            fixture.MetaBytes.AsSpan().SequenceEqual(metaBefore), "Borrowed sources unchanged");
        byte[] encoded = new byte[fixture.Body.Length];
        XorEscape.Copy(fixture.Payload, encoded, selected, 0);
        XorEscape.Copy(fixture.MetaBytes, encoded.AsSpan(fixture.Payload.Length), selected, fixture.Payload.Length & 3);
        XorEscape.Copy(footer[..(pad + 20)], encoded.AsSpan(fixture.Workload.Length), selected, fixture.Workload.Length & 3);
        CheckXor(fixture.Body, encoded, selected);
        WireVector? vector = null;
        if (fixture.Workload.Length <= 4096) {
            byte[] wire = new byte[fixture.Body.Length + 12];
            BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(fixture.Body.Length + 8));
            encoded.CopyTo(wire, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(fixture.Body.Length + 4), selected);
            BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(fixture.Body.Length + 8), PrototypeCodec.Fence);
            vector = new(Convert.ToHexString(fixture.Payload), Convert.ToHexString(fixture.MetaBytes),
                fixture.Tag, selected, Convert.ToHexString(wire));
        }
        XorEscape.InPlace(encoded, selected);
        Correctness.Require(encoded.AsSpan().SequenceEqual(fixture.Body), "Data XOR round-trip");
        return vector;
    }

    private static uint ScalarMinimumKey(ReadOnlySpan<byte> body) {
        for (uint key = 0; key <= 63; key++) {
            bool allowed = true;
            for (int i = 0; i < body.Length; i += 4)
                if ((BinaryPrimitives.ReadUInt32LittleEndian(body[i..]) ^ key) == PrototypeCodec.Fence) {
                    allowed = false; break;
                }
            if (allowed) return key;
        }
        throw new Exception("Tiny oracle exhausted sufficient set");
    }

    private static void CheckKey(ReadOnlySpan<byte> body, uint key) {
        Correctness.Require(key != PrototypeCodec.Fence, "Raw EscapeKey cannot be Fence");
        for (int i = 0; i < body.Length; i += 4)
            Correctness.Require((BinaryPrimitives.ReadUInt32LittleEndian(body[i..]) ^ key) != PrototypeCodec.Fence,
                "Independent scalar forbidden-key oracle");
    }

    private static void CheckXor(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> encoded, uint key) {
        Correctness.Require(encoded.Length == plaintext.Length, "Encoded length");
        for (int i = 0; i < plaintext.Length; i += 4)
            Correctness.Require(BinaryPrimitives.ReadUInt32LittleEndian(encoded[i..]) ==
                (BinaryPrimitives.ReadUInt32LittleEndian(plaintext[i..]) ^ key), "Independent scalar XOR oracle");
    }

    private sealed record Timed(string Name, int Count, double[] Values, double Median, double Min, double Max, double Allocation);
    private static List<Timed> Measure((string Name, Func<ulong> Action)[] variants) {
        int[] counts = new int[variants.Length];
        for (int index = 0; index < variants.Length; index++) {
            for (int warm = 0; warm < 3; warm++) _consumer ^= variants[index].Action();
            long start = Stopwatch.GetTimestamp(); int completed = 0;
            do { _consumer ^= variants[index].Action(); completed++; }
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 20 && completed < 131072);
            counts[index] = Math.Clamp((int)Math.Ceiling(completed * 0.040 / Stopwatch.GetElapsedTime(start).TotalSeconds), 1, 131072);
        }
        double[][] times = variants.Select(_ => new double[Samples]).ToArray(); long[] allocations = new long[variants.Length];
        for (int sample = 0; sample < Samples; sample++)
        for (int position = 0; position < variants.Length; position++) {
            int index = (sample + position) % variants.Length;
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            ulong checksum = 0;
            for (int iteration = 0; iteration < counts[index]; iteration++) checksum ^= variants[index].Action();
            long ticks = Stopwatch.GetTimestamp() - start;
            allocations[index] += GC.GetAllocatedBytesForCurrentThread() - before; _consumer ^= checksum;
            times[index][sample] = ticks * (1e9 / Stopwatch.Frequency) / counts[index];
        }
        return variants.Select((variant, index) => {
            double[] sorted = times[index].Order().ToArray();
            return new Timed(variant.Name, counts[index], times[index], sorted[Samples / 2], sorted[0], sorted[^1],
                allocations[index] / (double)(Samples * counts[index]));
        }).ToList();
    }

    private static List<WriterRow> MeasureFrozenWriter(Fixture fixture) {
        var values = new[] { new double[Samples], new double[Samples] };
        long[] allocated = [0, 0];
        byte[] contiguous = new byte[fixture.Body.Length];
        using var owner = new WriterFixture(fixture);
        for (int warmup = 0; warmup < 3; warmup++) {
            fixture.Body.CopyTo(contiguous, 0);
            uint key = XorEscape.SelectKey(PrototypeCodec.Fence, contiguous); XorEscape.InPlace(contiguous, key);
            owner.LoadBody(); key = owner.Writer.XorEscapeSinceReservationEnd(owner.Token, PrototypeCodec.Fence);
            owner.Publish(key);
        }
        for (int sample = 0; sample < Samples; sample++)
        for (int position = 0; position < 2; position++) {
            int variant = (sample + position) % 2;
            if (variant == 0) fixture.Body.CopyTo(contiguous, 0);
            else owner.LoadBody();
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            uint key;
            if (variant == 0) {
                key = XorEscape.SelectKey(PrototypeCodec.Fence, contiguous);
                XorEscape.InPlace(contiguous, key);
            } else key = owner.Writer.XorEscapeSinceReservationEnd(owner.Token, PrototypeCodec.Fence);
            long ticks = Stopwatch.GetTimestamp() - start;
            allocated[variant] += GC.GetAllocatedBytesForCurrentThread() - before;
            values[variant][sample] = ticks * (1e9 / Stopwatch.Frequency);
            CheckKey(fixture.Body, key);
            if (variant == 0) CheckXor(fixture.Body, contiguous, key);
            else {
                Correctness.Require(owner.Writer.Length == fixture.Body.Length + 4L && owner.Writer.PushedLength == 0 &&
                    owner.Writer.PendingReservationCount == 1, "Fused operation neither publishes nor changes boundaries");
                owner.Sink.Validate = true; owner.Publish(key);
                Correctness.Require(owner.Pool.Rents == owner.Pool.Returns, "Frozen writer pool balance");
            }
        }
        var rows = new List<WriterRow>();
        foreach (int index in new[] { 0, 1 }) {
            double[] sorted = values[index].Order().ToArray();
            rows.Add(new(fixture.Name, index == 0 ? "frozen-contiguous-data-select-inplace" : "frozen-owned-writer-select-inplace",
                fixture.Body.Length, owner.Feed, 1, values[index], sorted[Samples / 2], sorted[0], sorted[^1],
                allocated[index] / Samples, index == 0 ? 0 : owner.Pool.Rents,
                index == 0 ? 0 : owner.Pool.Returns, index == 0 ? 0 : owner.Pool.RentedBufferBytes,
                index == 0 ? 0 : owner.Sink.PushCalls, "Pool counters include 3 warmup loads plus 7 one-call samples; refill/pool/publication/oracle outside timer"));
        }
        return rows;
    }

    private sealed class CountPool : ArrayPool<byte> {
        internal long Rents, Returns, RentedBufferBytes;
        public override byte[] Rent(int minimumLength) {
            byte[] result = Shared.Rent(minimumLength); Rents++; RentedBufferBytes += result.Length; return result;
        }
        public override void Return(byte[] array, bool clearArray = false) { Returns++; Shared.Return(array, clearArray); }
    }

    private sealed class CheckingSink(Fixture fixture) : IByteSink {
        internal bool Validate;
        internal uint Key;
        internal long Position, PushCalls;
        internal void Reset() { Position = 0; }
        public void Push(ReadOnlySpan<byte> bytes) {
            PushCalls++;
            if (Validate) for (int i = 0; i < bytes.Length; i++) {
                long at = Position + i; byte expected;
                if (at < 4) expected = (byte)((uint)(fixture.Body.Length + 8) >> (int)(at * 8));
                else if (at < fixture.Body.Length + 4L) {
                    int bodyOffset = (int)at - 4;
                    expected = (byte)(fixture.Body[bodyOffset] ^ (byte)(Key >> ((bodyOffset & 3) * 8)));
                } else if (at < fixture.Body.Length + 8L) expected = (byte)(Key >> (int)((at - fixture.Body.Length - 4) * 8));
                else expected = (byte)(PrototypeCodec.Fence >> (int)((at - fixture.Body.Length - 8) * 8));
                Correctness.Require(bytes[i] == expected, "Actual writer publication scalar byte oracle");
            }
            if (!bytes.IsEmpty) _consumer ^= (ulong)bytes.Length ^ bytes[0] ^ bytes[^1];
            Position += bytes.Length;
        }
    }

    private sealed class WriterFixture : IDisposable {
        private readonly Fixture _fixture;
        internal readonly CountPool Pool = new();
        internal readonly CheckingSink Sink;
        internal readonly SinkReservableWriter Writer;
        internal readonly int Feed;
        internal int Token;
        internal WriterFixture(Fixture fixture) {
            _fixture = fixture; Feed = fixture.Workload.Chunk == 0 ? 65533 : fixture.Workload.Chunk;
            Sink = new(fixture);
            Writer = new(Sink, new ChunkedReservableWriterOptions { MinChunkSize = 1024, MaxChunkSize = 65536, Pool = Pool });
        }
        private void Begin() { Writer.Reset(); Sink.Reset(); _ = Writer.ReserveSpan(4, out Token); }
        internal void LoadBody() { Begin(); Append(_fixture.Body); }
        private void Append(ReadOnlySpan<byte> source) {
            while (!source.IsEmpty) {
                int count = Math.Min(source.Length, Feed);
                source[..count].CopyTo(Writer.GetSpan(count)); Writer.Advance(count); source = source[count..];
            }
        }
        private void AppendEncoded(ReadOnlySpan<byte> source, uint key, ref int phase) {
            while (!source.IsEmpty) {
                int count = Math.Min(source.Length, Feed);
                XorTransform.Copy(source[..count], Writer.GetSpan(count), key, phase, XorMode.Vector);
                Writer.Advance(count); phase += count; source = source[count..];
            }
        }
        internal uint LegacyLifecycle() {
            Begin();
            PreparedFrame prepared = PrototypeCodec.Prepare(_fixture.MatchedChunks, _fixture.Meta, _fixture.Tag,
                KeyStrategy.ZeroThenTinyBitmapRandom);
            int phase = 0;
            foreach (byte[] chunk in prepared.Chunks) AppendEncoded(chunk, prepared.Key, ref phase);
            AppendEncoded(prepared.Footer, prepared.Key, ref phase);
            Publish(prepared.Key); return prepared.Key;
        }
        internal uint DataLifecycle() {
            Begin(); Append(_fixture.Payload); Append(_fixture.MetaBytes);
            int pad = (-_fixture.Workload.Length) & 3; Span<byte> padding = stackalloc byte[3]; padding.Clear();
            Append(padding[..pad]);
            uint crc = Writer.GetCrcSinceReservationEnd(Token);
            Span<byte> tail = stackalloc byte[20]; WriteTail(tail, _fixture, crc); Append(tail);
            uint key = Writer.XorEscapeSinceReservationEnd(Token, PrototypeCodec.Fence);
            Publish(key); return key;
        }
        internal void Publish(uint key) {
            Sink.Key = key; Span<byte> closure = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(closure, key);
            BinaryPrimitives.WriteUInt32LittleEndian(closure[4..], PrototypeCodec.Fence); Append(closure);
            Correctness.Require(Writer.TryGetReservedSpan(Token, out Span<byte> head), "Pending head access");
            BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)(_fixture.Body.Length + 8));
            Writer.Commit(Token);
            Correctness.Require(Writer.IsIdle && Sink.Position == _fixture.Body.Length + 12L, "Complete prototype writer publication");
        }
        public void Dispose() => Writer.Dispose();
    }
}
