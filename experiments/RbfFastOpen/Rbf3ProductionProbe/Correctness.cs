using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia;
using Atelia.Data;
using Atelia.Rbf;

internal static class Correctness {
    internal static readonly byte[] Prior = "old"u8.ToArray();
    internal static readonly byte[] Tail = Program.Pattern(294, marker: true);
    internal const int TailMetaLength = 3;
    internal const uint PriorTag = 11, TailTag = 53;
    internal const int TailLength = 324, BodyCompletePrefix = TailLength - 4, TailSpan = TailLength + 4;

    internal sealed record Fixture(string Name, string File, string Profile, uint Key, uint Tag,
        int Length, string PayloadHex, string MetaHex, string Sha256);

    internal static object VerifyGolden(string output) {
        var folder = Path.Combine(output, "fixtures");
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
        int checks = 0;
        foreach (var fixture in fixtures) {
            string path = Path.Combine(folder, fixture.File);
            var original = File.ReadAllBytes(path);
            Program.Require(Convert.ToHexStringLower(SHA256.HashData(original)) == fixture.Sha256, "Independent fixture hash mismatch.");
            byte[] combined = [..Convert.FromHexString(fixture.PayloadHex), ..Convert.FromHexString(fixture.MetaHex)];
            int metaLength = fixture.MetaHex.Length / 2;
            var ticket = SizedPtr.Create(4, fixture.Length);
            foreach (var cache in new[] { RbfCacheMode.Off, RbfCacheMode.Slots16 }) {
                using var file = RbfFile.OpenReadOnlyExisting(path, cache);
                Program.VerifyFrame(file, ticket, combined, metaLength, fixture.Tag);
                int forward = 0, reverse = 0;
                foreach (var info in file.ScanForward()) { Program.Require(info.Ticket == ticket, "Forward scan ticket changed."); forward++; }
                foreach (var info in file.ScanReverse()) { Program.Require(info.Ticket == ticket, "Reverse scan ticket changed."); reverse++; }
                Program.Require(forward == 1 && reverse == 1, "Golden scan count mismatch.");
                var boundary = file.GetScanBoundaryAfter(ticket).Unwrap();
                int suffixCount = 0;
                foreach (var _ in file.ScanForward(boundary).Unwrap()) { suffixCount++; }
                Program.Require(suffixCount == 0, "Golden suffix boundary mismatch.");
                Program.Require(file.GetPhysicalOffsetImmediatelyAfter(ticket) == original.Length, "Byte ticket successor changed.");
                Program.Require(!file.ReadFrameInfoImmediatelyAfter(ticket).Unwrap().HasValue, "Unexpected successor.");
                checks++;
            }
            if (fixture.Profile == "RBF1") {
                bool rejected = false;
                try { using var rejectedFile = RbfFile.OpenExisting(path, out var ignoredReport); }
                catch (InvalidDataException) { rejected = true; }
                Program.Require(rejected && original.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Legacy writable open must reject unchanged.");
            }
            else {
                using var file = RbfFile.OpenExisting(path, out var report);
                Program.Require(report.Action == RbfTailRecoveryAction.None, "Healthy fixed-Key fixture repaired unexpectedly.");
                Program.VerifyFrame(file, ticket, combined, metaLength, fixture.Tag);
            }
        }
        return new { Passed = true, Fixtures = fixtures.Length, CheckedCacheViews = checks,
            Scope = "Independent Python wire -> production caller/pooled read, info/meta, both scans, boundary and legacy read-only ticket; fixed full-uint keys." };
    }

    internal static (byte[] Bytes, long Start, SizedPtr Ticket) MakeCanonical(string path, bool builder) {
        SizedPtr ticket;
        long start;
        using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            file.Append(PriorTag, Prior).Unwrap();
            start = file.TailOffset;
            ticket = Program.WriteFrame(file, Tail, TailMetaLength, TailTag, builder);
            file.DurableFlush();
        }
        Program.Require(ticket.Length == TailLength && start == 40, "Unexpected canonical units geometry.");
        return (File.ReadAllBytes(path), start, ticket);
    }

