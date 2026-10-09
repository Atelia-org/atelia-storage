using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using Atelia;
using Atelia.Data;
using Atelia.Rbf;

// Research probe: synthetic uint RefId and int RootMap value, not a VersionStore codec.
static class Probe {
    const uint Root = 1, Conditional = 2;
    static string work = "";
    static int serial, prefixCases, byteCases, truncated, completedTail, readonlyRejected;
    static bool preferExact;
    static int exactSuccesses, fallbackScans, lookupComparisons, variablePrefixCases;

    sealed class FrameReadFailure(AteliaError error) : Exception(error.ToString()) {
        public string ErrorCode { get; } = error.ErrorCode;
    }
    static RbfPooledFrame ReadChecked(RbfFrameInfo info) {
        var result = info.ReadPooledFrame();
        if (result.IsFailure) throw new FrameReadFailure(result.Error!);
        return result.Value!;
    }

    static void Check(bool value, string message) {
        if (!value) throw new InvalidOperationException(message);
    }
    static byte[] Encode(int root, Dictionary<int, SizedPtr>? table = null, int extra = 0) {
        var bytes = new byte[table is null ? 4 + extra : 8 + 12 * table.Count + extra];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, root);
        if (table is not null) {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), table.Count);
            int offset = 8;
            foreach (var entry in table.OrderBy(e => e.Key)) {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)entry.Key);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 4), entry.Value.Packed);
                offset += 12;
            }
        }
        return bytes;
    }
    static Dictionary<int, SizedPtr> Decode(ReadOnlySpan<byte> bytes) {
        Check(bytes.Length >= 8, "short conditional payload");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        Check(count >= 2 && count <= 4 && bytes.Length >= 8 + 12 * count, "invalid count");
        var result = new Dictionary<int, SizedPtr>();
        for (int i = 0; i < count; i++) {
            int offset = 8 + 12 * i;
            int id = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
            Check(result.TryAdd(id, SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 4)..]))), "duplicate member");
        }
        return result;
    }
    static bool Equal(Dictionary<int, SizedPtr> a, Dictionary<int, SizedPtr> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var t) && t == e.Value);

    // Baseline public API implementation: locate actual slot through the true reverse chain.
    static RbfFrameInfo? FindSlot(IRbfFile file, long offset) {
        var scan = file.ScanReverse(showTombstone: true).GetEnumerator();
        if (offset == file.TailOffset) return null;
        Check(offset < file.TailOffset, "future slot gap");
        {
            while (scan.MoveNext()) {
                var info = scan.Current;
                if (info.Ticket.Offset == offset) return info;
                Check(info.Ticket.Offset > offset, "slot is not a chain boundary");
            }
            if (scan.TerminationError is not null) throw new InvalidDataException(scan.TerminationError.ToString());
            throw new InvalidDataException("slot not found");
        }
    }
    static RbfPooledFrame? ReadSlot(IRbfFile file, SizedPtr wanted) {
        if (!preferExact) {
            var actual = FindSlot(file, wanted.Offset);
            return actual.HasValue ? ReadChecked(actual.Value) : null;
        }
        // Creating the sequence checks Idle/lifecycle/fault without scanning bytes.
        _ = file.ScanReverse(showTombstone: true);
        if (wanted.Offset == file.TailOffset) return null;
        Check(wanted.Offset < file.TailOffset, "future slot gap");
        var exact = file.ReadPooledFrame(wanted);
        if (exact.IsSuccess) { exactSuccesses++; return exact.Value!; }
        var error = exact.Error!;
        if (error.ErrorCode is not ("Rbf.ArgumentError" or "Rbf.FramingError" or "Rbf.CrcMismatch"))
            throw new FrameReadFailure(error);
        fallbackScans++;
        var located = FindSlot(file, wanted.Offset);
        Check(located.HasValue, "slot disappeared during serial lookup");
        if (located!.Value.Ticket == wanted) throw new FrameReadFailure(error);
        return ReadChecked(located.Value);
    }
    static bool Qualify(Dictionary<int, IRbfFile> files, int local, SizedPtr ticket, Dictionary<int, SizedPtr> table) {
        Check(table.TryGetValue(local, out var self) && self == ticket, "self ticket mismatch");
        foreach (var entry in table) {
            Check(files.ContainsKey(entry.Key), "missing ref");
            // Check actual content before labelling it a legitimate replacement.
            using var frame = ReadSlot(files[entry.Key], entry.Value);
            if (frame is null) return false;
            Check(!frame.IsTombstone, "unexpected tombstone");
            if (frame.Tag == Root) {
                Check(frame.PayloadAndMeta.Length >= 4, "invalid root");
                return false;
            }
            Check(frame.Tag == Conditional, "unknown kind");
            var peerTable = Decode(frame.PayloadAndMeta);
            Check(peerTable.TryGetValue(entry.Key, out var peerSelf) && peerSelf == frame.Ticket, "peer self mismatch");
            if (frame.Ticket != entry.Value || !Equal(table, peerTable)) return false;
        }
        return true;
    }
    static int Current(Dictionary<int, IRbfFile> files, int id) {
        var scan = files[id].ScanReverse(showTombstone: true).GetEnumerator();
        {
            while (scan.MoveNext()) {
                var info = scan.Current;
                using var frame = ReadChecked(info);
                Check(!frame.IsTombstone, "unexpected tombstone");
                int root = BinaryPrimitives.ReadInt32LittleEndian(frame.PayloadAndMeta);
                if (frame.Tag == Root) return root;
                Check(frame.Tag == Conditional, "unknown kind");
                if (Qualify(files, id, info.Ticket, Decode(frame.PayloadAndMeta))) return root;
            }
            if (scan.TerminationError is not null) throw new InvalidDataException(scan.TerminationError.ToString());
            throw new InvalidDataException("initial root missing");
        }
    }
    static IEnumerable<int[]> Orders(int[] values) {
        if (values.Length == 0) { yield return []; yield break; }
        foreach (int first in values)
            foreach (int[] rest in Orders(values.Where(v => v != first).ToArray()))
                yield return new[] { first }.Concat(rest).ToArray();
    }
    static string[] Paths(string name, int k) {
        string dir = Path.Combine(work, $"{serial++:D5}-{name}");
        Directory.CreateDirectory(dir);
        return Enumerable.Range(0, k).Select(i => Path.Combine(dir, $"{i}.rbf")).ToArray();
    }
    static Dictionary<int, IRbfFile> Open(string[] paths) {
        var files = new Dictionary<int, IRbfFile>();
        try {
            for (int i = 0; i < paths.Length; i++) {
                files[i] = RbfFile.OpenExisting(paths[i], out var report, RbfCacheMode.Off);
                if (report.Action == RbfTailRecoveryAction.Truncated) truncated++;
                if (report.Action == RbfTailRecoveryAction.CompletedTail) completedTail++;
            }
            return files;
        }
        catch { foreach (var f in files.Values) f.Dispose(); throw; }
    }
    static void Close(Dictionary<int, IRbfFile> files) { foreach (var f in files.Values) f.Dispose(); }
    static (byte[][] Old, byte[][] Full, SizedPtr[] Tickets) Template(int k, int[] order, int[]? extras = null) {
        var paths = Paths("template", k);
        var files = paths.Select((p, i) => (p, i)).ToDictionary(e => e.i, e => RbfFile.CreateNew(e.p));
        var builders = new RbfFrameBuilder[k];
        var tickets = new SizedPtr[k];
        try {
            for (int i = 0; i < k; i++) { files[i].Append(Root, Encode(0)).Unwrap(); files[i].DurableFlush(); }
            Close(files);
            var old = paths.Select(File.ReadAllBytes).ToArray();
            files = Open(paths);
            for (int i = 0; i < k; i++) {
                int length = 8 + 12 * k + (extras?[i] ?? 0);
                var size = RbfFile.MeasureWriteSize(length).Unwrap();
                var predicted = SizedPtr.Create(files[i].TailOffset, size.FrameLength);
                builders[i] = files[i].BeginAppend(length, 0, out tickets[i]);
                Check(tickets[i] == predicted, "prediction != Begin");
            }
            var table = tickets.Select((t, i) => (t, i)).ToDictionary(e => e.i, e => e.t);
            foreach (int i in order) {
                builders[i].PayloadAndMeta.Write(Encode(i + 1, table, extras?[i] ?? 0));
                Check(builders[i].EndAppend(Conditional).Unwrap() == tickets[i], "Begin != End");
            }
            for (int i = 0; i < k; i++) files[i].DurableFlush();
            foreach (var b in builders) b.Dispose();
            Close(files);
            files = new();
            return (old, paths.Select(File.ReadAllBytes).ToArray(), tickets);
        }
        finally { foreach (var b in builders) b.Dispose(); Close(files); }
    }
    static void CheckImages(byte[][] images, bool? expectedCommitted) {
        var paths = Paths("case", images.Length);
        for (int i = 0; i < paths.Length; i++) File.WriteAllBytes(paths[i], images[i]);
        foreach (var path in paths) {
            try { using var ro = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off); }
            catch (InvalidDataException) { readonlyRejected++; }
        }
        var files = Open(paths);
        try {
            var current = Enumerable.Range(0, paths.Length).Select(i => Current(files, i)).ToArray();
            preferExact = true;
            try {
                var exactCurrent = Enumerable.Range(0, paths.Length).Select(i => Current(files, i)).ToArray();
                Check(current.SequenceEqual(exactCurrent), "exact lookup disagrees with true-chain baseline");
                lookupComparisons += paths.Length;
            }
            finally { preferExact = false; }
            bool allOld = current.All(v => v == 0);
            bool allNew = current.Select((v, i) => v == i + 1).All(v => v);
            Check(allOld || allNew, "partial transaction visible");
            if (expectedCommitted.HasValue) Check(allNew == expectedCommitted.Value, "wrong selected group");
        }
        finally { Close(files); }
    }
    static void Prefixes() {
        for (int k = 2; k <= 4; k++) {
            foreach (var order in Orders(Enumerable.Range(0, k).ToArray())) {
                var t = Template(k, order);
                for (int count = 0; count <= k; count++) {
                    var completed = order.Take(count).ToHashSet();
                    CheckImages(Enumerable.Range(0, k).Select(i => completed.Contains(i) ? t.Full[i] : t.Old[i]).ToArray(), count == k);
                    prefixCases++;
                }
                if (k != 2) continue;
                for (int count = 0; count < k; count++) {
                    int partial = order[count];
                    int appendLength = t.Full[partial].Length - t.Old[partial].Length;
                    for (int cut = 0; cut <= appendLength; cut++) {
                        var completed = order.Take(count).ToHashSet();
                        var images = Enumerable.Range(0, k).Select(i => i == partial
                            ? t.Full[i][..(t.Old[i].Length + cut)]
                            : completed.Contains(i) ? t.Full[i] : t.Old[i]).ToArray();
                        // RBF may CompleteTail near the end; judge all-old/all-new, not fsync status.
                        CheckImages(images, null);
                        byteCases++;
                    }
                }
            }
        }
    }
    static object Replacement(int delta, bool anotherConditional) {
        var paths = Paths("reuse", 2);
        var files = paths.Select((p, i) => (p, i)).ToDictionary(e => e.i, e => RbfFile.CreateNew(e.p));
        SizedPtr oldA, oldB;
        try {
            foreach (var f in files.Values) { f.Append(Root, Encode(0)).Unwrap(); f.DurableFlush(); }
            var a = files[0].BeginAppend(32, 0, out oldA);
            var b = files[1].BeginAppend(32, 0, out oldB);
            var table = new Dictionary<int, SizedPtr> { [0] = oldA, [1] = oldB };
            a.PayloadAndMeta.Write(Encode(1, table)); a.EndAppend(Conditional).Unwrap();
            b.Dispose(); a.Dispose();
        }
        finally { Close(files); }
        files = Open(paths);
        string staleRead;
        SizedPtr replacement;
        try {
            if (anotherConditional) {
                var a = files[0].BeginAppend(32 + delta, 0, out var newA);
                var b = files[1].BeginAppend(32 + delta, 0, out var newB);
                var table = new Dictionary<int, SizedPtr> { [0] = newA, [1] = newB };
                a.PayloadAndMeta.Write(Encode(2, table, delta)); b.PayloadAndMeta.Write(Encode(2, table, delta));
                a.EndAppend(Conditional).Unwrap(); replacement = b.EndAppend(Conditional).Unwrap();
                a.Dispose(); b.Dispose();
                Check(newA != oldA, "completed anchor reused");
            }
            else replacement = files[1].Append(Root, Encode(2, extra: 28 + delta)).Unwrap();
            Check(replacement.Offset == oldB.Offset, "missing position was not reused");
            var stale = files[1].ReadPooledFrame(oldB);
            staleRead = stale.IsSuccess ? "Success" : stale.Error!.ErrorCode;
            if (stale.IsSuccess) stale.Value.Dispose();
            using var original = files[0].ReadPooledFrame(oldA).Unwrap();
            Check(!Qualify(files, 0, oldA, Decode(original.PayloadAndMeta)), "old group resurrected");
            Check(Current(files, 0) == (anotherConditional ? 2 : 0), "wrong A after reuse");
            Check(Current(files, 1) == 2, "wrong B after reuse");
            foreach (var f in files.Values) f.DurableFlush();
        }
        finally { Close(files); }
        return new { delta, anotherConditional, sameTicket = replacement == oldB, staleRead };
    }
    static object CorruptionAndLaterUpdate() {
        var t = Template(2, [0, 1]);
        var paths = Paths("later-update", 2);
        for (int i = 0; i < 2; i++) File.WriteAllBytes(paths[i], t.Full[i]);
        var files = Open(paths);
        try {
            files[1].Append(Root, Encode(9)).Unwrap(); files[1].DurableFlush();
            using var original = files[0].ReadPooledFrame(t.Tickets[0]).Unwrap();
            Check(Qualify(files, 0, t.Tickets[0], Decode(original.PayloadAndMeta)), "later update invalidated old group");
            Check(Current(files, 0) == 1 && Current(files, 1) == 9, "later ordinary update selected wrong state");
            var moved = files[0].Append(Conditional, Encode(3, Decode(original.PayloadAndMeta))).Unwrap();
            bool rejectedSelf = false;
            try { Qualify(files, 0, moved, Decode(original.PayloadAndMeta)); }
            catch (InvalidOperationException) { rejectedSelf = true; }
            Check(rejectedSelf, "copied old table accepted at new self position");
        }
        finally { Close(files); }
        paths = Paths("bad-crc", 2);
        for (int i = 0; i < 2; i++) File.WriteAllBytes(paths[i], t.Full[i]);
        var damaged = File.ReadAllBytes(paths[1]);
        // Probe corruption injection within encoded payload; no production layout logic.
        damaged[checked((int)t.Tickets[1].Offset + 8)] ^= 1;
        File.WriteAllBytes(paths[1], damaged);
        files = Open(paths);
        string error;
        try {
            var bad = files[1].ReadPooledFrame(t.Tickets[1]);
            Check(bad.IsFailure, "bad peer CRC accepted"); error = bad.Error!.ErrorCode;
            foreach (bool fast in new[] { false, true }) {
                preferExact = fast;
                bool rejected = false;
                try { Current(files, 0); }
                catch (FrameReadFailure ex) {
                    Check(ex.ErrorCode == error, "Current propagated a different frame error");
                    rejected = true;
                }
                Check(rejected, "bad peer became silent fallback");
            }
        }
        finally { preferExact = false; Close(files); }
        return new { laterOrdinaryUpdatePreservesGroup = true, copiedSelfRejected = true, peerContentError = error, propagatedError = error, errorPropagated = true };
    }
    static Dictionary<int, IRbfFile> CreateFiles(string[] paths) {
        var files = paths.Select((p, i) => (p, i)).ToDictionary(e => e.i, e => RbfFile.CreateNew(e.p));
        foreach (var file in files.Values) { file.Append(Root, Encode(0)).Unwrap(); file.DurableFlush(); }
        return files;
    }
    // A fresh private batch: predict and encode every member before sequential Begin/End.
    static Dictionary<int, SizedPtr> AppendFresh(Dictionary<int, IRbfFile> files, int[] ids, int root, int extra) {
        int length = 8 + 12 * ids.Length + extra;
        int frameLength = RbfFile.MeasureWriteSize(length).Unwrap().FrameLength;
        var table = ids.ToDictionary(id => id, id => SizedPtr.Create(files[id].TailOffset, frameLength));
        var payloads = ids.ToDictionary(id => id, id => Encode(root, table, extra));
        foreach (int id in ids) {
            var builder = files[id].BeginAppend(length, 0, out var ticket);
            try {
                Check(ticket == table[id], "sequential Begin changed prediction");
                builder.PayloadAndMeta.Write(payloads[id]);
                Check(builder.EndAppend(Conditional).Unwrap() == ticket, "sequential End changed ticket");
            }
            finally { builder.Dispose(); }
        }
        foreach (int id in ids) files[id].DurableFlush();
        return table;
    }
    static Dictionary<int, SizedPtr> IncompleteAB(Dictionary<int, IRbfFile> files) {
        var a = files[0].BeginAppend(40, 0, out var ta);
        var b = files[1].BeginAppend(40, 0, out var tb);
        var table = new Dictionary<int, SizedPtr> { [0] = ta, [1] = tb };
        try { a.PayloadAndMeta.Write(Encode(1, table, 8)); a.EndAppend(Conditional).Unwrap(); }
        finally { b.Dispose(); a.Dispose(); }
        return table;
    }
    static void AssertCurrentBoth(Dictionary<int, IRbfFile> files, params int[] expected) {
        foreach (bool fast in new[] { false, true }) {
            preferExact = fast;
            try { Check(expected.SequenceEqual(Enumerable.Range(0, expected.Length).Select(id => Current(files, id))), "unexpected current vector"); }
            finally { preferExact = false; }
        }
    }
    static object RefinementChecks() {
        foreach (var order in Orders([0, 1, 2])) {
            var variable = Template(3, order, [0, 5, 17]);
            Check(variable.Tickets.Select(t => t.Length).Distinct().Count() == 3, "variable lengths not exercised");
            for (int count = 0; count <= 3; count++) {
                var completed = order.Take(count).ToHashSet();
                CheckImages(Enumerable.Range(0, 3).Select(i => completed.Contains(i) ? variable.Full[i] : variable.Old[i]).ToArray(), count == 3);
                variablePrefixCases++;
            }
        }
        var historical = Template(3, [0, 1, 2], [0, 5, 17]);
        var paths = Paths("historical-exact", 3);
        for (int i = 0; i < 3; i++) File.WriteAllBytes(paths[i], historical.Full[i]);
        var files = Open(paths);
        int historicalFallbacks;
        try {
            for (int i = 0; i < 50; i++) foreach (int id in new[] { 1, 2 }) files[id].Append(Root, Encode(9)).Unwrap();
            preferExact = true;
            int before = fallbackScans;
            Check(Current(files, 0) == 1, "historical exact lookup failed");
            historicalFallbacks = fallbackScans - before;
            Check(historicalFallbacks == 0, "successful historical peer lookup scanned suffix");
        }
        finally { preferExact = false; Close(files); }

        var reuseResults = new List<object>();
        foreach (int delta in new[] { -4, 0, 4 }) foreach (bool overlap in new[] { false, true }) {
            files = CreateFiles(Paths("variable-overlap-reuse", 3));
            try {
                var old = IncompleteAB(files);
                var fresh = AppendFresh(files, overlap ? [1, 2] : [0, 1], 9, 8 + delta);
                Check(old[1].Offset == fresh[1].Offset, "peer offset not reused");
                Check((old[1] == fresh[1]) == (delta == 0), "unexpected reused ticket length");
                AssertCurrentBoth(files, overlap ? [0, 9, 9] : [9, 9, 0]);
                using var oldFrame = files[0].ReadPooledFrame(old[0]).Unwrap();
                preferExact = true;
                Check(!Qualify(files, 0, old[0], Decode(oldFrame.PayloadAndMeta)), "fresh overlap revived old group");
                reuseResults.Add(new { delta, overlap, sameTicket = old[1] == fresh[1], oldGroupRejected = true });
            }
            finally { preferExact = false; Close(files); }
        }

        // An original recoverable B body may be closed lazily after a later ordinary A update.
        var lazy = Template(2, [0, 1]);
        paths = Paths("lazy-completed-tail", 2);
        File.WriteAllBytes(paths[0], lazy.Full[0]);
        File.WriteAllBytes(paths[1], lazy.Full[1][..^8]); // Synthetic cut removes original Key/Fence suffix.
        files = new Dictionary<int, IRbfFile> { [0] = RbfFile.OpenExisting(paths[0], out _, RbfCacheMode.Off) };
        try {
            files[0].Append(Root, Encode(9)).Unwrap(); files[0].DurableFlush();
            Check(Current(files, 0) == 9, "ordinary update required old peer");
            bool readonlyRefused = false;
            try { using var ro = RbfFile.OpenReadOnlyExisting(paths[1], RbfCacheMode.Off); }
            catch (InvalidDataException) { readonlyRefused = true; }
            Check(readonlyRefused, "readonly treated recoverable body as absent");
            files[1] = RbfFile.OpenExisting(paths[1], out var recovery, RbfCacheMode.Off);
            Check(recovery.Action == RbfTailRecoveryAction.CompletedTail, "original body not recovered");
            AssertCurrentBoth(files, 9, 2);
            using var old = files[0].ReadPooledFrame(lazy.Tickets[0]).Unwrap();
            preferExact = true;
            Check(Qualify(files, 0, lazy.Tickets[0], Decode(old.PayloadAndMeta)), "lazy recovery lost original group");
        }
        finally { preferExact = false; Close(files); }

        // A bad actual replacement must survive disambiguation as its own CRC error.
        paths = Paths("bad-short-replacement", 2);
        files = CreateFiles(paths);
        SizedPtr replacement;
        try {
            var old = IncompleteAB(files);
            replacement = files[1].Append(Root, Encode(9, extra: 32)).Unwrap();
            Check(replacement.Offset == old[1].Offset && replacement.Length < old[1].Length, "replacement not shorter");
        }
        finally { Close(files); }
        var damaged = File.ReadAllBytes(paths[1]);
        damaged[checked((int)replacement.Offset + 8)] ^= 1;
        File.WriteAllBytes(paths[1], damaged);
        files = Open(paths);
        try {
            foreach (bool fast in new[] { false, true }) {
                preferExact = fast;
                bool crcRejected = false;
                try { Current(files, 0); }
                catch (FrameReadFailure ex) { Check(ex.ErrorCode == "Rbf.CrcMismatch", "bad actual replacement error changed"); crcRejected = true; }
                Check(crcRejected, "bad actual replacement became Uncommitted");
            }
        }
        finally { preferExact = false; Close(files); }

        files = CreateFiles(Paths("disposed-eof", 1));
        int lifecycleFallbacks;
        try {
            var file = files[0];
            var wanted = SizedPtr.Create(file.TailOffset, RbfFile.MeasureWriteSize(32).Unwrap().FrameLength);
            file.Dispose();
            preferExact = true;
            int before = fallbackScans;
            bool rejected = false;
            try { using var frame = ReadSlot(file, wanted); }
            catch (ObjectDisposedException) { rejected = true; }
            lifecycleFallbacks = fallbackScans - before;
            Check(rejected && lifecycleFallbacks == 0, "disposed EOF became missing or fallback");
        }
        finally { preferExact = false; Close(files); }
        return new { historicalPeerAppends = 100, historicalFallbacks, reuseResults,
            lazyCompletedTailAfterOrdinaryUpdate = true, badActualReplacementCrcPropagated = true,
            disposedEofRejected = true, lifecycleFallbacks };
    }
    static object InvalidReplacementChecks() {
        foreach (bool duplicate in new[] { false, true }) {
            var files = CreateFiles(Paths("bad-actual-codec", 2));
            try {
                var old = IncompleteAB(files);
                int length = 32; // Shorter than the original intended CU.
                var actual = SizedPtr.Create(files[1].TailOffset, RbfFile.MeasureWriteSize(length).Unwrap().FrameLength);
                var table = new Dictionary<int, SizedPtr> { [0] = old[0], [1] = duplicate ? actual : old[1] };
                byte[] bytes = Encode(9, table);
                if (duplicate) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0);
                Check(files[1].Append(Conditional, bytes).Unwrap() == actual, "bad-codec fixture length changed");
                foreach (bool fast in new[] { false, true }) {
                    preferExact = fast;
                    bool rejected = false;
                    try { Current(files, 0); }
                    catch (InvalidOperationException ex) when (ex.Message == (duplicate ? "duplicate member" : "peer self mismatch")) { rejected = true; }
                    Check(rejected, "invalid actual replacement became Uncommitted");
                }
            }
            finally { preferExact = false; Close(files); }
        }
        return new { badReplacementSelfRejected = true, duplicateReplacementMembersRejected = true };
    }
    public static void Main(string[] args) {
        work = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "conditional-update-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(args[0]);
        Check(!Directory.Exists(work), "output must be fresh"); Directory.CreateDirectory(work);
        Prefixes();
        var replacements = new[] { Replacement(0, false), Replacement(4, false), Replacement(0, true), Replacement(4, true) };
        var extraChecks = CorruptionAndLaterUpdate();
        var refinementChecks = RefinementChecks();
        var invalidReplacementChecks = InvalidReplacementChecks();
        var result = new { prefixCases, byteCases, truncated, completedTail, readonlyRejected, replacements, extraChecks, work,
            variablePrefixCases, lookupComparisons, exactSuccesses, fallbackScans, refinementChecks, invalidReplacementChecks,
            scope = "Real RBF3 public API and saved byte-prefix images; synthetic payload; no process kill, power-loss, VersionStore or package qualification." };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(work, "result.json"), json); Console.WriteLine(json);
    }
}
