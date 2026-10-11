using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;
using Store = Atelia.FrameStore.FrameStore;

string directory = Path.GetFullPath(args.Length == 1 ? args[0]
    : throw new ArgumentException("Expected a fresh FrameStore smoke directory."));
if (Directory.Exists(directory) || File.Exists(directory)) {
    throw new IOException("FrameStore smoke directory must be fresh.");
}
Directory.CreateDirectory(directory);
string storePath = Path.Combine(directory, "data");
byte[] bufferPayload = Enumerable.Range(0, 192).Select(value => (byte)value).ToArray();
byte[] bufferMeta = [0xA1, 0xA2];
byte[] firstMeta = [0xB1, 0xB2];
byte[] secondMeta = [0xC1];
byte[] unknownPayload = "unknown-size"u8.ToArray();
byte[] unknownMeta = [0xD1, 0xD2];
var expected = new Dictionary<FrameAddress, ExpectedFrame>();
byte[] identity;
FrameAddress[] addresses;
FrameRead retained;
var writer = Store.Create(storePath, rotationThresholdBytes: 128);
using (writer) {
    writer.EnsureUsable();
    Require(!writer.IsReadOnly && writer.RecoveryReports.Count == 0, "Unexpected Create mode or recovery.");
    identity = writer.StoreId.ToArray();
    FrameAddress buffer = writer.Append(0, bufferPayload, bufferMeta).Unwrap();
    expected.Add(buffer, new(0, bufferPayload.Concat(bufferMeta).ToArray(), bufferMeta.Length));

    // Each live builder owns a file. Fixed 12B addresses allow mutually referring records.
    using var first = writer.BeginAppend(FrameAddress.EncodedSize, firstMeta.Length, out FrameAddress firstAddress);
    using var second = writer.BeginAppend(FrameAddress.EncodedSize, secondMeta.Length, out FrameAddress secondAddress);
    Require(firstAddress != secondAddress, "Interleaved builders must have independent early addresses.");
    writer.EnsureUsable(); // Active builders do not invalidate this lifecycle check.
    byte[] firstPayload = EncodeFixed(secondAddress);
    byte[] secondPayload = EncodeFixed(firstAddress);
    WriteBytes(first.PayloadAndMeta, firstPayload.AsSpan(0, 6));
    WriteBytes(second.PayloadAndMeta, secondPayload);
    WriteBytes(second.PayloadAndMeta, secondMeta);
    Require(second.EndAppend(102, secondMeta.Length).Unwrap() == secondAddress, "Sized EndAppend changed its early address.");
    expected.Add(secondAddress, new(102, secondPayload.Concat(secondMeta).ToArray(), secondMeta.Length));

    using var unknown = writer.BeginAppend();
    WriteBytes(unknown.PayloadAndMeta, unknownPayload);
    WriteBytes(first.PayloadAndMeta, firstPayload.AsSpan(6));
    WriteBytes(first.PayloadAndMeta, firstMeta);
    Require(first.EndAppend(101).Unwrap() == firstAddress, "Interleaved completion changed its early address.");
    expected.Add(firstAddress, new(101, firstPayload.Concat(firstMeta).ToArray(), firstMeta.Length));
    WriteBytes(unknown.PayloadAndMeta, unknownMeta);
    FrameAddress unknownAddress = unknown.EndAppend(103, unknownMeta.Length).Unwrap();
    expected.Add(unknownAddress, new(103, unknownPayload.Concat(unknownMeta).ToArray(), unknownMeta.Length));
    writer.ConfirmDurable();
    Require(Directory.EnumerateFiles(Path.Combine(storePath, "archive"), "*.rbf", SearchOption.AllDirectories).Any(),
        "The small rotation threshold did not archive a completed file.");
    foreach (var entry in expected) { VerifyFrame(writer, entry.Key, entry.Value); }
    retained = writer.ReadFrame(buffer).Unwrap();
    addresses = [buffer, firstAddress, secondAddress, unknownAddress];
    File.WriteAllBytes(Path.Combine(directory, "addresses.bin"), EncodeVarInt(addresses));
}
RequireDisposed(writer);
using (retained) {
    Require(retained.PayloadAndMeta.SequenceEqual(expected[addresses[0]].Body), "FrameRead did not retain its owned bytes after store Dispose.");
}

// A writable reopen retains the same addresses and does not repair healthy files.
using (var reopened = Store.Open(storePath, rotationThresholdBytes: 128)) {
    reopened.EnsureUsable();
    Require(reopened.StoreId.SequenceEqual(identity), "Writable reopen changed the store identity.");
    Require(reopened.RecoveryReports.Values.All(report => report.Action == Atelia.Rbf.RbfTailRecoveryAction.None), "Healthy reopen unexpectedly recovered a tail.");
    foreach (var entry in expected) { VerifyFrame(reopened, entry.Key, entry.Value); }
    reopened.ConfirmDurable();
}

