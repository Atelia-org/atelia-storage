using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Atelia.FrameStore;
using Store = Atelia.FrameStore.FrameStore;

namespace FrameStoreResourceProbe;

internal static class Program {
    private const string Usage = "FrameStoreResourceProbe [--work-root fresh-directory] [--output fresh-json-file] [--source-revision revision] [--builder-limits 1,4,16] [--archive-scales 16,128,1024] [--size-hints 1048576,4194304,8388608] [--repeats 3] [--hint-iterations 4] [--append-iterations 8] [--large-payload-bytes 262144] [--archive-payload-bytes 256] [--random-reads 64] [--max-seconds 180] [--max-disk-mib 96]";

    public static int Main(string[] args) {
        ProbeOptions options;
        try { options = ProbeOptions.Parse(args); }
        catch (HelpRequestedException) { Console.WriteLine(Usage); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message + "\n" + Usage); return 2; }

        var report = new ProbeReport { Input = options };
        var elapsed = Stopwatch.StartNew();
        int exitCode = 0;
        try {
            Directory.CreateDirectory(options.WorkRoot);
            Console.Error.WriteLine($"Bounded probe: root={options.WorkRoot}; estimated data={options.EstimatedDataBytes}; disk limit={options.MaxDiskBytes}; cooperative time limit={options.MaxSeconds}s.");
            var probe = new ResourceProbe(options, report, elapsed);
            probe.Run();
            report.Status = "passed";
        }
        catch (Exception error) {
            report.Status = error is BudgetExceededException ? "budget-exceeded" : "failed";
            report.Failure = error.ToString();
            exitCode = error is BudgetExceededException ? 3 : 1;
        }
        finally {
            report.ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            report.FinishedUtc = DateTime.UtcNow;
            report.FinalDisk = DiskSnapshot.Capture(options.WorkRoot);
        }
        string output = options.Output ?? Path.Combine(options.WorkRoot, "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string json = JsonSerializer.Serialize(report, JsonOutput.Options);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) {
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
        Console.WriteLine(json);
        Console.Error.WriteLine($"{report.Status}: {report.Assertions.Count} assertions; {report.Samples.Count} samples; evidence={output}");
        return exitCode;
    }
}

internal sealed class ResourceProbe(ProbeOptions options, ProbeReport report, Stopwatch elapsed) {
    // This is a declared public Create input, not a copy of the internal initialization boundary.
    // A >=256B payload crosses this threshold; actual one-file-per-Append is asserted below.
    private const long ArchiveThreshold = 256;
    private const uint PayloadTag = 71;

    public void Run() {
        Warmup();
        foreach (int limit in options.BuilderLimits) {
            for (int repeat = 0; repeat < options.Repeats; repeat++) { Builders(limit, repeat); }
        }
        SizeHints();
        foreach (string pattern in new[] { "no-fence", "aligned-rbf3-fence" }) { LargeAppends(pattern); }
        OpenFailures();
        foreach (int scale in options.ArchiveScales) { Archives(scale); }
        Check("complete-all-configured-scenarios", true, "All configured scales and repetitions completed inside cooperative budgets.");
    }

    private void Warmup() {
        CheckTime();
        Console.Error.WriteLine("Warmup: observers, serializer, active/archive public paths.");
        _ = JsonSerializer.Serialize(report, JsonOutput.Options);
        _ = ResourceSnapshot.Capture();
        byte[] payload = CreatePayload(options.ArchivePayloadBytes, "aligned-rbf3-fence");
        string root = Path.Combine(options.WorkRoot, "warmup");
        using (var store = Store.Create(root, ArchiveThreshold)) {
            var address = store.Append(PayloadTag, payload).Unwrap();
            store.ConfirmDurable();
            VerifyFrame(store, address, payload);
            _ = store.Inventory(_ => { }).Unwrap();
            _ = store.Audit(_ => { }).Unwrap();
        }
        using (var store = Store.Open(root, ArchiveThreshold)) {
            using var builder = store.BeginAppend();
            var writer = builder.PayloadAndMeta;
            writer.GetSpan(64).Clear();
            writer.Advance(0);
            store.ConfirmDurable();
        }
        using (var readOnly = Store.OpenReadOnly(root)) { _ = readOnly.Inventory(_ => { }).Unwrap(); }
        Measure("observer", "warmed-baseline", 0, 0, root, () => { });
    }

