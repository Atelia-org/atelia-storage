using System.Buffers.Binary;
using Atelia.Data.Hashing;

namespace Atelia.RbfSegmentStore;

internal static class SegmentLocator {
    internal const string FileName = "active.segment";

    internal static byte[] Encode(RbfSegmentStoreLayout layout, uint active) {
        if (active == 0 || !Enum.IsDefined(layout)) { throw new ArgumentOutOfRangeException(nameof(active)); }
        byte[] bytes = new byte[20];
        "RBSA"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 20);
        bytes[8] = layout == RbfSegmentStoreLayout.Bucketed ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), active);
        RollingCrc.SealCodewordForward(bytes);
        return bytes;
    }

    internal static (RbfSegmentStoreLayout Layout, uint Active) Read(string storePath) {
        string path = Path.Combine(storePath, FileName);
        byte[] bytes;
        try {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != 20) { throw Error("MetadataCorrupt"); }
            bytes = new byte[20];
            file.ReadExactly(bytes);
        }
        catch (FileNotFoundException e) {
            throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "LegacyOrIncompleteLayout", path, innerException: e);
        }
        if (!bytes.AsSpan(0, 4).SequenceEqual("RBSA"u8)) { throw Error("MetadataCorrupt"); }
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (version != 1) { throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "UnsupportedVersion", path, observedVersion: version); }
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != 20 || !RollingCrc.CheckCodewordForward(bytes)) { throw Error("MetadataCorrupt"); }
        if (bytes[8] > 1 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 0) {
            throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "UnsupportedFlags", path);
        }
        uint active = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        if (active == 0) { throw Error("MetadataCorrupt"); }
        return (bytes[8] == 0 ? RbfSegmentStoreLayout.Bucketed : RbfSegmentStoreLayout.Flat, active);
        StorageOpenException Error(string code) => new(StorageOpenErrorKind.MaintenanceRequired, code, path);
    }

    internal static void Publish(string storePath, RbfSegmentStoreLayout layout, uint active, Action<string>? probe = null) {
        byte[] bytes = Encode(layout, active);
        string target = Path.Combine(storePath, FileName);
        string temporary = Path.Combine(storePath, ".active.segment." + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            probe?.Invoke("LocatorCreate");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                file.Write(bytes);
                probe?.Invoke("LocatorFlush");
                file.Flush(flushToDisk: true);
            }
            probe?.Invoke("LocatorReplace");
            File.Move(temporary, target, overwrite: true);
            probe?.Invoke("LocatorPublished");
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