int fileCount;
using (var cold = Store.OpenReadOnly(storePath)) {
    cold.EnsureUsable();
    Require(cold.IsReadOnly && cold.StoreId.SequenceEqual(identity), "Read-only reopen changed mode or identity.");
    var reader = new BareValueReader(File.ReadAllBytes(Path.Combine(directory, "addresses.bin")));
    foreach (FrameAddress original in addresses) {
        FrameAddress decoded = FrameAddress.ReadVarInt(ref reader);
        Require(decoded == original, "Persisted varint address did not round-trip.");
        VerifyFrame(cold, decoded, expected[original]);
    }
    reader.EnsureFullyConsumed();
    VerifyPeer(cold, addresses[1], addresses[2]);
    VerifyPeer(cold, addresses[2], addresses[1]);

    var seen = new HashSet<FrameAddress>();
    long inventoried = cold.Inventory(info => {
        ExpectedFrame frame = expected.TryGetValue(info.Address, out ExpectedFrame? found)
            ? found : throw new InvalidDataException("Inventory returned an unexpected address.");
        Require(seen.Add(info.Address), "Inventory repeated an address.");
        Require(info.Tag == frame.Tag && info.PayloadLength == frame.Body.Length - frame.MetaLength
            && info.TailMetaLength == frame.MetaLength && !info.IsTombstone, "Inventory metadata disagrees with the written frame.");
        VerifyFrame(cold, info.Address, frame);
    }).Unwrap();
    Require(inventoried == expected.Count && seen.Count == expected.Count, "Inventory did not cover every user frame.");
    var reports = new List<FrameFileAudit>();
    long audited = cold.Audit(reports.Add).Unwrap();
    Require(audited == expected.Count && reports.Sum(report => report.UserFrameCount) == expected.Count,
        "Audit user-frame totals disagree with Inventory.");
    Require(reports.All(report => report.FileId != 0 && report.HeaderPayload.Length == 24), "Audit returned an invalid file header report.");
    fileCount = reports.Count;
}
Console.WriteLine($"FrameStore public package smoke passed: {expected.Count} frames, {fileCount} files; three append forms, interleaved early addresses, archive, varint, cold reads, Inventory and Audit.");
Console.WriteLine($"Retained smoke files: {directory}");

static byte[] EncodeFixed(FrameAddress address) {
    var bytes = new byte[FrameAddress.EncodedSize];
    Require(address.TryWrite(bytes), "Cannot encode an accepted early address.");
    return bytes;
}

static byte[] EncodeVarInt(FrameAddress[] addresses) {
    var output = new ArrayBufferWriter<byte>();
    var writer = new BareValueWriter(output);
    foreach (FrameAddress address in addresses) { address.WriteVarInt(writer); }
    Require(output.WrittenCount == addresses.Sum(address => address.MeasureVarInt()), "Varint measurement disagrees with the actual output.");
    return output.WrittenSpan.ToArray();
}

static void WriteBytes(FramePayloadWriter writer, ReadOnlySpan<byte> bytes) {
    bytes.CopyTo(writer.GetSpan(bytes.Length));
    writer.Advance(bytes.Length);
}

static void VerifyFrame(Store store, FrameAddress address, ExpectedFrame expected) {
    using var frame = store.ReadFrame(address).Unwrap();
    Require(frame.Address == address && frame.Tag == expected.Tag && frame.TailMetaLength == expected.MetaLength
        && !frame.IsTombstone && frame.PayloadAndMeta.SequenceEqual(expected.Body), "A complete public read changed frame bytes or metadata.");
}

static void VerifyPeer(Store store, FrameAddress address, FrameAddress peer) {
    using var frame = store.ReadFrame(address).Unwrap();
    Require(FrameAddress.TryRead(frame.PayloadAndMeta[..FrameAddress.EncodedSize], out FrameAddress decoded)
        && decoded == peer, "A mutual reference changed after archive and reopen.");
}

static void RequireDisposed(Store store) {
    try { store.EnsureUsable(); }
    catch (ObjectDisposedException) { return; }
    throw new InvalidDataException("EnsureUsable must reject the disposed owner.");
}

static void Require(bool condition, string message) {
    if (!condition) { throw new InvalidDataException(message); }
}

internal sealed record ExpectedFrame(uint Tag, byte[] Body, int MetaLength);
