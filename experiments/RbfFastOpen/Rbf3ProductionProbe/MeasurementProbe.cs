using System.Diagnostics;
using Atelia;
using Atelia.Data;
using Atelia.Rbf;

internal static class MeasurementProbe {
    internal sealed record Results(List<object> Open, List<object> Anomalous, List<object> Performance);

    internal static Results Run(string output, bool quick) {
        int samples = quick ? 3 : 7;
        var open = new List<object>();
        var abnormal = new List<object>();
        foreach (int n in quick ? new[] { 1, 1000 } : new[] { 1, 1000, 100000 }) {
            string history = Path.Combine(output, "io", $"history-n{n}.rbf");
            using (var file = RbfFile.CreateNew(history, RbfCacheMode.Off)) {
                for (int i = 1; i < n; i++) { file.Append(7, ReadOnlySpan<byte>.Empty).Unwrap(); }
            }
            foreach (string size in quick ? new[] { "empty", "1m" } : new[] { "empty", "1m", "max" }) {
                int meta = size == "max" ? 65535 : size == "1m" ? 3 : 0;
                int total = size == "max" ? RbfFile.MaxPayloadAndMetaLength : size == "1m" ? 1024 * 1024 + meta : 0;
                byte[] data = Program.Pattern(total, marker: true);
                string path = Path.Combine(output, "io", $"open-n{n}-{size}.rbf");
                File.Copy(history, path, overwrite: false);
                SizedPtr ticket;
                using (var file = RbfFile.OpenExisting(path, out _, RbfCacheMode.Off)) {
                    ticket = Program.WriteFrame(file, data, meta, 71, builder: false);
                    file.DurableFlush();
                }
                long[] times = new long[samples];
                using (var warm = RbfFile.OpenReadOnlyExisting(path)) { Program.Require(warm.TailOffset == new FileInfo(path).Length, "Warm open length mismatch."); }
                for (int i = 0; i < samples; i++) {
                    long start = Stopwatch.GetTimestamp();
                    using var file = RbfFile.OpenReadOnlyExisting(path);
                    times[i] = Stopwatch.GetTimestamp() - start;
                }
                var readOnlyMetrics = OpenMetrics(path, writable: false);
                var writableMetrics = OpenMetrics(path, writable: true);
                open.Add(new { Frames = n, FinalPayloadBytes = total - meta, TailMetaBytes = meta,
                    FrameBytes = ticket.Length, FileBytes = new FileInfo(path).Length,
                    WarmOpenTicks = times, WarmOpenMedianMicroseconds = Median(times) * 1_000_000.0 / Stopwatch.Frequency,
                    ReadOnlyMetrics = readOnlyMetrics, WritableMetrics = writableMetrics,
                    AssertedRequestedBytesMaximum = 39, AssertedReadCallsMaximum = 4 });
                if (n == 1) {
                    if (size != "max") { using var file = RbfFile.OpenReadOnlyExisting(path); Program.VerifyFrame(file, ticket, data, meta, 71); }
                    else {
                        VerifyMaximum(path, ticket, data, meta);
                        Console.WriteLine($"Real maximum W: frame roundtrip passed: {ticket.Length} bytes.");
                    }
                    if (size != "empty") { abnormal.Add(MeasureAbnormal(output, path, ticket, size)); }
                }
                Console.WriteLine($"Healthy Open n={n}, final={size}: {readOnlyMetrics.RequestedBytes} logical bytes.");
                // Maximum input and caller buffer are released before the next large case.
                data = null!;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        return new(open, abnormal, Performance(output, quick));
    }

    private static RbfReadMetricsSnapshot OpenMetrics(string path, bool writable) {
        using var metrics = RbfReadMetrics.Begin();
        using var file = writable ? RbfFile.OpenExisting(path, out _) : RbfFile.OpenReadOnlyExisting(path);
        var value = metrics.Snapshot();
        Program.Require(value.RequestedBytes <= 39 && value.RawRequestedBytes <= 39 && value.ReturnedBytes <= 39 && value.ReadCalls <= 4,
            "Healthy production Open exceeded its structural read budget.");
        Program.Require(value.ReadAheadReturnedBytes == 0, "Factory Open unexpectedly fetched reader cache pages.");
        return value;
    }

    private static void VerifyMaximum(string path, SizedPtr ticket, byte[] input, int meta) {
        Program.Require(ticket.Length == SizedPtr.MaxLength && input.Length == 268435424 && ticket.Offset == 4, "Maximum RBF3 geometry mismatch.");
        using var file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
        VerifyMaximumCaller(file, ticket, input, meta);
        GC.Collect();
        using (var pooled = file.ReadPooledFrame(ticket).Unwrap()) {
            Program.Require(pooled.PayloadAndMeta.SequenceEqual(input) && pooled.TailMetaLength == meta, "Maximum pooled read mismatch.");
        }
        var info = file.ReadFrameInfo(ticket).Unwrap();
        Program.Require(info.PayloadLength == input.Length - meta && (info.PayloadLength & 3) == 1, "Maximum metadata byte phase mismatch.");
        using var tail = info.ReadPooledTailMeta().Unwrap();
        Program.Require(tail.TailMeta.SequenceEqual(input.AsSpan(input.Length - meta)), "Maximum metadata decode mismatch.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void VerifyMaximumCaller(IRbfFile file, SizedPtr ticket, byte[] input, int meta) {
        var buffer = new byte[ticket.Length];
        var frame = file.ReadFrame(ticket, buffer).Unwrap();
        Program.Require(frame.PayloadAndMeta.SequenceEqual(input) && frame.TailMetaLength == meta, "Maximum caller read mismatch.");
    }

    private static object MeasureAbnormal(string output, string healthy, SizedPtr ticket, string size) {
        string path = Path.Combine(output, "io", $"anomalous-{size}.rbf");
        File.Copy(healthy, path, overwrite: false);
        // Drop part of TrailerCodeword along with Key/Fence, retaining a known valid HeadLen.
        long eof = ticket.Offset + ticket.Length - 12;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None)) { stream.SetLength(eof); }
        using var metrics = RbfReadMetrics.Begin();
        long began = Stopwatch.GetTimestamp();
        using var file = RbfFile.OpenExisting(path, out var report, RbfCacheMode.Off);
        long ticks = Stopwatch.GetTimestamp() - began;
        var value = metrics.Snapshot();
        Program.Require(report.Action == RbfTailRecoveryAction.Truncated && report.FinalLength == 4, "Incomplete maximum tail must truncate to its confirmed start.");
        const long aggregateBound = (long)SizedPtr.MaxLength + 135;
        Program.Require(value.RequestedBytes <= aggregateBound && value.RawRequestedBytes <= aggregateBound, "Abnormal Open exceeded scan plus qualification aggregate budget.");
        // No overlap between 64KiB scan blocks. Additional source-proven qualification reads
        // are reported in aggregate counters rather than mislabeled as search coverage.
        return new { Size = size, OriginalFileBytes = eof, Action = report.Action.ToString(), Metrics = value,
            Ticks = ticks, Milliseconds = ticks * 1000.0 / Stopwatch.Frequency,
            ScanOffsetCoverageMaximum = (long)SizedPtr.MaxLength + 7, ScanBlockBytes = 65536,
            AggregateRequestedBytesMaximum = aggregateBound,
            Scope = "One residual tail, production nonoverlapping aligned Fence scan; counters include Header and qualification reads." };
    }

    private static List<object> Performance(string output, bool quick) {
        int samples = quick ? 3 : 7;
        int batchBytes = (quick ? 2 : 16) * 1024 * 1024;
        var rows = new List<object>();
        foreach (int payloadBytes in new[] { 4096, 1024 * 1024 }) {
            foreach (bool marker in new[] { false, true }) {
                const int meta = 3;
                byte[] input = Program.Pattern(payloadBytes + meta, marker);
                int operations = Math.Max(2, batchBytes / payloadBytes);
                var appendTimes = new long[2][] { new long[samples], new long[samples] };
                var flushTimes = new long[2][] { new long[samples], new long[samples] };
                var allocations = new long[2][] { new long[samples], new long[samples] };
                for (int sample = 0; sample < samples; sample++) {
                    for (int turn = 0; turn < 2; turn++) {
                        int method = (sample + turn) & 1;
                        string path = Path.Combine(output, "io", $"perf-write-{payloadBytes}-{marker}-{method}-{sample}.rbf");
                        using var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
                        // Warm this file's scratch/pool lifecycle outside timing; subsequent public
                        // writes include CRC, key selection, transform, copy/chunks and real writes.
                        var warmTicket = Program.WriteFrame(file, input, meta, 83, method == 1);
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long began = Stopwatch.GetTimestamp();
                        SizedPtr last = warmTicket;
                        for (int i = 0; i < operations; i++) { last = Program.WriteFrame(file, input, meta, 83, method == 1); }
                        appendTimes[method][sample] = Stopwatch.GetTimestamp() - began;
                        allocations[method][sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        began = Stopwatch.GetTimestamp();
                        file.DurableFlush();
                        flushTimes[method][sample] = Stopwatch.GetTimestamp() - began;
                        Program.VerifyFrame(file, last, input, meta, 83);
                    }
                }
                foreach (int method in new[] { 0, 1 }) {
                    rows.Add(new { Operation = method == 0 ? "Append" : "Builder", PayloadBytes = payloadBytes,
                        MarkerPattern = marker, Samples = samples, OperationsPerSample = operations,
                        AppendTicks = appendTimes[method], FlushTicks = flushTimes[method], AllocatedBytes = allocations[method],
                        MedianNanosecondsPerFrame = Median(appendTimes[method]) * 1_000_000_000.0 / Stopwatch.Frequency / operations,
                        TimingScope = "Warm scratch/pool; public CRC+key+transform+buffer copy/chunks+real buffered W: writes. Durable flush reported separately." });
                }
                rows.AddRange(MeasureRead(output, input, payloadBytes, marker, meta, samples, quick));
            }
        }
        return rows;
    }

    private static List<object> MeasureRead(string output, byte[] input, int payload, bool marker, int meta, int samples, bool quick) {
        string path = Path.Combine(output, "io", $"perf-read-{payload}-{marker}.rbf");
        SizedPtr ticket;
        using (var writer = RbfFile.CreateNew(path)) { ticket = Program.WriteFrame(writer, input, meta, 89, builder: false); writer.DurableFlush(); }
        string[] names = ["ReadFrame", "ReadPooledFrame", "ReadFrameInfo", "ReadTailMeta", "ReadPooledTailMeta"];
        var rows = new List<object>();
        foreach (var cache in new[] { RbfCacheMode.Off, RbfCacheMode.Slots16 }) {
            using var file = RbfFile.OpenReadOnlyExisting(path, cache);
            byte[] buffer = new byte[ticket.Length], metaBuffer = new byte[meta];
            var times = names.Select(_ => new long[samples]).ToArray();
            var allocs = names.Select(_ => new long[samples]).ToArray();
            int operations = payload >= 1024 * 1024 ? (quick ? 4 : 16) : (quick ? 32 : 512);
            for (int kind = 0; kind < names.Length; kind++) { ReadOne(file, ticket, buffer, metaBuffer, kind); }
            for (int sample = 0; sample < samples; sample++) {
                for (int turn = 0; turn < names.Length; turn++) {
                    int kind = (sample + turn) % names.Length;
                    long allocated = GC.GetAllocatedBytesForCurrentThread(), began = Stopwatch.GetTimestamp();
                    for (int i = 0; i < operations; i++) { ReadOne(file, ticket, buffer, metaBuffer, kind); }
                    times[kind][sample] = Stopwatch.GetTimestamp() - began;
                    allocs[kind][sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
            }
            Program.VerifyFrame(file, ticket, input, meta, 89);
            for (int kind = 0; kind < names.Length; kind++) {
                using var metrics = RbfReadMetrics.Begin();
                ReadOne(file, ticket, buffer, metaBuffer, kind);
                rows.Add(new { Operation = names[kind], Cache = cache.ToString(), PayloadBytes = payload, MarkerPattern = marker,
                    Samples = samples, OperationsPerSample = operations, Ticks = times[kind], AllocatedBytes = allocs[kind],
                    MedianNanosecondsPerFrame = Median(times[kind]) * 1_000_000_000.0 / Stopwatch.Frequency / operations,
                    SeparateWarmMetrics = metrics.Snapshot(), TimingScope = "Repeated same ticket; warm OS cache and warm RBF cache where enabled; public read/CRC/decode and pool lifecycle." });
            }
        }
        return rows;
    }

    private static uint _readSink;
    private static void ReadOne(IRbfFile file, SizedPtr ticket, byte[] buffer, byte[] meta, int kind) {
        switch (kind) {
            case 0: var borrowed = file.ReadFrame(ticket, buffer).Unwrap(); _readSink = borrowed.Tag; break;
            case 1: using (var pooled = file.ReadPooledFrame(ticket).Unwrap()) { _readSink = pooled.Tag; } break;
            case 2: _readSink = file.ReadFrameInfo(ticket).Unwrap().Tag; break;
            case 3: _readSink = file.ReadTailMeta(ticket, meta).Unwrap().Tag; break;
            case 4: using (var pooled = file.ReadPooledTailMeta(ticket).Unwrap()) { _readSink = pooled.Tag; } break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    internal static long Median(long[] values) => values.Order().ElementAt(values.Length / 2);
}
