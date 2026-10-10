using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Atelia.Binary;
using Atelia.FrameStore;
using Atelia.Rbf;
using Store = Atelia.FrameStore.FrameStore;

// Experimental RootMap composition over production public codecs; no VersionStore implementation.
const int MaxRootMapBytes = 1 << 20;
const int MinAddressBytes = 3;
const int MinEntryBytes = 1 + MinAddressBytes;
int checks = 0;

// Independent numeric vectors: fixed12 is input only; Base128 goldens are derived from the
// SizedPtr serialization bit allocation, without calling Serialize or the VarInt writer.
byte[] minimumFixed = Convert.FromHexString("010000000700000400000000"); // FileId=1, offset=4, length=28.
byte[] maximumFixed = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFF");
byte[] minimumVar = [0x01, 0x87, 0x02]; // Serialized ticket=0x107, not Packed=0x04000007.
byte[] maximumVar = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
FrameAddress minimum = ReadFixed(minimumFixed);
FrameAddress maximum = ReadFixed(maximumFixed);
FrameAddress legacyHigh = ReadFixed(Convert.FromHexString("EFCDAB89F0DEBC9A78563412"));
byte[] golden = [0x01, 0x03, 0x41, .. minimumVar];
byte[] maximumGolden = [0x01, 0x03, 0x41, .. maximumVar];
Require(minimum.MeasureVarInt() == 3 && EncodeAddress(minimum).AsSpan().SequenceEqual(minimumVar), "Independent minimum address golden");
Require(maximum.MeasureVarInt() == 15 && EncodeAddress(maximum).AsSpan().SequenceEqual(maximumVar), "Independent maximum address golden");
Require(ReadAddressExact(minimumVar) == minimum && ReadAddressExact(maximumVar) == maximum, "Read independent address goldens");
Require(ReadAddressExact(EncodeAddress(legacyHigh)) == legacyHigh, "Historical high fixed12 input through public variable codec");
byte[] fixedRoundTrip = new byte[FrameAddress.EncodedSize];
Require(minimum.TryWrite(fixedRoundTrip) && fixedRoundTrip.AsSpan().SequenceEqual(minimumFixed), "Fixed12 codec remains available");
Require(Encode([new("A", minimum)]).AsSpan().SequenceEqual(golden), "Independent A RootMap golden");
Require(Encode([new("A", maximum)]).AsSpan().SequenceEqual(maximumGolden), "Independent maximum A RootMap golden");
Require(Encode([]).AsSpan().SequenceEqual(new byte[] { 0 }), "Empty golden");
Require(ReadExact(golden)["A"] == minimum, "Read independent RootMap golden");
Require(ReadExact([0x02, 0x00, .. minimumVar, 0x03, 0x41, .. minimumVar]).Count == 2, "Two minimum entries satisfy the 4B lower bound");

