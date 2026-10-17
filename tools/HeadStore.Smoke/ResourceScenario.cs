using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Atelia.FrameStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Smoke;

/// <summary>Finite public consumer observations; elapsed time, allocation and RSS are not service guarantees.</summary>
internal static class ResourceScenario {
    private const int Schema = 1;
    private const int Repetitions = 3;
    private const int RootLength = 1024;
    private const int DataLength = 256;
    private const uint DataTag = 0x52534331;
    private const uint RootMagic = 0x52535231;
    private const string ManifestName = "resource-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static int Run(string[] args) {
        if (args.Length == 3 && args[0] == "resource-write"
            && int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
            && size is 16 or 64 or 256) {
            Write(Path.GetFullPath(args[1]), size);
            return 0;
        }
        if (args.Length == 2 && args[0] == "resource-read") {
            Read(Path.GetFullPath(args[1]));
            return 0;
        }
        throw new ArgumentException("Usage: resource-write <fresh-root> <16|64|256> | resource-read <root>");
    }

    private static void Write(string root, int size) {
        Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Resource fixture root must be absent or empty.");
        Directory.CreateDirectory(root);
        string[] names = TagNames(size);
        Counts counts = ExpectedCounts(size, names);
        var measurements = new List<Measurement>();
        ResourceSample before = Sample(root);
        using (var data = DataStore.Create(Path.Combine(root, "data"))) {
            using (var heads = Heads.Create(Path.Combine(root, "heads"), data)) {
                for (uint first = 1; first <= size; first += 3) {
                    HeadEntryId initial = heads.CreateHead(WritePayload(data, first, 0), out var outcome).Unwrap();
                    Confirmed(outcome);
                    Require(initial.HeadId.LocalId == first, "Automatic root numbering differs.");
                    HeadEntryId selected = heads.PublishHead(initial.HeadId, WritePayload(data, first, 1), out outcome).Unwrap();
                    Confirmed(outcome);
                    if (first + 1 <= size) {
                        HeadId child = heads.ForkHead(selected, out outcome).Unwrap();
                        Confirmed(outcome);
                        Require(child.LocalId == first + 1, "Automatic child numbering differs.");
                        HeadEntryId advanced = heads.PublishHead(child, WritePayload(data, first + 1, 10), out outcome).Unwrap();
                        Confirmed(outcome);
                        if (first + 2 <= size) {
                            HeadId grandchild = heads.ForkHead(advanced, out outcome).Unwrap();
                            Confirmed(outcome);
                            Require(grandchild.LocalId == first + 2, "Automatic grandchild numbering differs.");
                        }
                    }
                    // The first exact fork has a growing source suffix; inherited child history stays fixed.
                    for (int generation = 2; generation < 2 + SuffixLength(first, size); generation++) {
                        heads.PublishHead(initial.HeadId, WritePayload(data, first, generation), out outcome).Unwrap();
                        Confirmed(outcome);
                    }
                }
                Measure(root, "CreateTag.all.hotAndOrdinary", 1, () => {
                    for (uint id = 1; id <= size; id++) {
                        HeadId selected = heads.ListHeads().Unwrap().Single(head => head.LocalId == id);
                        HeadEntry head = heads.ReadHead(selected).Unwrap();
                        HeadSetTag tag = heads.CreateTag(names[id - 1], [selected], out var outcome).Unwrap();
                        Require(tag.Entries.Count == 1 && tag.Entries[selected] == head.Id, "Tag captured another Entry.");
                        Confirmed(outcome);
                    }
                    return size;
                }, measurements);
                Require(heads.ListHeads().Unwrap().Count == size, "Written ref count differs.");
            }
        }
        ResourceSample afterCreateDispose = Sample(root);
        RequireClosed(afterCreateDispose);
        // Writer Open performs discovery for its high water mark; record it separately from RO Open.
        using (DataStore data = Measure(root, "writer.dataOpen", 0,
            () => DataStore.Open(Path.Combine(root, "data")), measurements)) {
            using (Heads heads = Measure(root, "writer.headOpen", 0,
                () => Heads.Open(Path.Combine(root, "heads"), data), measurements)) {
                Require(!data.IsReadOnly && !heads.IsReadOnly, "Writer reopen unexpectedly produced a read-only owner.");
                Measure(root, "writer.ListHeads", 1, () => {
                    int count = heads.ListHeads().Unwrap().Count;
                    Require(count == size, "Writer reopen discovery differs.");
                    return count;
                }, measurements);
            }
        }
        ResourceSample after = Sample(root);
        RequireClosed(after);
        File.WriteAllText(Path.Combine(root, ManifestName), JsonSerializer.Serialize(new Manifest(Schema, size, names), JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", scenario = "resource-write", pid = Environment.ProcessId, schema = Schema, size,
            counts, measurements, before, afterCreateDispose, after, semantics = Semantics()
        }, JsonOptions));
    }

    private static void Read(string root) {
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, ManifestName)), JsonOptions)
            ?? throw new InvalidDataException("Missing resource manifest.");
        Require(manifest.Schema == Schema && manifest.Size is 16 or 64 or 256, "Unknown resource fixture schema/size.");
        int size = manifest.Size;
        string[] names = manifest.TagNames;
        Require(names.SequenceEqual(TagNames(size)), "Resource fixture tag vector differs.");
        Counts counts = ExpectedCounts(size, names);
        var nameIds = names.Select((name, index) => (name, id: (uint)(index + 1)))
            .ToDictionary(item => item.name, item => item.id, StringComparer.Ordinal);
        var measurements = new List<Measurement>();
        var checkpoints = new Dictionary<string, ResourceSample>();
        // Sampler/BCL initialization precedes the first retained checkpoint. No forced GC is used.
        Sample(root);
        checkpoints.Add("beforeOpen", Sample(root));
        using (DataStore data = Measure(root, "traversal.dataOpenReadOnly", 0,
            () => DataStore.OpenReadOnly(Path.Combine(root, "data")), measurements)) {
            checkpoints.Add("afterDataOpen", Sample(root));
            using (Heads heads = Measure(root, "traversal.headOpenReadOnly", 0,
                () => Heads.OpenReadOnly(Path.Combine(root, "heads"), data), measurements)) {
                Require(data.IsReadOnly && heads.IsReadOnly, "Resource traversal owners must be read-only.");
                ResourceSample opened = Sample(root);
                checkpoints.Add("afterVersionOpen", opened);
                for (int iteration = 1; iteration <= Repetitions; iteration++) {
                    IReadOnlyList<HeadId> refs = Measure(root, "ListHeads", iteration, () => {
                        IReadOnlyList<HeadId> result = heads.ListHeads().Unwrap();
                        Require(result.Count == size && result.Select(id => id.LocalId).Order().SequenceEqual(Enumerable.Range(1, size).Select(id => (uint)id)), "Head discovery differs.");
                        return result;
                    }, measurements);
                    IReadOnlyList<HeadOriginInfo> forks = Measure(root, "ListHeadOrigins", iteration, () => {
                        IReadOnlyList<HeadOriginInfo> result = heads.ListHeadOrigins(size).Unwrap();
                        Require(result.Count == counts.Forks, "Fork declaration count differs.");
                        return result;
                    }, measurements);
                    HeadEntryId[] currentEntries = Measure(root, "ReadHeadHistory.all", iteration,
                        () => ValidateHistories(data, heads, refs, forks, size, counts.HistoryEntries), measurements);
                    Measure(root, "ScanTags", iteration, () => {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        var buckets = new Dictionary<int, int>();
                        HeadSetTagScanEnd end = heads.ScanTags(info => {
                            Require(nameIds.TryGetValue(info.Name, out uint id) && seen.Add(info.Name), "Unexpected or repeated scanned tag.");
                            ValidateInfo(info, currentEntries[id - 1]);
                            int bucket = TagBucket(info.Name);
                            buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
                            return true;
                        }).Unwrap();
                        Require(end == HeadSetTagScanEnd.Complete && seen.Count == size, "Tag scan did not complete.");
                        Require(buckets.Count == counts.ActualTagBuckets && buckets.Values.Max() == counts.MaxTagRecordsPerBucket, "Observed tag distribution differs.");
                        return seen.Count;
                    }, measurements);
                    Measure(root, "Audit", iteration, () => {
                        HeadStoreAudit result = heads.Audit().Unwrap();
                        Require(result.HeadCount == counts.Heads && result.EntryCount == counts.LocalEntries
                            && result.OriginCount == counts.Forks && result.TagCount == counts.Tags
                            && result.TagBucketCount == counts.ActualTagBuckets, "Complete HeadStore audit counts differ.");
                        return result;
                    }, measurements);
                    // Even IDs form the hot bucket. First/last hot and one ordinary name are bounded samples.
                    foreach (uint id in new uint[] { 2, (uint)size, 1 }) {
                        Measure(root, $"ReadTagInfo.ref-{id}", iteration, () => {
                            HeadSetTagInfo info = heads.ReadTagInfo(names[id - 1]).Unwrap();
                            Require(info.Name == names[id - 1], "Named tag metadata returned another name.");
                            ValidateInfo(info, currentEntries[id - 1]);
                            return info;
                        }, measurements);
                        Measure(root, $"ReadTag.ref-{id}", iteration, () => {
                            HeadSetTag tag = heads.ReadTag(names[id - 1]).Unwrap();
                            Require(tag.Entries.Count == 1 && tag.Entries[refs.Single(head => head.LocalId == id)] == currentEntries[id - 1],
                                "Tag member differs from the independently verified history.");
                            return tag;
                        }, measurements);
                    }
                    ResourceSample traversed = Sample(root);
                    checkpoints.Add($"afterTemporaryTraversal{iteration}", traversed);
                    RequireSameVersionDescriptors(opened, traversed, "Temporary traversal retained HeadStore descriptors.");
                }
                Measure(root, "FrameStore.Audit", 1, () => {
                    long frames = data.Audit(static _ => { }).Unwrap();
                    Require(frames == counts.DataFrames, "Data frame count differs.");
                    return frames;
                }, measurements);
            }
            checkpoints.Add("afterTraversalVersionDispose", Sample(root));
            RequireNoVersionDescriptors(checkpoints["afterTraversalVersionDispose"]);
        }
        checkpoints.Add("afterTraversalDataDispose", Sample(root));
        RequireClosed(checkpoints["afterTraversalDataDispose"]);

        // A second fresh owner isolates the documented owner cache from the traversal owner above.
        using (DataStore data = Measure(root, "cache.dataOpenReadOnly", 0,
            () => DataStore.OpenReadOnly(Path.Combine(root, "data")), measurements)) {
            using (Heads heads = Measure(root, "cache.headOpenReadOnly", 0,
                () => Heads.OpenReadOnly(Path.Combine(root, "heads"), data), measurements)) {
                ResourceSample cacheOpened = Sample(root);
                checkpoints.Add("beforeReadHeadCache", cacheOpened);
                ResourceSample? firstPass = null;
                for (int iteration = 1; iteration <= Repetitions; iteration++) {
                    Measure(root, "ReadHead.all.ownerCache", iteration, () => {
                        for (uint id = 1; id <= size; id++) {
                            HeadEntry entry = heads.ReadHead(id).Unwrap();
                            ExpectedEntry expected = ExpectedHistory(id, size)[0];
                            Require(entry.Id.HeadId.LocalId == expected.LocalHeadId, "ReadHead returned an unexpected Entry owner.");
                            ValidatePayload(data, entry.Payload, expected.Creator, expected.Generation);
                        }
                        return size;
                    }, measurements);
                    ResourceSample cached = Sample(root);
                    checkpoints.Add($"afterReadHeadCache{iteration}", cached);
                    if (firstPass is null) { firstPass = cached; }
                    else { RequireSameVersionDescriptors(firstPass, cached, "Repeated ReadHead grew the owner cache."); }
                    if (cacheOpened.HeadFileDescriptors is int before && cached.HeadFileDescriptors is int after) {
                        Require(after - before == size, "ReadHead cache does not contain exactly one reader per ref.");
                    }
                }
            }
            checkpoints.Add("afterCacheVersionDispose", Sample(root));
            RequireNoVersionDescriptors(checkpoints["afterCacheVersionDispose"]);
        }
        checkpoints.Add("afterCacheDataDispose", Sample(root));
        RequireClosed(checkpoints["afterCacheDataDispose"]);
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", scenario = "resource-read", pid = Environment.ProcessId, schema = Schema, size,
            repetitions = Repetitions, counts, measurements, checkpoints, semantics = Semantics()
        }, JsonOptions));
    }

    private static HeadEntryId[] ValidateHistories(DataStore data, Heads heads, IReadOnlyList<HeadId> refs,
        IReadOnlyList<HeadOriginInfo> forks, int size, long expectedTotal) {
        var histories = new Dictionary<uint, List<HeadEntryId>>();
        var currentEntries = new HeadEntryId[size];
        long total = 0;
        foreach (HeadId id in refs) {
            ExpectedEntry[] expected = ExpectedHistory(id.LocalId, size);
            var entryIds = new List<HeadEntryId>(expected.Length);
            HeadHistoryEnd end = heads.ReadHeadHistory(id, new HeadHistoryBudget(expected.Length + 1L, 100L + expected.Length * 4L), entry => {
                Require(entryIds.Count < expected.Length, "History visited the source's later suffix.");
                ExpectedEntry item = expected[entryIds.Count];
                Require(entry.Id.HeadId.LocalId == item.LocalHeadId, "History entryId ownership differs.");
                ValidatePayload(data, entry.Payload, item.Creator, item.Generation);
                entryIds.Add(entry.Id);
                return true;
            }).Unwrap();
            Require(end == HeadHistoryEnd.Complete && entryIds.Count == expected.Length && entryIds.Distinct().Count() == entryIds.Count, "History closure/unique entryId count differs.");
            currentEntries[id.LocalId - 1] = entryIds[0];
            histories.Add(id.LocalId, entryIds);
            total += entryIds.Count;
        }
        Require(total == expectedTotal, "Total inherited history count differs.");
        Require(forks.Select(fork => fork.ChildHeadId.LocalId).Order().SequenceEqual(
            Enumerable.Range(1, size).Select(id => (uint)id).Where(id => id % 3 != 1)), "Fork child set differs.");
        foreach (HeadOriginInfo fork in forks) {
            uint child = fork.ChildHeadId.LocalId;
            uint first = ((child - 1) / 3) * 3 + 1;
            uint source = child % 3 == 2 ? first : first + 1;
            Require(child % 3 != 1 && fork.SourceHeadId.LocalId == source, "Declared fork parent differs.");
            List<HeadEntryId> sourceHistory = histories[source];
            HeadEntryId selected = child % 3 == 2 ? sourceHistory[^2] : sourceHistory[0];
            Require(fork.SourceHeadId == selected.HeadId && fork.SourceEntryTicket.Packed == selected.Ticket.Packed, "Declared exact fork point differs.");
            List<HeadEntryId> childHistory = histories[child];
            int ownLength = child % 3 == 2 ? 1 : 0;
            Require(childHistory[ownLength] == selected, "Inherited prefix starts at a different exact entryId.");
            int selectedIndex = sourceHistory.IndexOf(selected);
            Require(childHistory.Skip(ownLength).SequenceEqual(sourceHistory.Skip(selectedIndex)), "Inherited prefix closure differs.");
        }
        return currentEntries;
    }

    private static ExpectedEntry[] ExpectedHistory(uint id, int size) {
        uint first = ((id - 1) / 3) * 3 + 1;
        if (id % 3 == 1) {
            return Enumerable.Range(0, 2 + SuffixLength(first, size)).Reverse()
                .Select(generation => new ExpectedEntry(first, first, generation)).ToArray();
        }
        var child = new[] {
            new ExpectedEntry(first + 1, first + 1, 10),
            new ExpectedEntry(first, first, 1), new ExpectedEntry(first, first, 0)
        };
        return child;
    }

    private static int SuffixLength(uint first, int size) { return first == 1 ? size : 2; }

    private static Counts ExpectedCounts(int size, string[] names) {
        long entries = 0, forks = 0, histories = 0;
        for (uint id = 1; id <= size; id++) {
            entries += id % 3 == 1 ? 2 + SuffixLength(id, size) : id % 3 == 2 ? 1 : 0;
            forks += id % 3 == 1 ? 0 : 1;
            histories += ExpectedHistory(id, size).Length;
        }
        int[] bucketCounts = names.GroupBy(TagBucket).Select(group => group.Count()).ToArray();
        return new Counts(size, entries, forks, size, histories, entries, bucketCounts.Length, bucketCounts.Max());
    }

    private static byte[] WritePayload(DataStore data, uint creator, int generation) {
        byte[] record = Record(DataLength, DataTag, creator, generation);
        FrameAddress address = data.Append(DataTag, record).Unwrap();
        byte[] root = Record(RootLength, RootMagic, creator, generation);
        Require(address.TryWrite(root.AsSpan(16, FrameAddress.EncodedSize)), "Cannot encode a public data address.");
        return root;
    }

    private static byte[] Record(int length, uint magic, uint creator, int generation) {
        var bytes = new byte[length];
        for (int offset = 0; offset < length; offset++) { bytes[offset] = Pattern(offset, creator, generation); }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), creator);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), Schema);
        return bytes;
    }

    private static void ValidatePayload(DataStore data, ReadOnlySpan<byte> bytes, uint creator, int generation) {
        ValidateRecord(bytes, RootLength, RootMagic, creator, generation, 16 + FrameAddress.EncodedSize);
        Require(FrameAddress.TryRead(bytes.Slice(16, FrameAddress.EncodedSize), out FrameAddress address), "Root has an invalid data address.");
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        Require(frame.Address == address && frame.Tag == DataTag && frame.TailMetaLength == 0 && !frame.IsTombstone, "Referenced data envelope differs.");
        ValidateRecord(frame.PayloadAndMeta, DataLength, DataTag, creator, generation, 16);
    }

    private static void ValidateRecord(ReadOnlySpan<byte> bytes, int length, uint magic, uint creator, int generation, int patternStart) {
        Require(bytes.Length == length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == magic
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == creator
            && BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) == generation
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == Schema, "Application bytes differ.");
        for (int offset = patternStart; offset < length; offset++) {
            Require(bytes[offset] == Pattern(offset, creator, generation), "Application payload pattern differs.");
        }
    }

    private static byte Pattern(int offset, uint creator, int generation) { return unchecked((byte)(offset * 17 + creator * 3 + generation)); }

    private static void ValidateInfo(HeadSetTagInfo info, HeadEntryId head) {
        Require(info.EntryCount == 1, "Tag metadata member count differs.");
    }

    private static string[] TagNames(int size) {
        var names = new string[size];
        int target = TagBucket("resource/hot");
        for (int id = 1; id <= size; id++) {
            if ((id & 1) != 0) { names[id - 1] = FormattableString.Invariant($"resource/ref/{id}"); continue; }
            // Reproduce only the documented F-VS-TAG-ROUTING algorithm, not an internal API.
            for (int candidate = 0; candidate < 100_000; candidate++) {
                string name = FormattableString.Invariant($"resource/hot/{id}/{candidate}");
                if (TagBucket(name) == target) { names[id - 1] = name; break; }
            }
            Require(names[id - 1] is not null, "Finite hot-bucket name search exhausted.");
        }
        return names;
    }

    private static int TagBucket(string name) {
        ulong hash = 14695981039346656037UL;
        foreach (char codeUnit in name) {
            hash = unchecked((hash ^ (byte)codeUnit) * 1099511628211UL);
            hash = unchecked((hash ^ (byte)(codeUnit >> 8)) * 1099511628211UL);
        }
        return (int)((hash ^ (hash >> 10)) & 1023);
    }

    private static T Measure<T>(string root, string operation, int iteration, Func<T> action, List<Measurement> measurements) {
        ResourceSample before = Sample(root);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        T result = action();
        long stopped = Stopwatch.GetTimestamp();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        ResourceSample after = Sample(root);
        measurements.Add(new Measurement(operation, iteration, Stopwatch.GetElapsedTime(started, stopped).TotalMilliseconds,
            allocatedAfter - allocatedBefore, before, after));
        return result;
    }

    private static ResourceSample Sample(string root) {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSet = process.WorkingSet64;
        int? handles = OperatingSystem.IsWindows() ? process.HandleCount : null;
        int? descriptors = null, rootDescriptors = null, headDescriptors = null, dataDescriptors = null;
        if (OperatingSystem.IsLinux()) {
            descriptors = rootDescriptors = headDescriptors = dataDescriptors = 0;
            string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            // GetFileSystemEntries closes its own directory fd before readlink; vanished entries are omitted.
            foreach (string entry in Directory.GetFileSystemEntries("/proc/self/fd")) {
                string? target;
                try { target = new FileInfo(entry).LinkTarget; }
                catch (FileNotFoundException) { continue; } // A sampled fd can close between enumeration and readlink.
                if (target is null) { continue; }
                descriptors++;
                if (target.StartsWith(prefix, StringComparison.Ordinal)) {
                    rootDescriptors++;
                    if (target.StartsWith(prefix + "heads/", StringComparison.Ordinal)) { headDescriptors++; }
                    if (target.StartsWith(prefix + "data/", StringComparison.Ordinal)) { dataDescriptors++; }
                }
            }
        }
        return new ResourceSample(handles, descriptors, rootDescriptors, headDescriptors, dataDescriptors,
            workingSet, GC.GetTotalMemory(forceFullCollection: false));
    }

    private static object Semantics() {
        return new {
            resourceCounter = OperatingSystem.IsWindows() ? "Process.HandleCount: all OS handle types, process-wide noise possible"
                : "Linux /proc/self/fd readlink sample: fd counts, not Windows handle equivalents",
            rootDescriptors = "Linux only: current fd symlink targets under the fixture path; directory sampler's closed fd omitted",
            allocations = "GC.GetAllocatedBytesForCurrentThread delta: synchronous public operation plus consumer validation/callback/retained results; excludes sampler and other threads",
            timing = "Stopwatch interval: operation plus consumer validation; first calls may include JIT/runtime initialization, later calls use warm OS/runtime caches",
            writeTiming = "Writer Open reopens new owners in the writing process with warm runtime/OS caches; CreateTag batch includes ReadHead head capture; ForkHead is fixture setup and is not separately timed",
            rss = "WorkingSet64 observation only; managedHeapBytes uses GetTotalMemory(false); neither is a peak or pass/fail limit; no forced GC",
            ownerCache = "Fresh traversal owner uses temporary readers; a separate fresh ReadHead owner retains one ref reader per distinct ref until Dispose",
            closure = "Application opaque bytes and every referenced data frame checked; Audit separately validates storage structure",
            readOnlyFiles = "Caller must compare complete fixture file list and hashes before/after resource-read",
            scope = "Finite vectors only, no throughput promise, native fault/power-loss or package-consumer qualification"
        };
    }

    private static void RequireSameVersionDescriptors(ResourceSample before, ResourceSample after, string message) {
        if (before.HeadFileDescriptors is int first && after.HeadFileDescriptors is int second) { Require(first == second, message); }
    }

    private static void RequireNoVersionDescriptors(ResourceSample sample) {
        if (sample.HeadFileDescriptors is int count) { Require(count == 0, "HeadStore Dispose retained fixture descriptors."); }
    }

    private static void RequireClosed(ResourceSample sample) {
        if (sample.RootFileDescriptors is int count) { Require(count == 0, "Owner Dispose retained fixture descriptors."); }
    }

    private static void Confirmed(PublicationOutcome outcome) { Require(outcome == PublicationOutcome.Confirmed, "Successful publication was not Confirmed."); }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }

    private sealed record Manifest(int Schema, int Size, string[] TagNames);
    private readonly record struct ExpectedEntry(uint LocalHeadId, uint Creator, int Generation);
    private sealed record Counts(long Heads, long LocalEntries, long Forks, long Tags, long HistoryEntries,
        long DataFrames, int ActualTagBuckets, int MaxTagRecordsPerBucket);
    private sealed record ResourceSample(int? ProcessHandles, int? ProcessFileDescriptors, int? RootFileDescriptors,
        int? HeadFileDescriptors, int? DataFileDescriptors, long WorkingSetBytes, long ManagedHeapBytes);
    private sealed record Measurement(string Operation, int Iteration, double ElapsedMs, long AllocatedBytes,
        ResourceSample Before, ResourceSample After);
}