    private void Builders(int limit, int repeat) {
        string root = NewConfiguredRoot($"builders-{limit}-r{repeat}", limit);
        Store? store = null;
        var builders = new List<FrameBuilder>();
        FrameAddress completed = default;
        byte[] payload = CreatePayload(options.ArchivePayloadBytes, "no-fence");
        try {
            Measure("builders", "create", repeat, limit, root, () => store = Store.Create(root));
            Measure("builders", "hold-full-quota-plus-append", repeat, limit, root, () => {
                for (int i = 0; i < limit; i++) {
                    CheckTime();
                    var builder = store!.BeginAppend(1, 0, out _);
                    builders.Add(builder);
                    var writer = builder.PayloadAndMeta;
                    writer.GetSpan(1)[0] = (byte)i;
                    writer.Advance(1);
                }
                Expect<InvalidOperationException>(() => store!.BeginAppend(), "full Builder quota rejects BeginAppend");
                completed = store!.Append(PayloadTag, payload).Unwrap();
            });
            var held = DiskSnapshot.Capture(root);
            Check("historical-active-peak", held.ActiveFileCount == limit + 1L, $"M={limit}; active={held.ActiveFileCount}; includes Append's short lease while all Builders are held.");
            Measure("builders", "healthy-cancel-to-idle", repeat, limit, root, () => {
                foreach (var builder in builders) { builder.Dispose(); }
                builders.Clear();
                store!.ConfirmDurable();
                VerifyFrame(store, completed, payload);
                Check("cancelled-builders-not-inventory", store.Inventory(_ => { }).Unwrap() == 1, "Only completed Append is inventoried after cancellations.");
            }, new() { ["retainedActivePolicy"] = "Idle writable active handles retained until archive/owner Dispose" });
            Measure("builders", "dispose", repeat, limit, root, () => { store!.Dispose(); store = null; });

            // Config changes and raw metadata observations occur only after the owner has closed.
            WriteConfiguration(root, 1);
            Measure("builders", "cold-owner-open-m1", repeat, limit, root, () => store = Store.Open(root));
            Check("m1-retains-entire-historical-active", store!.RecoveryReports.Count == limit + 1,
                $"Historical active={limit + 1}; recovery reports={store.RecoveryReports.Count}; lowering M does not shrink it.");
            Measure("builders", "m1-first-confirm", repeat, limit, root, () => {
                store!.ConfirmDurable();
                VerifyFrame(store, completed, payload);
                using var builder = store.BeginAppend();
                Expect<InvalidOperationException>(() => store.BeginAppend(), "Reopened M=1 is enforced");
            });
            Measure("builders", "reopened-dispose", repeat, limit, root, () => { store!.Dispose(); store = null; });
            CheckFilesClosable(root);
            using var verify = Store.Open(root);
            verify.ConfirmDurable();
            Check("post-dispose-owner-reacquired", verify.RecoveryReports.Count == limit + 1, "Fresh owner reacquired after disposal with all active resources released.");
        }
        finally {
            // Owner cleanup invalidates any leases if a probe assertion or budget stops the phase.
            store?.Dispose();
        }
    }