// This host file is an experiment container, not a Snapshot or VersionStore publication.
using var sandbox = new ProbeSandbox();
string root = Path.Combine(sandbox.Path, "store");
string hostPath = Path.Combine(sandbox.Path, "rootmap.bin");
FrameAddress first, second;
byte[] storeId;
byte[] persistedMap;
using (var store = Store.Create(root)) {
    storeId = store.StoreId.ToArray();
    first = store.Append(17, [1, 2, 3], [4]).Unwrap();
    using (var builder = store.BeginAppend(6, 0, out var early)) {
        byte[] earlyBytes = EncodeAddress(early);
        var payloadWriter = builder.PayloadAndMeta;
        "second"u8.CopyTo(payloadWriter.GetSpan(6));
        payloadWriter.Advance(6);
        second = builder.EndAppend(23).Unwrap();
        Require(second == early && earlyBytes.AsSpan().SequenceEqual(EncodeAddress(second)), "Early and completed address have identical variable encodings");
    }
    persistedMap = Encode([new("first", first), new("second", second)]);
    store.ConfirmDurable();
    File.WriteAllBytes(hostPath, persistedMap);
}
var recovered = ReadExact(File.ReadAllBytes(hostPath));
Require(recovered.Count == 2 && recovered["first"] == first && recovered["second"] == second, "Persisted RootMap restores issued addresses");
using (var store = Store.OpenReadOnly(root)) {
    Require(store.IsReadOnly && store.StoreId.SequenceEqual(storeId), "Read-only reopen retains store identity");
    using var firstRead = store.ReadFrame(recovered["first"]).Unwrap();
    using var secondRead = store.ReadFrame(recovered["second"]).Unwrap();
    Require(firstRead.Address == first && firstRead.Tag == 17 && firstRead.TailMetaLength == 1 &&
        firstRead.PayloadAndMeta.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Read restored first address and tail metadata");
    Require(secondRead.Address == second && secondRead.Tag == 23 && secondRead.TailMetaLength == 0 &&
        secondRead.PayloadAndMeta.SequenceEqual("second"u8), "Read restored second address");
}
// Keep all general RootMap/key and 1MiB vectors on actual public issued addresses.
FrameAddress address = first.MeasureVarInt() <= second.MeasureVarInt() ? first : second;
byte[] addressBytes = EncodeAddress(address);
Require(addressBytes.Length == address.MeasureVarInt(), "Issued address measured length");
byte[] issuedMap = Encode([new("A", address)]);
string high = new((char)0xD800, 1), low = new((char)0xDC00, 1);
string[] keys = ["", "A", "a", "\0", "\uFEFF", high, low, "\uFFFD", "é", "e\u0301", "😀", "中"];
var entries = keys.Select(key => new KeyValuePair<string, FrameAddress>(key, address)).ToArray();
byte[] encoded = Encode(entries);
var decoded = ReadExact(encoded);
Require(decoded.Count == keys.Length && keys.All(key => decoded[key] == address), "All code-unit keys preserved");
var reverse = ReadExact(Encode(entries.Reverse()));
Require(reverse.Count == decoded.Count && decoded.All(pair => reverse[pair.Key] == pair.Value), "Order-independent content");
Require(!encoded.AsSpan().SequenceEqual(Encode(entries.Reverse())), "No whole-map canonical bytes promise");
Require(ReadExact([0x80, 0x00]).Count == 0, "Redundant count accepted with actual consumption");
Require(ReadExact([0x01, 0x02, 0x41, 0x00, .. addressBytes])["A"] == address, "Alternative UTF16 A accepted");
Require(ReadExact([0x01, 0x83, 0x00, 0x41, .. addressBytes])["A"] == address, "Redundant string header accepted");
byte[] redundantFileId = [0x81, 0x00, 0x87, 0x02];
byte[] redundantTicket = [0x01, 0x87, 0x82, 0x00];
byte[] fullWidthRedundant = [0x81, 0x80, 0x80, 0x80, 0x00, 0x87, 0x82, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00];
Require(ReadExact([0x01, 0x03, 0x41, .. redundantFileId])["A"] == minimum, "Redundant FileId accepted");
Require(ReadExact([0x01, 0x03, 0x41, .. redundantTicket])["A"] == minimum, "Redundant serialized ticket accepted");
Require(ReadExact([0x01, 0x03, 0x41, .. fullWidthRedundant])["A"] == minimum, "Full-width redundant address accepted");
var redundantReader = new BareValueReader([0x01, 0x03, 0x41, .. redundantTicket, 0x5A]);
Require(ReadRoot(ref redundantReader)["A"] == minimum && redundantReader.ConsumedCount == golden.Length + 1 &&
    redundantReader.ReadByte() == 0x5A, "Reader accounts for redundant address bytes and retains suffix");

var composed = new BareValueReader([0x7E, .. issuedMap, 0x5A]);
Require(composed.ReadByte() == 0x7E, "Host prefix");
Require(ReadRoot(ref composed)["A"] == address && composed.ReadByte() == 0x5A, "Root prefix leaves host suffix");
composed.EnsureFullyConsumed();
for (int length = 0; length < golden.Length; length++) {
    ExpectRootFailure(golden[..length], $"Minimum golden truncation {length}");
}
for (int length = 0; length < maximumGolden.Length; length++) {
    ExpectRootFailure(maximumGolden[..length], $"Maximum golden truncation {length}");
}
for (int length = 0; length < persistedMap.Length; length++) {
    ExpectRootFailure(persistedMap[..length], $"Issued map truncation {length}");
}
ExpectRootFailure([0xFF, 0xFF, 0xFF, 0xFF, 0x0F], "Impossible count", beforeMap: true);
ExpectRootFailure([0x80, 0x80, 0x80, 0x80, 0x10], "Count overflow", beforeMap: true);
ExpectRootFailure([0x80, 0x80, 0x80, 0x80, 0x80], "Count continuation overflow", beforeMap: true);
ExpectRootFailure([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, .. addressBytes], "Huge string declaration", beforeKey: true);
ExpectRootFailure([0x01, 0x09, 0x41, 0x42, 0x43, 0x44, 0x01, 0x87], "String consumes minimum address suffix", beforeKey: true);
ExpectRootFailure([0x01, 0x07, 0xED, 0xA0, 0x80, .. addressBytes], "Invalid UTF8 surrogate");
byte[][] invalidAddresses = [
    [0x00, 0x87, 0x02], // Zero FileId.
    [0x01, 0x83, 0x02], // offset=0, length=28.
    [0x01, 0x86, 0x02], // offset=4, length=24.
    [0x01, 0x00],
    [0x80, 0x80, 0x80, 0x80, 0x10, 0x87, 0x02],
    [0x80, 0x80, 0x80, 0x80, 0x80, 0x87, 0x02],
    [0x01, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02],
    [0x01, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80]
];
for (int i = 0; i < invalidAddresses.Length; i++) {
    ExpectAddressFailure(invalidAddresses[i], typeof(InvalidDataException), $"Invalid public address {i}");
    ExpectRootFailure([0x01, 0x03, 0x41, .. invalidAddresses[i]], $"Invalid address in RootMap {i}");
}
ExpectAddressFailure([0x01], typeof(EndOfStreamException), "Truncated public ticket");
ExpectAddressFailure([0x81], typeof(EndOfStreamException), "Truncated public FileId");
ExpectAddressFailure([0x01, 0x87], typeof(EndOfStreamException), "Truncated public serialized ticket");
var priorCursor = new BareValueReader([0x7E, 0x01, 0x03, 0x41, .. invalidAddresses[0]]);
priorCursor.ReadByte();
bool priorFailed = false;
try { ReadRoot(ref priorCursor); }
catch (InvalidDataException) { priorFailed = true; }
Require(priorFailed && priorCursor.ConsumedCount == 1, "RootMap failure preserves a nonzero original cursor");
ExpectRootFailure([0x02, 0x03, 0x41, .. addressBytes, 0x02, 0x41, 0x00, .. addressBytes], "Duplicate decoded key");
ExpectRootFailure([0x02, 0x03, 0x41, .. minimumVar, 0x03, 0x41, .. redundantFileId], "Duplicate key across address representations");
ExpectExactFailure([0x00, 0x7F], "Empty with trailing bytes");
ExpectExactFailure([.. issuedMap, 0x7F], "A with trailing bytes");
ExpectEncodeFailure([new("A", address), new("A", address)], "Duplicate input");
ExpectEncodeFailure([new(null!, address)], "Null key");
ExpectEncodeFailure([new("A", default)], "Default address");
bool defaultMeasureFailed = false;
try { default(FrameAddress).MeasureVarInt(); }
catch (InvalidOperationException) { defaultMeasureFailed = true; }
Require(defaultMeasureFailed, "Default public address cannot be measured");
var untouchedOutput = new ArrayBufferWriter<byte>();
bool defaultWriteFailed = false;
try { default(FrameAddress).WriteVarInt(new BareValueWriter(untouchedOutput)); }
catch (InvalidOperationException) { defaultWriteFailed = true; }
Require(defaultWriteFailed && untouchedOutput.WrittenCount == 0, "Default public address rejects before output");
Require(Encode([new("A", address)], issuedMap.Length).Length == issuedMap.Length, "Exact small host budget");
ExpectEncodeFailure([new("A", address)], "One byte over small host budget", issuedMap.Length - 1);
ExpectRootFailure(golden, "Decode small host budget", maxBytes: golden.Length - 1, beforeKey: true);
Require(RbfFile.MeasureWriteSize(RbfFile.MaxPayloadAndMetaLength).IsSuccess, "Actual RBF maximum accepted");
Require(RbfFile.MeasureWriteSize(RbfFile.MaxPayloadAndMetaLength + 1).IsFailure, "Actual RBF maximum plus one rejected");
// A key near 1MiB has a 3B string header. The address cost is measured from an issued address.
string boundaryKey = new('x', MaxRootMapBytes - 1 - 3 - address.MeasureVarInt());
byte[] boundaryMap = Encode([new(boundaryKey, address)]);
Require(boundaryMap.Length == MaxRootMapBytes && ReadExact(boundaryMap)[boundaryKey] == address, "Actual 1MiB boundary");
ExpectEncodeFailure([new(boundaryKey + "x", address)], "Actual boundary plus one");
ExpectRootFailure([0x81, 0x00, .. boundaryMap.AsSpan(1)], "Redundant count crosses actual byte cap");
byte[] redundantIssued = AddRedundantFileIdByte(addressBytes);
Require(ReadAddressExact(redundantIssued) == address && redundantIssued.Length == addressBytes.Length + 1, "Issued redundant address denotes the same numeric value");
byte[] redundantBoundary = [.. boundaryMap.AsSpan(0, boundaryMap.Length - addressBytes.Length), .. redundantIssued];
ExpectRootFailure(redundantBoundary, "Redundant address crosses actual byte cap");
Require(ReadExact([0x01, 0x03, 0x41, .. redundantIssued])["A"] == address, "Issued redundant address accepted below byte cap");

Console.WriteLine(JsonSerializer.Serialize(new {
    Runtime = Environment.Version.ToString(), Platform = Environment.OSVersion.ToString(), Checks = checks,
    IndependentGolden = Convert.ToHexString(golden), IndependentMinimumAddressGolden = Convert.ToHexString(minimumVar),
    IndependentMaximumAddressGolden = Convert.ToHexString(maximumVar), KeysRoundTripped = keys.Length,
    EncodedMapBytes = encoded.Length, IssuedAddressBytes = addressBytes.Length,
    PersistedMapBytes = persistedMap.Length, PersistedFramesReadBack = recovered.Count,
    DefaultUtf8Collision = Encoding.UTF8.GetBytes(high).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(low)) &&
        Encoding.UTF8.GetBytes(high).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\uFFFD")),
    DefaultUtf16ReplacesLoneSurrogate = Convert.ToHexString(Encoding.Unicode.GetBytes(high)) == "FDFF",
    RbfPayloadAndMetaMaximum = RbfFile.MaxPayloadAndMetaLength,
    RootMapCodewordLimit = MaxRootMapBytes,
    ConservativeEntryCountBound = (MaxRootMapBytes - 1L) / MinEntryBytes,
    Scope = "Public Binary/FrameAddress composition, real FrameStore issuance and Create/Append/known-size BeginAppend/EndAppend/ConfirmDurable/Dispose/RO RootMap address readback, actual 1MiB vectors; no VersionStore publication, crash, package or performance qualification"
}, new JsonSerializerOptions { WriteIndented = true }));

