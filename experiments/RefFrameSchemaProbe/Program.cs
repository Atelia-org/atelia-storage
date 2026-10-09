using System.Buffers.Binary;
using System.Text.Json;
using Atelia.Data;
using Atelia.Rbf;

// Experimental header codec and shape oracle. No production RefId, RootMap or VersionStore.
const int RootLimit = 1 << 20;
const ulong Child = 0x0123456789ABCDEF, Source = 0xFEDCBA9876543210;
byte[] vsId = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
byte[] rootHeader = Convert.FromHexString("010000000102030405060708090A0B0C0D0E0F10EFCDAB8967452301");
byte[] forkHeader = [.. rootHeader, .. Convert.FromHexString("1032547698BADCFE0800004000000000")];
byte[] oneRoot = Convert.FromHexString("010341EFCDAB89F0DEBC9A78563412"); // Existing independent synthetic-address golden.
var sourceTicket = SizedPtr.Create(64, 32);
int checks = 0;
Require(EncodeHeader(null).AsSpan().SequenceEqual(rootHeader), "Independent root header golden");
Require(EncodeHeader(sourceTicket).AsSpan().SequenceEqual(forkHeader), "Independent fork header golden");
Require(ReadHeader(rootHeader) is null && ReadHeader(forkHeader) == sourceTicket, "Length discriminator");
for (int size = 0; size <= 45; size++) {
    if (size is 28 or 44) { continue; }
    ExpectInvalid(() => ReadHeader(new byte[size]), $"Illegal header length {size}");
}
ExpectInvalid(() => ReadHeader(Mutate(rootHeader, b => b[0] = 2)), "Unknown version");
ExpectInvalid(() => ReadHeader(Mutate(rootHeader, b => b[4] ^= 1)), "Wrong VS identity");
ExpectInvalid(() => ReadHeader(Mutate(rootHeader, b => b[20] ^= 1)), "Wrong child identity");
ExpectInvalid(() => ReadHeader(Mutate(forkHeader, b => b.AsSpan(28, 8).Clear())), "Zero source");
ExpectInvalid(() => ReadHeader(Mutate(forkHeader, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(28), Child))), "Self source");
ExpectInvalid(() => ReadHeader(Mutate(forkHeader, b => b.AsSpan(36, 8).Clear())), "Default source ticket");
ExpectInvalid(() => ReadHeader(Mutate(forkHeader, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(36), SizedPtr.Create(4, 32).Packed))), "Header coordinate source");
ExpectInvalid(() => ReadHeader(Mutate(forkHeader, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(36), SizedPtr.Create(64, 28).Packed))), "Source smaller than snapshot");
ExpectInvalid(() => ReadHeader(Mutate(forkHeader, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(36), SizedPtr.Create(64, MaxSnapshotFrame() + 4).Packed))), "Source over host capacity");
Require(ReadHeader(EncodeHeader(SizedPtr.Create(SizedPtr.MaxOffset, 32)))?.Offset == SizedPtr.MaxOffset, "Ticket end may cross MaxOffset");
Require(RbfFile.MeasureWriteSize(28).Unwrap().FrameLength == 56 && RbfFile.MeasureWriteSize(44).Unwrap().FrameLength == 72, "Header physical sizes");
Require(MaxSnapshotFrame() == RootLimit + 28, "Snapshot host upper bound");

