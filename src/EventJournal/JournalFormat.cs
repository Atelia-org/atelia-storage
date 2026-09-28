using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

internal static class JournalFormat {
    internal const string FileName = "journal.format";
    internal static byte[] Encode() {
        byte[] bytes = new byte[16];
        "EJFM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 16);
        RollingCrc.SealCodewordForward(bytes);
        return bytes;
    }
    internal static void ValidateMarker(string root) {
        if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }
        string path = Path.Combine(root, FileName);
        Span<byte> bytes = stackalloc byte[16];
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != 16) { throw Bad("MetadataCorrupt", path); }
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException ex) {
            throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "LegacyOrIncompleteLayout", path, innerException: ex);
        }
        if (!bytes[..4].SequenceEqual("EJFM"u8)) { throw Bad("MetadataCorrupt", path); }
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (version != 2) { throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "UnsupportedVersion", path, observedVersion: version); }
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 16 || !RollingCrc.CheckCodewordForward(bytes)) { throw Bad("MetadataCorrupt", path); }
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 0) { throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "UnsupportedFlags", path); }
    }
    internal static CatalogSnapshot Validate(string root) {
        ValidateMarker(root);
        RequireFile(Path.Combine(root, "events", "active.segment"));
        string snapshot = Path.Combine(root, "refs", CatalogSnapshotCodec.FileName);
        RequireFile(snapshot);
        using var file = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
        return CatalogSnapshotCodec.Read(file, snapshot);
    }
    internal static void PublishInitial(string root, IRbfFile refOpLog) {
        refOpLog.DurableFlush();
        string snapshot = Path.Combine(root, "refs", CatalogSnapshotCodec.FileName);
        Publish(snapshot, stream => CatalogSnapshotCodec.Write(stream, new CatalogSnapshot(RbfScanBoundary.Empty,
            new Dictionary<string, RefId>(StringComparer.Ordinal), new Dictionary<string, EventAddress>(StringComparer.Ordinal))));
        Publish(Path.Combine(root, FileName), stream => stream.Write(Encode()));
    }
    internal static void Publish(string target, Action<Stream> write, Action<string>? probe = null) {
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                probe?.Invoke("CheckpointBeforeWrite");
                write(stream);
                probe?.Invoke("CheckpointBeforeTempFlush");
                stream.Flush(flushToDisk: true);
            }
            probe?.Invoke("CheckpointBeforeReplace");
            File.Move(temporary, target, overwrite: true);
            probe?.Invoke("CheckpointAfterReplace");
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
    private static void RequireFile(string path) {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (FileNotFoundException ex) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "MetadataMissing", path, innerException: ex); }
        catch (DirectoryNotFoundException ex) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "MetadataMissing", path, innerException: ex); }
    }
    private static StorageOpenException Bad(string code, string path) => new(StorageOpenErrorKind.MaintenanceRequired, code, path);
}