void Require(bool condition, string label) {
    if (!condition) { throw new InvalidDataException(label); }
    checks++;
}

void ExpectRootFailure(byte[] bytes, string label, int maxBytes = MaxRootMapBytes,
    bool beforeMap = false, bool beforeKey = false) {
    var reader = new BareValueReader(bytes);
    var observed = new MaterializationObservations();
    Exception? failure = null;
    try { ReadRoot(ref reader, maxBytes, observed); }
    catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { failure = error; }
    Require(failure is not null && reader.ConsumedCount == 0 &&
        (!beforeMap || observed.MapConstructions == 0) &&
        (!beforeKey || observed.KeyReadCalls == 0), label);
}

void ExpectAddressFailure(byte[] bytes, Type failureType, string label) {
    var reader = new BareValueReader([0x7E, .. bytes]);
    reader.ReadByte();
    Exception? failure = null;
    try { FrameAddress.ReadVarInt(ref reader); }
    catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { failure = error; }
    Require(failure?.GetType() == failureType && reader.ConsumedCount == 1, label);
}

void ExpectExactFailure(byte[] bytes, string label) {
    bool failed = false;
    try { ReadExact(bytes); }
    catch (InvalidDataException) { failed = true; }
    Require(failed, label);
}

void ExpectEncodeFailure(IEnumerable<KeyValuePair<string, FrameAddress>> entries, string label,
    int maxBytes = MaxRootMapBytes) {
    bool failed = false;
    try { Encode(entries, maxBytes); }
    catch (InvalidDataException) { failed = true; }
    Require(failed, label);
}