string workRoot = Path.GetFullPath(args.Length == 1 ? args[0] : "W:/");
if (!Directory.Exists(workRoot)) { throw new DirectoryNotFoundException(workRoot); }
string workDirectory = Path.Combine(workRoot, "ref-frame-schema-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDirectory);
var ownedPaths = new List<string>();
RbfTailRecoveryReport initialRepair = default, updateRepair = default;
long naiveMinimum = 0, incompleteInitialLength = 0;
try {
    foreach (byte[] header in new[] { rootHeader, forkHeader }) {
        string path = NewPath();
        using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            var h = file.Append(0, header).Unwrap();
            var initial = file.Append(1, new byte[] { 0 }).Unwrap();
            file.DurableFlush();
            Require(h.Offset == RbfScanBoundary.Empty.EndExclusive && initial.Offset == file.GetPhysicalOffsetImmediatelyAfter(h), "Consecutive initial frames");
        }
        using var read = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
        var pair = ReadFirstTwo(read);
        Require(pair.InitialPayload.SequenceEqual(new byte[] { 0 }), "Cold read exact empty body");
        Require(read.TailOffset == (header.Length == 28 ? 100 : 116), "Derived empty initialization length");
    }

    string headerOnly = NewPath();
    using (var file = RbfFile.CreateNew(headerOnly, RbfCacheMode.Off)) { file.Append(0, rootHeader).Unwrap(); file.DurableFlush(); }
    using (var file = RbfFile.OpenReadOnlyExisting(headerOnly, RbfCacheMode.Off)) { ExpectInvalid(() => ReadFirstTwo(file), "Missing second snapshot"); }
    string unknownKind = NewPath();
    using (var file = RbfFile.CreateNew(unknownKind, RbfCacheMode.Off)) { file.Append(0, rootHeader).Unwrap(); file.Append(2, new byte[] { 0 }).Unwrap(); file.DurableFlush(); }
    using (var file = RbfFile.OpenReadOnlyExisting(unknownKind, RbfCacheMode.Off)) { ExpectInvalid(() => ReadFirstTwo(file), "Unknown ordinary kind"); }

    string incompleteInitial = NewPath();
    using (var file = RbfFile.CreateNew(incompleteInitial, RbfCacheMode.Off)) { file.Append(0, rootHeader).Unwrap(); file.Append(1, oneRoot).Unwrap(); file.DurableFlush(); }
    byte[] complete = File.ReadAllBytes(incompleteInitial);
    File.WriteAllBytes(incompleteInitial, complete[..^8]); // Remove TailKey/Fence; ordinary RBF body is complete.
    incompleteInitialLength = new FileInfo(incompleteInitial).Length;
    naiveMinimum = RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(28).Unwrap().AppendLength + RbfFile.MeasureWriteSize(1).Unwrap().AppendLength;
    Require(incompleteInitialLength >= naiveMinimum, "Long incomplete initialization passes empty-map minimum");
    ExpectInvalid(() => { using var file = RbfFile.OpenReadOnlyExisting(incompleteInitial, RbfCacheMode.Off); }, "Read-only cannot inspect open tail");
    Require(File.ReadAllBytes(incompleteInitial).AsSpan().SequenceEqual(complete.AsSpan(0, complete.Length - 8)), "Read-only left bytes unchanged");
    using (var file = RbfFile.OpenExisting(incompleteInitial, out initialRepair, RbfCacheMode.Off)) {
        Require(initialRepair.Action == RbfTailRecoveryAction.CompletedTail && initialRepair.AffectedFrameOffset == 64, "RBF completes initial body");
        Require(ReadFirstTwo(file).InitialPayload.SequenceEqual(oneRoot), "Completed initial shape and full CRC pass");
    }
    using (var file = RbfFile.OpenExisting(incompleteInitial, out var again, RbfCacheMode.Off)) { Require(again.Action == RbfTailRecoveryAction.None && ReadFirstTwo(file).InitialPayload.SequenceEqual(oneRoot), "Second open loses former repair evidence"); }

    string incompleteUpdate = NewPath();
    using (var file = RbfFile.CreateNew(incompleteUpdate, RbfCacheMode.Off)) { file.Append(0, rootHeader).Unwrap(); file.Append(1, new byte[] { 0 }).Unwrap(); file.Append(1, oneRoot).Unwrap(); file.DurableFlush(); }
    byte[] updated = File.ReadAllBytes(incompleteUpdate);
    File.WriteAllBytes(incompleteUpdate, updated[..^8]);
    using (var file = RbfFile.OpenExisting(incompleteUpdate, out updateRepair, RbfCacheMode.Off)) {
        Require(updateRepair.Action == RbfTailRecoveryAction.CompletedTail && updateRepair.AffectedFrameOffset == 100, "Update completion after actual initial boundary");
        Require(ReadFirstTwo(file).InitialPayload.SequenceEqual(new byte[] { 0 }), "Update repair preserves complete initialization");
    }
}
finally {
    // Exact files created by this probe only; no recursive deletion or scanning.
    foreach (string path in ownedPaths) { File.Delete(path); }
    Directory.Delete(workDirectory);
}

