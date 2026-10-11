using System.Buffers.Binary;
using System.Text.Json;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using DataStore = Atelia.FrameStore.FrameStore;
using Versions = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Smoke;

/// <summary>Fixed 09/TagNameTests vectors; this consumer does not implement the routing algorithm.</summary>
internal static class TagGoldenScenario {
    private static readonly Golden[] Goldens = [
        new("A", "A", 0x351, false),
        new("Release", "Release", 0x174, true),
        new("release", "release", 0x1aa, false),
        // Construct the lone surrogate at runtime; JSON and attribute encoders may replace it.
        new("U+D800", new string((char)0xd800, 1), 0x265, false),
        new("tag-48", "tag-48", 0x106, false),
        new("tag-65", "tag-65", 0x106, true)
    ];

    internal static int Run(string[] args) {
        if (args.Length != 2 || args[0] is not ("tag-golden-write" or "tag-golden-read")) {
            throw new ArgumentException("Usage: tag-golden-write <fresh-root> | tag-golden-read <root>");
        }
        string root = Path.GetFullPath(args[1]);
        if (args[0] == "tag-golden-write") { Write(root); }
        else { Read(root); }
        return 0;
    }

    private static void Write(string root) {
        Require(!File.Exists(root) && (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()),
            "tag-golden-write requires an absent or empty root.");
        Directory.CreateDirectory(root);
        Manifest manifest;
        using (var data = DataStore.Create(Path.Combine(root, "data"))) {
            using var versions = Versions.Create(Path.Combine(root, "versions"), data, reservedRefIdUpperBound: 1);
            RefRevision source = versions.CreateRef(1, [0x81, 0xff, 0x00], out var outcome).Unwrap();
            Confirmed(outcome);
            var entries = new List<Entry>();
            for (int index = 0; index < Goldens.Length; index++) {
                Golden golden = Goldens[index];
                byte[] payload = Payload(index);
                bool created = golden.Related
                    ? versions.CreateTag(golden.Name, payload, source, out outcome).Unwrap()
                    : versions.CreateTag(golden.Name, payload, out outcome).Unwrap();
                Require(created, "CreateTag did not return true.");
                Confirmed(outcome);
                TagInfo info = versions.ReadTagInfo(golden.Name).Unwrap();
                Require(StringComparer.Ordinal.Equals(golden.Name, info.Name), "Writer changed the original name code units.");
                entries.Add(new Entry(golden.Label, CodeUnits(info.Name), info.RecordedAtUnixMs,
                    info.RelatedRefId?.LocalId, info.RelatedTicket?.Packed));
            }
            // Related notes retain the selected coordinates, even when the source advances.
            _ = versions.PublishRef(source.RefId, [0x42, 0x11], out outcome).Unwrap();
            Confirmed(outcome);
            manifest = new Manifest(Convert.ToBase64String(data.StoreId), Convert.ToBase64String(versions.VersionStoreId),
                source.Ticket.Packed, entries.ToArray());
            ValidatePublic(versions, manifest);
        }
        File.WriteAllText(Path.Combine(root, "tag-golden-fixture.json"), JsonSerializer.Serialize(manifest));
        string[] paths = ValidatePaths(root);
        Emit("tag-golden-write", paths, manifest);
    }

