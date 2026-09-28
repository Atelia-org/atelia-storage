using System.Buffers.Binary;
using System.Text;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

internal sealed record CatalogSnapshot(RbfScanBoundary Boundary,
    Dictionary<string, RefId> Branches, Dictionary<string, EventAddress> Tags) {
    internal long LiveCount => checked((long)Branches.Count + Tags.Count);
}

/// <summary>Single streaming codec used by initialization, checkpoints and offline indexes.</summary>
internal static class CatalogSnapshotCodec {
    internal const string FileName = "catalog.snapshot";

    internal static void Write(Stream stream, CatalogSnapshot snapshot) {
        var branches = snapshot.Branches.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var tags = snapshot.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        long length = 68;
        var ids = new HashSet<RefId>();
        foreach (var p in branches) {
            ValidateName(p.Key);
            if (!IsValidRefId(p.Value) || !ids.Add(p.Value)) { throw new ArgumentException("Invalid active RefId."); }
            length = checked(length + 10 + p.Key.Length);
        }
        foreach (var p in tags) {
            ValidateName(p.Key);
            ValidateAddress(p.Value);
            length = checked(length + 18 + p.Key.Length);
        }
        ValidateBoundary(snapshot.Boundary);
        if (snapshot.Boundary == RbfScanBoundary.Empty && snapshot.LiveCount != 0) { throw new ArgumentException("Empty prefix cannot have live entries."); }
        Span<byte> header = stackalloc byte[64];
        header.Clear();
        "EJCS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 64);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], (ulong)snapshot.Boundary.EndExclusive);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], snapshot.Boundary.AnchorTicket.Packed);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], snapshot.Boundary.AnchorContentCrc32C);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)branches.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)tags.Length);
        header[44] = snapshot.Boundary.AnchorTicket.Packed == 0 ? (byte)0 : (byte)1;
        uint crc = RollingCrc.DefaultInitValue;
        WritePart(stream, header, ref crc);
        Span<byte> entry = stackalloc byte[146];
        foreach (var p in branches) {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)p.Key.Length);
            Encoding.ASCII.GetBytes(p.Key, entry[2..]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[(2 + p.Key.Length)..], p.Value.Packed);
            WritePart(stream, entry[..(10 + p.Key.Length)], ref crc);
        }
        foreach (var p in tags) {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)p.Key.Length);
            Encoding.ASCII.GetBytes(p.Key, entry[2..]);
            EventAddressCodec.Encode(p.Value, entry[(2 + p.Key.Length)..]);
            WritePart(stream, entry[..(18 + p.Key.Length)], ref crc);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(entry, crc ^ RollingCrc.DefaultFinalXor);
        stream.Write(entry[..4]);
    }

    internal static CatalogSnapshot Read(Stream stream, string path) {
        StorageOpenException Bad(string code = "MetadataCorrupt") => new(StorageOpenErrorKind.MaintenanceRequired, code, path);
        try {
            long physicalLength = stream.Length - stream.Position;
            if (physicalLength < 68) { throw Bad(); }
            uint crc = RollingCrc.DefaultInitValue;
            Span<byte> header = stackalloc byte[64];
            ReadPart(stream, header, ref crc);
            if (!header[..4].SequenceEqual("EJCS"u8)) { throw Bad(); }
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            if (version != 1) { throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "UnsupportedVersion", path, observedVersion: version); }
            if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 64 || BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) != (ulong)physicalLength) { throw Bad(); }
            if (header[44] > 1 || header[45..].ContainsAnyExcept((byte)0)) {
                throw new StorageOpenException(StorageOpenErrorKind.FormatUnsupported, "UnsupportedFlags", path);
            }
            uint branchCount = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
            uint tagCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
            if (header[44] == 0 && (branchCount != 0 || tagCount != 0)) { throw Bad("CatalogInvalid"); }
            if (branchCount > int.MaxValue || tagCount > int.MaxValue) { throw Bad(); }
            long minLength = checked(68L + 11L * branchCount + 19L * tagCount);
            long maxLength = checked(68L + 138L * branchCount + 146L * tagCount);
            if (physicalLength < minLength || physicalLength > maxLength) { throw Bad(); }
            ulong end = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            if (end > long.MaxValue) { throw Bad(); }
            var boundary = new RbfScanBoundary((long)end,
                SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(header[24..])),
                BinaryPrimitives.ReadUInt32LittleEndian(header[32..]));
            try { ValidateBoundary(boundary); }
            catch (ArgumentException) { throw Bad(); }
            if ((header[44] == 0) != (boundary.AnchorTicket.Packed == 0)) { throw Bad(); }
            var branches = new Dictionary<string, RefId>(StringComparer.Ordinal);
            var tags = new Dictionary<string, EventAddress>(StringComparer.Ordinal);
            var ids = new HashSet<RefId>();
            Span<byte> entry = stackalloc byte[146];
            string? previous = null;
            for (uint i = 0; i < branchCount; i++) {
                string name = ReadName(stream, entry, ref crc, ref previous, path);
                ReadPart(stream, entry[..8], ref crc);
                var id = new RefId(BinaryPrimitives.ReadUInt64LittleEndian(entry));
                if (!IsValidRefId(id) || !ids.Add(id)) { throw Bad("CatalogInvalid"); }
                branches.Add(name, id);
            }
            previous = null;
            for (uint i = 0; i < tagCount; i++) {
                string name = ReadName(stream, entry, ref crc, ref previous, path);
                ReadPart(stream, entry[..16], ref crc);
                var result = EventAddressCodec.Decode(entry[..16]);
                if (result.IsFailure) { throw Bad("CatalogInvalid"); }
                EventAddress address = result.Unwrap();
                try { ValidateAddress(address); }
                catch (ArgumentException) { throw Bad("CatalogInvalid"); }
                tags.Add(name, address);
            }
            uint expected = crc ^ RollingCrc.DefaultFinalXor;
            stream.ReadExactly(entry[..4]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != expected || stream.Position != stream.Length) { throw Bad(); }
            return new CatalogSnapshot(boundary, branches, tags);
        }
        catch (EndOfStreamException) { throw Bad(); }
        catch (OverflowException) { throw Bad(); }
    }

    private static string ReadName(Stream stream, Span<byte> buffer, ref uint crc, ref string? previous, string path) {
        ReadPart(stream, buffer[..2], ref crc);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        if (count is < 1 or > 128 || stream.Length - stream.Position < count + 4) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "CatalogInvalid", path); }
        ReadPart(stream, buffer[..count], ref crc);
        if (buffer[..count].ContainsAnyInRange((byte)128, byte.MaxValue)) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "CatalogInvalid", path); }
        string name = Encoding.ASCII.GetString(buffer[..count]);
        if (EventJournal.ValidateTagName(name) is not null || previous is not null && StringComparer.Ordinal.Compare(previous, name) >= 0) {
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "CatalogInvalid", path);
        }
        previous = name;
        return name;
    }

    private static void ValidateName(string name) {
        if (EventJournal.ValidateTagName(name) is not null) { throw new ArgumentException("Invalid catalog name."); }
    }
    private static bool IsValidRefId(RefId id) {
        var ticket = SizedPtr.FromPacked(id.Packed);
        return ticket.Offset >= 4 && ticket.Length is >= 24 and <= 248;
    }
    private static void ValidateAddress(EventAddress address) {
        if (address.SegmentNumber == 0 || address.Ticket.Offset < 4 || address.Ticket.Length < 24) { throw new ArgumentException("Invalid catalog address."); }
    }
    private static void ValidateBoundary(RbfScanBoundary boundary) {
        if (boundary.AnchorTicket.Packed == 0) {
            if (boundary != RbfScanBoundary.Empty) { throw new ArgumentException("Invalid empty boundary."); }
        }
        else if (boundary.EndExclusive < 4 || (boundary.EndExclusive & 3) != 0 || boundary.AnchorTicket.Offset < 4 || boundary.AnchorTicket.Length is < 24 or > 248) {
            throw new ArgumentException("Invalid catalog boundary.");
        }
    }
    private static void ReadPart(Stream stream, Span<byte> bytes, ref uint crc) {
        stream.ReadExactly(bytes);
        crc = RollingCrc.CrcForward(crc, bytes);
    }
    private static void WritePart(Stream stream, ReadOnlySpan<byte> bytes, ref uint crc) {
        stream.Write(bytes);
        crc = RollingCrc.CrcForward(crc, bytes);
    }
}