Console.WriteLine(JsonSerializer.Serialize(new {
    Runtime = Environment.Version.ToString(), Platform = Environment.OSVersion.ToString(), Checks = checks,
    RootHeaderGolden = Convert.ToHexString(rootHeader), ForkHeaderGolden = Convert.ToHexString(forkHeader),
    RootHeaderPayloadBytes = 28, ForkHeaderPayloadBytes = 44, HeaderFrameLengths = new[] { 56, 72 },
    EmptyInitialFileLengths = new[] { 100, 116 }, SnapshotPayloadLimit = RootLimit, SnapshotFrameLimit = MaxSnapshotFrame(),
    ProbeDrive = Path.GetPathRoot(workRoot), NaiveEmptyMapInitializationMinimum = naiveMinimum,
    IncompleteInitialLength = incompleteInitialLength, InitialRepair = initialRepair, UpdateRepair = updateRepair,
    Scope = "Experimental numeric identity/header codec and public RBF files; synthetic RootMap golden; counterexample to a minimum-length-only initial guard. No VersionStore implementation, prefix qualification, crash/rename/platform/performance proof"
}, new JsonSerializerOptions { WriteIndented = true }));

int MaxSnapshotFrame() => RbfFile.MeasureWriteSize(RootLimit).Unwrap().FrameLength;
string NewPath() { string path = Path.Combine(workDirectory, $"image-{ownedPaths.Count}.rbf"); ownedPaths.Add(path); return path; }
byte[] EncodeHeader(SizedPtr? source) {
    byte[] bytes = new byte[source.HasValue ? 44 : 28];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1); vsId.CopyTo(bytes, 4);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), Child);
    if (source.HasValue) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(28), Source); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(36), source.Value.Packed); }
    return bytes;
}
SizedPtr? ReadHeader(ReadOnlySpan<byte> bytes) {
    if (bytes.Length is not (28 or 44) || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 || !bytes.Slice(4, 16).SequenceEqual(vsId) || BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]) != Child) { throw new InvalidDataException("Header shape or identity"); }
    if (bytes.Length == 28) { return null; }
    ulong source = BinaryPrimitives.ReadUInt64LittleEndian(bytes[28..]);
    var ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(bytes[36..]));
    long minimumOffset = RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(28).Unwrap().AppendLength;
    if (source == 0 || source == Child || ticket.Offset < minimumOffset || ticket.Length < RbfFile.MeasureWriteSize(1).Unwrap().FrameLength || ticket.Length > MaxSnapshotFrame()) { throw new InvalidDataException("Source coordinate"); }
    return ticket;
}
(SizedPtr InitialTicket, byte[] InitialPayload) ReadFirstTwo(IRbfFile file) {
    if (file.Format != RbfFormat.Rbf3) { throw new InvalidDataException("Profile"); }
    var scan = file.ScanForward(showTombstone: true).GetEnumerator();
    if (!scan.MoveNext()) { throw new InvalidDataException(scan.TerminationError?.ToString() ?? "Missing header"); }
    var h = scan.Current;
    if (h.Tag != 0 || h.IsTombstone || h.TailMetaLength != 0 || h.PayloadLength is not (28 or 44) || h.Ticket.Offset != RbfScanBoundary.Empty.EndExclusive || h.Ticket.Length != RbfFile.MeasureWriteSize(h.PayloadLength).Unwrap().FrameLength) { throw new InvalidDataException("Header frame"); }
    using (var frame = h.ReadPooledFrame().Unwrap()) { ReadHeader(frame.PayloadAndMeta); }
    long secondOffset = file.GetPhysicalOffsetImmediatelyAfter(h.Ticket);
    if (!scan.MoveNext()) { throw new InvalidDataException(scan.TerminationError?.ToString() ?? "Missing initial"); }
    var info = scan.Current;
    if (info.Tag != 1 || info.IsTombstone || info.TailMetaLength != 0 || info.PayloadLength < 1 || info.PayloadLength > RootLimit || info.Ticket.Offset != secondOffset || info.Ticket.Length != RbfFile.MeasureWriteSize(info.PayloadLength).Unwrap().FrameLength) { throw new InvalidDataException("Initial shape"); }
    using var initial = info.ReadPooledFrame().Unwrap();
    // Deliberately only a framing/full-CRC oracle. RootMap field decoding belongs to the prior probe and future library.
    return (info.Ticket, initial.PayloadAndMeta.ToArray());
}
byte[] Mutate(byte[] input, Action<byte[]> mutate) { var copy = input.ToArray(); mutate(copy); return copy; }
void Require(bool condition, string label) { if (!condition) { throw new InvalidDataException(label); } checks++; }
void ExpectInvalid(Action action, string label) {
    bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; }
    Require(failed, label);
}