    private static void Read(string root) {
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "tag-golden-fixture.json")))
            ?? throw new InvalidDataException("Missing tag golden fixture evidence.");
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data"))) {
            using var versions = Versions.OpenReadOnly(Path.Combine(root, "versions"), data);
            Require(data.IsReadOnly && versions.IsReadOnly, "The golden consumer must use read-only owners.");
            Require(Convert.ToBase64String(data.StoreId) == manifest.DataId
                && Convert.ToBase64String(versions.VersionStoreId) == manifest.VersionStoreId, "Cold identity changed.");
            ValidatePublic(versions, manifest);
            VersionStoreAudit audit = versions.Audit().Unwrap();
            Require(audit.RefCount == 1 && audit.SnapshotCount == 2 && audit.TagCount == 6
                && audit.TagBucketCount == 5 && audit.ForkCount == 0, "Unexpected golden inventory.");
        }
        string[] paths = ValidatePaths(root);
        ValidatePhysicalBuckets(root, manifest);
        Emit("tag-golden-read", paths, manifest);
    }

    private static void ValidatePublic(Versions versions, Manifest manifest) {
        Require(manifest.Entries.Length == Goldens.Length, "The fixture must contain all fixed vectors.");
        Require(versions.ListRefs().Unwrap().Select(id => id.LocalId).SequenceEqual(new uint[] { 1 }),
            "Creating tags changed the formal ref set.");
        RefBytesSnapshot currentSource = versions.ReadRef(1).Unwrap();
        Require(currentSource.Revision.Ticket.Packed != manifest.SourceTicket
            && currentSource.Payload.SequenceEqual(new byte[] { 0x42, 0x11 }), "The related source did not retain its later head.");
        for (int index = 0; index < Goldens.Length; index++) {
            Golden golden = Goldens[index];
            TagInfo info = versions.ReadTagInfo(golden.Name).Unwrap();
            ValidateInfo(info, golden, manifest.Entries[index], manifest.SourceTicket);
            Require(versions.ResolveTag(golden.Name).Unwrap().Bytes.SequenceEqual(Payload(index)), "The exact named root payload changed.");
        }
        // Both case-distinct names and both colliding full names must remain separate.
        Require(!versions.ResolveTag("Release").Unwrap().Bytes.SequenceEqual(versions.ResolveTag("release").Unwrap().Bytes)
            && !versions.ResolveTag("tag-48").Unwrap().Bytes.SequenceEqual(versions.ResolveTag("tag-65").Unwrap().Bytes),
            "Distinct Ordinal names were conflated.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        TagScanEnd end = versions.ScanTags(info => {
            int index = Array.FindIndex(Goldens, golden => StringComparer.Ordinal.Equals(golden.Name, info.Name));
            Require(index >= 0 && seen.Add(info.Name), "Scan returned an unknown or repeated golden name.");
            ValidateInfo(info, Goldens[index], manifest.Entries[index], manifest.SourceTicket);
            Require(versions.ResolveTag(info.Name).Unwrap().Bytes.SequenceEqual(Payload(index)), "Scan/name routing returned a different root.");
            return true;
        }).Unwrap();
        Require(end == TagScanEnd.Complete && seen.Count == Goldens.Length, "Scan did not return the complete Ordinal name set.");

        // These independent BPV vectors decode to A; routing is observed through the public query.
        foreach (byte[] wire in new[] { new byte[] { 3, 65 }, new byte[] { 2, 65, 0 }, new byte[] { 0x83, 0, 65 } }) {
            var reader = new BareValueReader(wire);
            string name = reader.ReadString(3072);
            reader.EnsureFullyConsumed();
            Require(StringComparer.Ordinal.Equals(name, "A") && versions.ResolveTag(name).Unwrap().Bytes.SequenceEqual(Payload(0)),
                "An independently decoded A representation changed name routing.");
        }
    }

    private static void ValidateInfo(TagInfo info, Golden golden, Entry entry, ulong sourceTicket) {
        Require(entry.Label == golden.Label && entry.NameCodeUnits == CodeUnits(golden.Name)
            && StringComparer.Ordinal.Equals(info.Name, golden.Name) && info.RecordedAtUnixMs == entry.RecordedAtUnixMs,
            "The original name or recorded metadata changed across the process boundary.");
        uint? expectedRef = golden.Related ? 1u : null;
        ulong? expectedTicket = golden.Related ? sourceTicket : null;
        Require(info.RelatedRefId?.LocalId == expectedRef && entry.RelatedRefId == expectedRef
            && info.RelatedTicket?.Packed == expectedTicket && entry.RelatedTicket == expectedTicket,
            "The complete Related metadata differs from the selected source coordinates.");
    }

    private static string[] ValidatePaths(string root) {
        string tags = Path.Combine(root, "versions", "tags");
        string[] actual = Directory.EnumerateFileSystemEntries(tags).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray();
        string[] expected = Goldens.Select(golden => golden.FileName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(expected, StringComparer.Ordinal), "Observed tag paths differ from the independent 09 bucket goldens.");
        return actual;
    }

    private static void ValidatePhysicalBuckets(string root, Manifest manifest) {
        foreach (uint bucket in Goldens.Select(golden => golden.Bucket).Distinct()) {
            using var file = RbfFile.OpenReadOnlyExisting(Path.Combine(root, "versions", "tags", $"{bucket:x8}.rbf"));
            Require(file.Format == RbfFormat.Rbf3, "The golden bucket is not RBF3.");
            var scan = file.ScanForward(showTombstone: true).GetEnumerator();
            Require(scan.MoveNext(), "Missing golden bucket header.");
            using (var header = file.ReadPooledFrame(scan.Current.Ticket).Unwrap()) {
                ReadOnlySpan<byte> bytes = header.PayloadAndMeta;
                Require(header.Tag == 0 && !header.IsTombstone && header.TailMetaLength == 0 && bytes.Length == 24
                    && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 1
                    && bytes.Slice(4, 16).SequenceEqual(Convert.FromBase64String(manifest.VersionStoreId))
                    && BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) == bucket, "Bucket header identity/format/id differs from its fixed golden path.");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (scan.MoveNext()) {
                using var frame = file.ReadPooledFrame(scan.Current.Ticket).Unwrap();
                Require(frame.Tag == 1 && !frame.IsTombstone && frame.TailMetaLength >= 15, "Unexpected physical golden record shape.");
                ReadOnlySpan<byte> meta = frame.PayloadAndMeta[^frame.TailMetaLength..];
                Require(RollingCrc.CheckCodewordForward(meta), "Physical golden metadata CRC failed.");
                var reader = new BareValueReader(meta[..^4]);
                long time = reader.ReadInt64LE();
                uint related = reader.ReadVarUInt32();
                ulong? ticket = related == 0 ? null : SizedPtr.Deserialize(reader.ReadVarUInt64()).Packed;
                string name = reader.ReadString(3072);
                reader.EnsureFullyConsumed();
                int index = Array.FindIndex(Goldens, golden => StringComparer.Ordinal.Equals(golden.Name, name));
                Require(index >= 0 && Goldens[index].Bucket == bucket && names.Add(name), "Physical name was rewritten, duplicated or routed to the wrong golden bucket.");
                Entry entry = manifest.Entries[index];
                Require(time == entry.RecordedAtUnixMs && related == (entry.RelatedRefId ?? 0) && ticket == entry.RelatedTicket
                    && frame.PayloadAndMeta[..^frame.TailMetaLength].SequenceEqual(Payload(index)), "Physical root or metadata differs from the public golden record.");
            }
            Require(scan.TerminationError is null && names.Count == Goldens.Count(golden => golden.Bucket == bucket),
                "Golden bucket did not reach normal EOF with all expected records.");
        }
    }

    private static byte[] Payload(int index) => Enumerable.Range(0, 67 + index).Select(value => unchecked((byte)(value * 37 + index * 19))).ToArray();
    private static string CodeUnits(string name) => string.Concat(name.Select(value => ((ushort)value).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
    private static void Emit(string command, string[] paths, Manifest manifest) {
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "ok", command, pid = Environment.ProcessId, tagCount = 6, bucketCount = 5, bucketPaths = paths,
            originalCodeUnits = manifest.Entries.Select(entry => entry.NameCodeUnits).ToArray(),
            payloadHex = Enumerable.Range(0, Goldens.Length).Select(index => Convert.ToHexString(Payload(index))).ToArray(),
            metadata = manifest.Entries, ordinalNames = true, collisionNamesDistinct = true,
            sourceTicket = manifest.SourceTicket, scanComplete = true,
            physicalFormatChecked = command == "tag-golden-read"
        }));
        Console.Out.Flush();
    }
    private static void Confirmed(PublicationOutcome outcome) => Require(outcome == PublicationOutcome.Confirmed, "Public publication did not confirm.");
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
    private sealed record Golden(string Label, string Name, uint Bucket, bool Related) {
        internal string FileName => $"{Bucket:x8}.rbf";
    }
    private sealed record Entry(string Label, string NameCodeUnits, long RecordedAtUnixMs, uint? RelatedRefId, ulong? RelatedTicket);
    private sealed record Manifest(string DataId, string VersionStoreId, ulong SourceTicket, Entry[] Entries);
}