static FrameAddress ReadFixed(byte[] bytes) {
    if (!FrameAddress.TryRead(bytes, out var address)) { throw new InvalidDataException("Invalid independent fixed12 input."); }
    return address;
}

static byte[] EncodeAddress(FrameAddress address) {
    var output = new ArrayBufferWriter<byte>(address.MeasureVarInt());
    address.WriteVarInt(new BareValueWriter(output));
    return output.WrittenSpan.ToArray();
}

static FrameAddress ReadAddressExact(byte[] bytes) {
    var reader = new BareValueReader(bytes);
    var address = FrameAddress.ReadVarInt(ref reader);
    reader.EnsureFullyConsumed();
    return address;
}

// Mutate only a Base128 field's lexical form; no FrameStore/RBF framing encoder is copied.
static byte[] AddRedundantFileIdByte(byte[] bytes) {
    int final = 0;
    while ((bytes[final] & 0x80) != 0) { final++; }
    if (final >= 4) { throw new InvalidDataException("Issued FileId cannot acquire another legal redundant byte."); }
    byte[] result = new byte[bytes.Length + 1];
    bytes.AsSpan(0, final).CopyTo(result);
    result[final] = (byte)(bytes[final] | 0x80);
    result[final + 1] = 0;
    bytes.AsSpan(final + 1).CopyTo(result.AsSpan(final + 2));
    return result;
}