    internal static object VerifyProductionCuts(string output) {
        var rows = new List<object>();
        var vectors = new List<object>();
        for (int headerPrefix = 0; headerPrefix < 4; headerPrefix++) {
            string path = Path.Combine(output, "io", $"header-cut-{headerPrefix}.rbf");
            byte[] bytes = "RBF3"u8[..headerPrefix].ToArray();
            File.WriteAllBytes(path, bytes);
            foreach (bool writable in new[] { false, true }) {
                bool rejected = false;
                try { using var file = writable ? RbfFile.OpenExisting(path, out _) : RbfFile.OpenReadOnlyExisting(path); }
                catch (InvalidDataException) { rejected = true; }
                Program.Require(rejected && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Incomplete Header must reject unchanged.");
            }
        }
        foreach (bool builder in new[] { false, true }) {
            string label = builder ? "builder" : "append";
            var canonical = MakeCanonical(Path.Combine(output, "io", $"canonical-{label}.rbf"), builder);
            // Small writer outputs receive a full independent bitwise CRC/wire audit after this process exits.
            string singlePath = Path.Combine(output, "io", $"vector-{label}.rbf");
            using (var single = RbfFile.CreateNew(singlePath)) { Program.WriteFrame(single, Tail, TailMetaLength, TailTag, builder); }
            vectors.Add(new { Method = label, PayloadHex = Convert.ToHexStringLower(Tail.AsSpan(0, Tail.Length - TailMetaLength)),
                MetaHex = Convert.ToHexStringLower(Tail.AsSpan(Tail.Length - TailMetaLength)), Tag = TailTag,
                WireHex = Convert.ToHexStringLower(File.ReadAllBytes(singlePath)) });
            int none = 0, truncated = 0, completed = 0;
            for (int prefix = 0; prefix <= TailSpan; prefix++) {
                string path = Path.Combine(output, "io", $"cut-{label}-{prefix:D3}.rbf");
                File.WriteAllBytes(path, canonical.Bytes.AsSpan(0, (int)canonical.Start + prefix).ToArray());
                var report = VerifyRecover(path, canonical.Start, prefix, verifyTail: prefix >= BodyCompletePrefix);
                switch (report.Action) {
                    case RbfTailRecoveryAction.None: none++; break;
                    case RbfTailRecoveryAction.Truncated: truncated++; break;
                    case RbfTailRecoveryAction.CompletedTail: completed++; break;
                    default: throw new InvalidOperationException("Unexpected action in RBF3 prefix experiment.");
                }
            }
            string corrupted = Path.Combine(output, "io", $"content-corrupt-{label}.rbf");
            var bad = canonical.Bytes.ToArray();
            bad[(int)canonical.Ticket.Offset + 4] ^= 1;
            File.WriteAllBytes(corrupted, bad);
            using (var file = RbfFile.OpenExisting(corrupted, out var report)) {
                Program.Require(report.Action == RbfTailRecoveryAction.None, "Payload corruption must not change structurally complete Open.");
                Program.Require(file.ReadFrame(canonical.Ticket, new byte[canonical.Ticket.Length]).IsFailure, "Checked caller read missed payload CRC corruption.");
                Program.Require(file.ReadPooledFrame(canonical.Ticket).IsFailure, "Checked pooled read missed payload CRC corruption.");
            }
            Program.Require(bad.AsSpan().SequenceEqual(File.ReadAllBytes(corrupted)), "Content-bad healthy structure was modified.");
            rows.Add(new { Writer = label, Prefixes = TailSpan + 1, None = none, Truncated = truncated,
                CompletedTail = completed, BodyCompletePrefix, FrameBytes = TailLength, KeyFencePrefixes = 9,
                ReadOnlyUnchanged = true, RecoveryAgainNone = true, ReadCrcOrthogonal = true });
        }
        Program.WriteJson(Path.Combine(output, "production-vectors.json"), vectors);
        return new { Passed = true, HeaderPrefixesRejectedUnchanged = 4, Rows = rows, Scope = "All terminal byte-prefix cuts of actual production Append and chunked Builder wires; sole incomplete-tail policy." };
    }

    internal static RbfTailRecoveryReport VerifyRecover(string path, long start, int prefix, bool verifyTail) {
        byte[] before = File.ReadAllBytes(path);
        bool readonlyRejected = false;
        try { using var _ = RbfFile.OpenReadOnlyExisting(path); }
        catch (InvalidDataException) { readonlyRejected = true; }
        Program.Require(readonlyRejected == (prefix != 0 && prefix != TailSpan), "Read-only qualification differs from expected prefix closure.");
        Program.Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Read-only recovery changed bytes.");
        RbfTailRecoveryReport report;
        using (var file = RbfFile.OpenExisting(path, out report, RbfCacheMode.Off)) {
            var expectedAction = prefix == 0 || prefix == TailSpan ? RbfTailRecoveryAction.None :
                verifyTail ? RbfTailRecoveryAction.CompletedTail : RbfTailRecoveryAction.Truncated;
            Program.Require(report.Action == expectedAction, $"Prefix {prefix}: expected {expectedAction}, got {report.Action}.");
            Program.Require(report.OriginalLength == start + prefix && report.FinalLength == (verifyTail ? start + TailSpan : start), "Recovery physical report mismatch.");
            Program.VerifyFrame(file, SizedPtr.Create(4, 32), Prior, 0, PriorTag);
            if (verifyTail) { Program.VerifyFrame(file, SizedPtr.Create(start, TailLength), Tail, TailMetaLength, TailTag); }
        }
        var closed = File.ReadAllBytes(path);
        int preservedBytes = Math.Min(before.Length, closed.Length);
        Program.Require(before.AsSpan(0, preservedBytes).SequenceEqual(closed.AsSpan(0, preservedBytes)),
            "First recovery changed an existing retained byte prefix.");
        using (var again = RbfFile.OpenExisting(path, out var second)) {
            Program.Require(second.Action == RbfTailRecoveryAction.None && second.OriginalLength == closed.Length, "Recovery is not idempotent.");
        }
        Program.Require(closed.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Second recovery changed closed bytes.");
        return report;
    }
}