    private void SizeHints() {
        string root = NewConfiguredRoot("size-hints", 1);
        using var store = Store.Create(root);
        foreach (int hint in options.SizeHints) {
            Console.Error.WriteLine($"GetSpan sizeHint={hint}, repeats={options.Repeats}, iterations={options.HintIterations}.");
            for (int repeat = 0; repeat < options.Repeats; repeat++) {
                var details = new Dictionary<string, object?> {
                    ["requestedSizeHint"] = hint, ["declaredPayloadBytes"] = 1, ["iterations"] = options.HintIterations,
                    ["managedPoolRetainedBytes"] = "unknown"
                };
                var capacities = new List<int>();
                var logicalDeltas = new List<long>();
                Measure("size-hint", "borrow-advance0-cancel", repeat, hint, root, () => {
                    for (int i = 0; i < options.HintIterations; i++) {
                        CheckTime();
                        using var builder = store.BeginAppend(1, 0, out _);
                        var writer = builder.PayloadAndMeta;
                        long baseline = writer.Length;
                        Span<byte> borrowed = writer.GetSpan(hint);
                        capacities.Add(borrowed.Length);
                        borrowed[0] = 1;
                        writer.Advance(0);
                        logicalDeltas.Add(writer.Length - baseline);
                    }
                }, details);
                details["returnedCapacities"] = capacities;
                details["logicalLengthDeltas"] = logicalDeltas;
                Check("size-hint-capacity", capacities.All(capacity => capacity >= hint), "Borrowed capacity is measured separately from logical writer Length.");
                Check("size-hint-advance0-no-logical-output", logicalDeltas.All(delta => delta == 0), "Advance(0) ends the borrow without adding user bytes; every Builder is healthily cancelled.");
            }
        }
        Check("size-hint-no-user-frames", store.Inventory(_ => { }).Unwrap() == 0, "Large borrowed capacities did not complete user output.");
        Measure("size-hint", "owner-dispose", 0, 0, root, store.Dispose);
        CheckFilesClosable(root);
    }