static byte[] Encode(IEnumerable<KeyValuePair<string, FrameAddress>> entries,
    int maxBytes = MaxRootMapBytes) {
    var captured = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
    var plans = new List<(StringEncodingPlan Key, FrameAddress Address)>();
    long fields = 0;
    foreach (var pair in entries) {
        if (pair.Key is null || pair.Value == default || pair.Key.Length + 1L + MinAddressBytes > maxBytes) {
            throw new InvalidDataException("Invalid key/address or necessarily oversized key.");
        }
        StringEncodingPlan plan = BareValueEncoding.PrepareString(pair.Key);
        long nextFields = checked(fields + plan.EncodedLength + pair.Value.MeasureVarInt());
        long total = checked(BareValueEncoding.MeasureVarUInt32((uint)plans.Count + 1) + nextFields);
        if (total > maxBytes || !captured.TryAdd(pair.Key, pair.Value)) {
            throw new InvalidDataException("Oversized map or duplicate key.");
        }
        fields = nextFields;
        plans.Add((plan, pair.Value));
    }
    _ = captured.ToFrozenDictionary(StringComparer.Ordinal);
    long length = checked(BareValueEncoding.MeasureVarUInt32((uint)plans.Count) + fields);
    if (length > maxBytes) { throw new InvalidDataException("Oversized map."); }
    var output = new ArrayBufferWriter<byte>((int)length);
    var writer = new BareValueWriter(output);
    writer.WriteVarUInt32((uint)plans.Count);
    foreach (var entry in plans) {
        writer.WriteString(entry.Key);
        entry.Address.WriteVarInt(writer);
    }
    if (output.WrittenCount != length) { throw new InvalidDataException("Measured length mismatch."); }
    return output.WrittenSpan.ToArray();
}

static FrozenDictionary<string, FrameAddress> ReadExact(byte[] bytes) {
    var reader = new BareValueReader(bytes);
    var result = ReadRoot(ref reader);
    reader.EnsureFullyConsumed();
    return result;
}

static FrozenDictionary<string, FrameAddress> ReadRoot(ref BareValueReader reader,
    int maxBytes = MaxRootMapBytes, MaterializationObservations? observed = null) {
    var window = reader;
    var root = new BareValueReader(window.ReadRawBytes(Math.Min(window.RemainingCount, maxBytes)));
    uint count = root.ReadVarUInt32();
    if (count > root.RemainingCount / MinEntryBytes) { throw new InvalidDataException("Impossible entry count."); }
    if (observed is not null) { observed.MapConstructions++; }
    var decoded = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
    for (uint i = 0; i < count; i++) {
        var preview = root;
        uint header = preview.ReadVarUInt32();
        uint payloadBytes = (header & 1) == 0 ? header : header >> 1;
        long required = payloadBytes + (long)MinAddressBytes + (long)MinEntryBytes * (count - i - 1);
        if (payloadBytes > int.MaxValue || required > preview.RemainingCount) {
            throw new InvalidDataException("Key would exceed the actual remaining record budget.");
        }
        if (observed is not null) { observed.KeyReadCalls++; }
        string key = root.ReadString((int)payloadBytes);
        FrameAddress address = FrameAddress.ReadVarInt(ref root);
        if (!decoded.TryAdd(key, address)) { throw new InvalidDataException("Duplicate decoded key."); }
    }
    var result = decoded.ToFrozenDictionary(StringComparer.Ordinal);
    var committed = reader;
    // Commit actual codeword bytes. Re-measuring decoded addresses would lose legal redundancy.
    committed.ReadRawBytes(root.ConsumedCount);
    reader = committed;
    return result;
}

sealed class MaterializationObservations {
    public int MapConstructions;
    public int KeyReadCalls;
}

sealed class ProbeSandbox : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RootMapCodecProbe-" + Guid.NewGuid().ToString("N"));

    public ProbeSandbox() => Directory.CreateDirectory(Path);

    public void Dispose() {
        string resolved = System.IO.Path.GetFullPath(Path);
        string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(resolved).StartsWith("RootMapCodecProbe-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Probe cleanup escaped its generated TEMP directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