    private void LargeAppends(string pattern) {
        Console.Error.WriteLine($"Large Append pattern={pattern}; payload={options.LargePayloadBytes}; batches={options.Repeats}x{options.AppendIterations}.");
        string root = NewConfiguredRoot("large-append-" + pattern, 1);
        byte[] payload = CreatePayload(options.LargePayloadBytes, pattern);
        Store? store = null;
        var addresses = new List<FrameAddress>();
        try {
            Measure("large-append", "create", 0, payload.Length, root, () => store = Store.Create(root), new() { ["payloadPattern"] = pattern });
            for (int repeat = 0; repeat < options.Repeats; repeat++) {
                var details = new Dictionary<string, object?> {
                    ["payloadPattern"] = pattern, ["callerInputBytes"] = payload.Length,
                    ["iterations"] = options.AppendIterations,
                    ["scratchInterpretation"] = pattern == "aligned-rbf3-fence" ? "Aligned public wire fence forces nonzero key; large Append scratch branch, verified after Dispose" : "No-fence control; actual final key is checked after Dispose",
                    ["managedPoolRetainedBytes"] = "unknown"
                };
                Measure("large-append", "append-batch", repeat, payload.Length, root, () => {
                    for (int i = 0; i < options.AppendIterations; i++) {
                        CheckTime();
                        addresses.Add(store!.Append(PayloadTag, payload).Unwrap());
                    }
                }, details);
                Measure("owned-read", "retain-read-results", repeat, payload.Length, root, () => {
                    // Each result owns a full decoded frame. Retain one batch, then measure release below.
                    foreach (var address in addresses.TakeLast(options.AppendIterations)) {
                        var result = store!.ReadFrame(address).Unwrap();
                        try { VerifyRead(result, address, payload); }
                        catch { result.Dispose(); throw; }
                        retainedReads.Add(result);
                    }
                }, new() { ["retainedResults"] = options.AppendIterations, ["payloadPattern"] = pattern });
                Measure("owned-read", "dispose-read-results", repeat, payload.Length, root, ReleaseReads,
                    new() { ["releasedResults"] = options.AppendIterations, ["payloadPattern"] = pattern });
            }
            Measure("large-append", "confirm", 0, payload.Length, root, () => store!.ConfirmDurable(), new() { ["payloadPattern"] = pattern });
            Measure("large-append", "owner-dispose", 0, payload.Length, root, () => { store!.Dispose(); store = null; }, new() { ["payloadPattern"] = pattern });
            string dataPath = Directory.GetFiles(Path.Combine(root, "active"), "*.rbf").Single();
            byte[] image = File.ReadAllBytes(dataPath);
            uint lastKey = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(image.Length - 8, 4));
            Check("actual-large-append-tail-key", pattern == "no-fence" ? lastKey == 0 : lastKey != 0,
                $"pattern={pattern}; observed last raw TailKey={lastKey}; read only after owner closed, per public RBF3 wire layout.");
            report.Samples.Last(sample => sample.Scenario == "large-append" && sample.Details.GetValueOrDefault("payloadPattern") as string == pattern)
                .Details["observedLastTailKeyAfterDispose"] = lastKey;
            CheckFilesClosable(root);
        }
        finally { ReleaseReads(); store?.Dispose(); }
    }

    private readonly List<FrameRead> retainedReads = [];

    private void ReleaseReads() {
        foreach (var frame in retainedReads) { frame.Dispose(); }
        retainedReads.Clear();
    }

    private void OpenFailures() {
        string root = NewConfiguredRoot("open-failure", 4);
        using (var store = Store.Create(root)) {
            var builders = Enumerable.Range(0, 3).Select(_ => store.BeginAppend()).ToArray();
            foreach (var builder in builders) { builder.Dispose(); }
            store.ConfirmDurable();
        }
        string path = Directory.GetFiles(Path.Combine(root, "active"), "*.rbf").Order(StringComparer.Ordinal).Last();
        byte[] healthy = File.ReadAllBytes(path);
        byte[] damaged = healthy.ToArray();
        // Public wire: HeaderFence 4B + frame HeadLen 4B; first header payload byte at 8.
        // Leave structure intact but invalidate the first FrameStore header's complete CRC.
        damaged[8] ^= 1;
        File.WriteAllBytes(path, damaged);
        try {
            for (int repeat = 0; repeat < options.Repeats; repeat++) {
                FailedOpen(root, "highest-active-header-crc", repeat, Path.GetFileName(path));
            }
        }
        finally { File.WriteAllBytes(path, healthy); }
        Measure("open-failure", "header-restored-open-confirm", 0, 3, root, () => {
            using var store = Store.Open(root);
            Check("failed-open-cleaned-earlier-active", store.RecoveryReports.Count == 3, "After failing on the highest active header, all earlier active handles and owner lock permit fresh Open.");
            store.ConfirmDurable();
        });
        CheckFilesClosable(root);

        string invalid = Path.Combine(root, "active", "bad-formal-item.rbf");
        File.WriteAllBytes(invalid, [0]);
        try {
            for (int repeat = 0; repeat < options.Repeats; repeat++) { FailedOpen(root, "bad-formal-name", repeat, Path.GetFileName(invalid)); }
        }
        finally { File.Delete(invalid); }
        Measure("open-failure", "formal-item-removed-open-confirm", 0, 3, root, () => {
            using var store = Store.Open(root);
            store.ConfirmDurable();
        });
        CheckFilesClosable(root);
    }

    private void FailedOpen(string root, string kind, int repeat, string damagedName) {
        Exception? failure = null;
        Measure("open-failure", kind, repeat, 3, root, () => {
            try { using var unexpected = Store.Open(root); }
            catch (Exception error) { failure = error; }
        });
        report.ExpectedFailures.Add(new { Kind = kind, Repeat = repeat, Root = root, DamagedName = damagedName, Error = failure?.ToString() });
        Check("open-rejects-invalid-data", failure is InvalidDataException,
            $"kind={kind}; repeat={repeat}; error={failure?.GetType().FullName ?? "unexpected success"}; a lock-leak rejection is not accepted as this data error.");
    }

    private void Archives(int scale) {
        Console.Error.WriteLine($"Archive scale={scale}; repeats={options.Repeats}; fixed payload={options.ArchivePayloadBytes}.");
        string root = Path.Combine(options.WorkRoot, "archive-" + scale);
        byte[] payload = CreatePayload(options.ArchivePayloadBytes, "no-fence");
        var addresses = new List<FrameAddress>();
        Measure("archive-scale", "construct", 0, scale, root, () => {
            using var store = Store.Create(root, ArchiveThreshold);
            for (int i = 0; i < scale; i++) {
                CheckTime();
                addresses.Add(store.Append(PayloadTag, payload).Unwrap());
            }
            store.ConfirmDurable();
        }, new() { ["fixedPayloadBytes"] = payload.Length, ["rotationThresholdBytes"] = ArchiveThreshold });
        var disk = DiskSnapshot.Capture(root);
        Check("archive-file-scale", disk.ArchiveFileCount == scale && disk.ActiveFileCount == 0,
            $"requested={scale}; archive={disk.ArchiveFileCount}; active={disk.ActiveFileCount}; actual bucket count={disk.ArchiveBucketCount}.");
        if (scale >= 1024) { Check("archive-crosses-bucket", disk.ArchiveBucketCount >= 2, "The measured formal set crosses the fixed 1024-ID bucket boundary."); }

        for (int repeat = 0; repeat < options.Repeats; repeat++) {
            Store? store = null;
            try {
                Measure("archive-scale", "cold-owner-open", repeat, scale, root, () => store = Store.Open(root, ArchiveThreshold),
                    new() { ["cacheCondition"] = "OS cache uncontrolled/expected warm; owner freshly opened", ["fixedPayloadBytes"] = payload.Length });
                Measure("archive-scale", "inventory", repeat, scale, root, () => {
                    long count = store!.Inventory(info => {
                        if (info.PayloadLength != payload.Length || info.Tag != PayloadTag) { throw new InvalidDataException("Unexpected inventory item."); }
                    }).Unwrap();
                    Check("inventory-count", count == scale, $"scale={scale}; count={count}.");
                });
                Measure("archive-scale", "audit", repeat, scale, root, () => {
                    int files = 0;
                    long count = store!.Audit(audit => {
                        files++;
                        if (audit.HeaderPayload.Length != 24 || audit.UserFrameCount != 1) { throw new InvalidDataException("Unexpected audit file."); }
                    }).Unwrap();
                    Check("audit-count", count == scale && files == scale, $"scale={scale}; user count={count}; file callbacks={files}.");
                });
                Measure("archive-read", "repeated-random-read-and-dispose", repeat, scale, root, () => {
                    var random = new Random(1741 + repeat);
                    for (int i = 0; i < options.RandomReads; i++) {
                        CheckTime();
                        VerifyFrame(store!, addresses[random.Next(addresses.Count)], payload);
                    }
                }, new() { ["readCount"] = options.RandomReads, ["randomSeed"] = 1741 + repeat });
                foreach (bool audit in new[] { false, true }) {
                    ScanFailure(store!, root, scale, repeat, audit, cancel: false, addresses[0], payload);
                    ScanFailure(store!, root, scale, repeat, audit, cancel: true, addresses[0], payload);
                }
                Measure("archive-scale", "owner-dispose", repeat, scale, root, () => { store!.Dispose(); store = null; });
            }
            finally { store?.Dispose(); }
            CheckFilesClosable(root);
        }
    }

    private void ScanFailure(Store store, string root, int scale, int repeat, bool audit, bool cancel, FrameAddress address, byte[] payload) {
        using var cancellation = new CancellationTokenSource();
        var visitorError = new ApplicationException("intentional-resource-probe-visitor-error");
        Exception? failure = null;
        int callbacks = 0;
        Measure("scan-failure", (audit ? "audit" : "inventory") + (cancel ? "-cancel" : "-visitor-throw"), repeat, scale, root, () => {
            void Visit() {
                callbacks++;
                if (cancel) { cancellation.Cancel(); }
                else { throw visitorError; }
            }
            try {
                if (audit) { _ = store.Audit(_ => Visit(), cancellation.Token).Unwrap(); }
                else { _ = store.Inventory(_ => Visit(), cancellation.Token).Unwrap(); }
            }
            catch (Exception error) { failure = error; }
        }, new() { ["cancellationLocation"] = "first visitor callback", ["expectedFailure"] = true });
        Check("scan-failure-propagates", callbacks == 1 && (cancel ? failure is OperationCanceledException : ReferenceEquals(failure, visitorError)),
            $"audit={audit}; cancel={cancel}; callbacks={callbacks}; exception={failure?.GetType().Name ?? "none"}.");
        // No mutations to owned paths: the same owner can confirm, read and scan again after cleanup.
        Measure("scan-failure", "owner-usable-after-" + (audit ? "audit" : "inventory") + (cancel ? "-cancel" : "-throw"), repeat, scale, root, () => {
            store.ConfirmDurable();
            VerifyFrame(store, address, payload);
            long count = audit ? store.Audit(_ => { }).Unwrap() : store.Inventory(_ => { }).Unwrap();
            Check("scan-guard-released", count == scale, "Same owner confirms, reads and repeats the interrupted scan successfully.");
        });
    }

    private void Measure(string scenario, string phase, int repeat, int scale, string root, Action action, Dictionary<string, object?>? details = null) {
        CheckTime();
        var before = ResourceSnapshot.Capture();
        CheckTime();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        Exception? failure = null;
        try { action(); }
        catch (Exception error) { failure = error; }
        timer.Stop();
        long allocatedDelta = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var after = ResourceSnapshot.Capture();
        var disk = DiskSnapshot.Capture(root);
        details ??= [];
        if (failure is not null) { details["operationFailure"] = failure.ToString(); }
        report.Samples.Add(new(scenario, phase, repeat, scale, timer.Elapsed.TotalMilliseconds,
            allocatedDelta, before, after, disk, details));
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        var total = DiskSnapshot.Capture(options.WorkRoot);
        long boundedBytes = total.AllocatedFileBytes ?? total.LogicalFileBytes;
        if (boundedBytes > options.MaxDiskBytes) {
            throw new BudgetExceededException($"Actual regular-file disk bytes {boundedBytes} exceeded {options.MaxDiskBytes}; stores retained for inspection.");
        }
        CheckTime();
    }

    private void CheckTime() {
        if (elapsed.Elapsed.TotalSeconds > options.MaxSeconds) {
            throw new BudgetExceededException($"Cooperative time budget {options.MaxSeconds}s exceeded; stores retained, owner cleanup still runs.");
        }
    }

    private void Check(string name, bool condition, string detail) {
        report.Assertions.Add(new(name, condition, detail));
        if (!condition) { throw new InvalidOperationException(name + ": " + detail); }
    }

    private void Expect<TException>(Action action, string detail) where TException : Exception {
        bool rejected = false;
        try { action(); }
        catch (TException) { rejected = true; }
        Check("expected-rejection", rejected, detail);
    }

    private string NewConfiguredRoot(string name, int limit) {
        string root = Path.Combine(options.WorkRoot, name);
        Directory.CreateDirectory(root);
        WriteConfiguration(root, limit);
        return root;
    }

    private static void WriteConfiguration(string root, int limit) => File.WriteAllText(
        Path.Combine(root, "framestore.config.json"), JsonSerializer.Serialize(new { MaxOutstandingBuilders = limit }));

    private static byte[] CreatePayload(int length, string pattern) {
        byte[] payload = new byte[length];
        Array.Fill(payload, (byte)0x35);
        if (pattern == "aligned-rbf3-fence") { "RBF3"u8.CopyTo(payload); }
        return payload;
    }

    private static void VerifyFrame(Store store, FrameAddress address, byte[] payload) {
        using var read = store.ReadFrame(address).Unwrap();
        VerifyRead(read, address, payload);
    }

    private static void VerifyRead(FrameRead read, FrameAddress address, byte[] payload) {
        if (read.Address != address || read.Tag != PayloadTag || read.TailMetaLength != 0
            || read.IsTombstone || !read.PayloadAndMeta.SequenceEqual(payload)) {
            throw new InvalidDataException("Complete owned read differs from the expected public output.");
        }
    }

    private void CheckFilesClosable(string root) {
        // No owner exists here. Strict public owner reacquisition is checked separately.
        // The exclusive raw opens complement process counts with actual per-file closure evidence.
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.rbf", SearchOption.AllDirectories)) {
            CheckTime();
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            count++;
        }
        Check("disposed-data-files-exclusively-openable", true, $"{count} regular data files opened exclusively after all owners closed; no bytes modified.");
    }
}
