using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Rbf;

// Experimental composition, not a production FrameAddress or VersionStore codec.
const int MaxRootMapBytes = 1 << 20;
var address = new SyntheticAddress(0x89ABCDEF, 0x123456789ABCDEF0);
byte[] addressGolden = Convert.FromHexString("EFCDAB89F0DEBC9A78563412");
byte[] golden = [0x01, 0x03, 0x41, .. addressGolden];
int checks = 0;
Require(Encode([new("A", address)]).AsSpan().SequenceEqual(golden), "Independent A golden");
Require(Encode([]).AsSpan().SequenceEqual(new byte[] { 0 }), "Empty golden");
Require(ReadExact(golden)["A"] == address, "Read independent golden");

string high = new((char)0xD800, 1), low = new((char)0xDC00, 1);
string[] keys = ["", "A", "a", "\0", "\uFEFF", high, low, "\uFFFD", "é", "e\u0301", "😀", "中"];
var entries = keys.Select(key => new KeyValuePair<string, SyntheticAddress>(key, address)).ToArray();
byte[] encoded = Encode(entries);
var decoded = ReadExact(encoded);
Require(decoded.Count == keys.Length && keys.All(key => decoded[key] == address), "All code-unit keys preserved");
var reverse = ReadExact(Encode(entries.Reverse()));
Require(reverse.Count == decoded.Count && decoded.All(pair => reverse[pair.Key] == pair.Value), "Order-independent content");
Require(!encoded.AsSpan().SequenceEqual(Encode(entries.Reverse())), "No whole-map canonical bytes promise");
Require(ReadExact([0x80, 0x00]).Count == 0, "Redundant count accepted with actual consumption");
Require(ReadExact([0x01, 0x02, 0x41, 0x00, .. addressGolden])["A"] == address, "Alternative UTF16 A accepted");
Require(ReadExact([0x01, 0x83, 0x00, 0x41, .. addressGolden])["A"] == address, "Redundant string header accepted");

var composed = new BareValueReader([0x7E, .. golden, 0x5A]);
Require(composed.ReadByte() == 0x7E, "Host prefix");
Require(ReadRoot(ref composed)["A"] == address && composed.ReadByte() == 0x5A, "Root prefix leaves host suffix");
composed.EnsureFullyConsumed();
for (int length = 0; length < golden.Length; length++) {
    ExpectRootFailure(golden[..length], $"Truncation {length}");
}
ExpectRootFailure([0xFF, 0xFF, 0xFF, 0xFF, 0x0F], "Impossible count", beforeMap: true);
ExpectRootFailure([0x80, 0x80, 0x80, 0x80, 0x10], "Count overflow", beforeMap: true);
ExpectRootFailure([0x80, 0x80, 0x80, 0x80, 0x80], "Count continuation overflow", beforeMap: true);
ExpectRootFailure([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, .. addressGolden], "Huge string declaration", beforeKey: true);
ExpectRootFailure([0x01, 0x21, .. new byte[14]], "String consumes address suffix", beforeKey: true);
ExpectRootFailure([0x01, 0x07, 0xED, 0xA0, 0x80, .. addressGolden], "Invalid UTF8 surrogate");
ExpectRootFailure([0x01, 0x00, .. new byte[12]], "Invalid synthetic address");
ExpectRootFailure([0x02, 0x03, 0x41, .. addressGolden, 0x02, 0x41, 0x00, .. addressGolden], "Duplicate decoded key");
ExpectExactFailure([0x00, 0x7F], "Empty with trailing bytes");
ExpectExactFailure([.. golden, 0x7F], "A with trailing bytes");
ExpectEncodeFailure([new("A", address), new("A", address)], "Duplicate input");
ExpectEncodeFailure([new(null!, address)], "Null key");
ExpectEncodeFailure([new("A", default)], "Default address");
Require(Encode([new("A", address)], golden.Length).Length == golden.Length, "Exact small host budget");
ExpectEncodeFailure([new("A", address)], "One byte over small host budget", golden.Length - 1);
ExpectRootFailure(golden, "Decode small host budget", maxBytes: golden.Length - 1, beforeKey: true);
Require(RbfFile.MeasureWriteSize(RbfFile.MaxPayloadAndMetaLength).IsSuccess, "Actual RBF maximum accepted");
Require(RbfFile.MeasureWriteSize(RbfFile.MaxPayloadAndMetaLength + 1).IsFailure, "Actual RBF maximum plus one rejected");
string boundaryKey = new('x', MaxRootMapBytes - 16); // 1B count + 3B string header + key + 12B address.
byte[] boundaryMap = Encode([new(boundaryKey, address)]);
Require(boundaryMap.Length == MaxRootMapBytes && ReadExact(boundaryMap)[boundaryKey] == address, "Actual 1MiB boundary");
ExpectEncodeFailure([new(boundaryKey + "x", address)], "Actual boundary plus one");
ExpectRootFailure([0x81, 0x00, .. boundaryMap.AsSpan(1)], "Redundant count crosses actual byte cap", beforeKey: true);

Console.WriteLine(JsonSerializer.Serialize(new {
    Runtime = Environment.Version.ToString(), Platform = Environment.OSVersion.ToString(), Checks = checks,
    IndependentGolden = Convert.ToHexString(golden), KeysRoundTripped = keys.Length, EncodedMapBytes = encoded.Length,
    DefaultUtf8Collision = Encoding.UTF8.GetBytes(high).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(low)) &&
        Encoding.UTF8.GetBytes(high).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\uFFFD")),
    DefaultUtf16ReplacesLoneSurrogate = Convert.ToHexString(Encoding.Unicode.GetBytes(high)) == "FDFF",
    RbfPayloadAndMetaMaximum = RbfFile.MaxPayloadAndMetaLength,
    RootMapCodewordLimit = MaxRootMapBytes,
    ConservativeEntryCountBound = (MaxRootMapBytes - 1L) / 13,
    Scope = "Public Binary/Rbf composition with synthetic numeric addresses and actual 1MiB map; no FrameStore/VersionStore, persistence, near-RBF-limit allocations or performance qualification"
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

void ExpectExactFailure(byte[] bytes, string label) {
    bool failed = false;
    try { ReadExact(bytes); }
    catch (InvalidDataException) { failed = true; }
    Require(failed, label);
}

void ExpectEncodeFailure(IEnumerable<KeyValuePair<string, SyntheticAddress>> entries, string label,
    int maxBytes = MaxRootMapBytes) {
    bool failed = false;
    try { Encode(entries, maxBytes); }
    catch (InvalidDataException) { failed = true; }
    Require(failed, label);
}

static byte[] Encode(IEnumerable<KeyValuePair<string, SyntheticAddress>> entries,
    int maxBytes = MaxRootMapBytes) {
    var captured = new Dictionary<string, SyntheticAddress>(StringComparer.Ordinal);
    var plans = new List<(StringEncodingPlan Key, SyntheticAddress Address)>();
    long fields = 0;
    foreach (var pair in entries) {
        if (pair.Key is null || !pair.Value.IsNumericallyValid || pair.Key.Length + 1L + 12 > maxBytes) {
            throw new InvalidDataException("Invalid key/address or necessarily oversized key.");
        }
        StringEncodingPlan plan = BareValueEncoding.PrepareString(pair.Key);
        long nextFields = checked(fields + plan.EncodedLength + 12);
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
        writer.WriteUInt32LE(entry.Address.FileId);
        writer.WriteUInt64LE(entry.Address.Packed);
    }
    if (output.WrittenCount != length) { throw new InvalidDataException("Measured length mismatch."); }
    return output.WrittenSpan.ToArray();
}

static FrozenDictionary<string, SyntheticAddress> ReadExact(byte[] bytes) {
    var reader = new BareValueReader(bytes);
    var result = ReadRoot(ref reader);
    reader.EnsureFullyConsumed();
    return result;
}

static FrozenDictionary<string, SyntheticAddress> ReadRoot(ref BareValueReader reader,
    int maxBytes = MaxRootMapBytes, MaterializationObservations? observed = null) {
    var window = reader;
    var root = new BareValueReader(window.ReadRawBytes(Math.Min(window.RemainingCount, maxBytes)));
    uint count = root.ReadVarUInt32();
    if (count > root.RemainingCount / 13) { throw new InvalidDataException("Impossible entry count."); }
    if (observed is not null) { observed.MapConstructions++; }
    var decoded = new Dictionary<string, SyntheticAddress>(StringComparer.Ordinal);
    for (uint i = 0; i < count; i++) {
        var preview = root;
        uint header = preview.ReadVarUInt32();
        uint payloadBytes = (header & 1) == 0 ? header : header >> 1;
        long required = payloadBytes + 12L + 13L * (count - i - 1);
        if (payloadBytes > int.MaxValue || required > preview.RemainingCount) {
            throw new InvalidDataException("Key would exceed the actual remaining record budget.");
        }
        if (observed is not null) { observed.KeyReadCalls++; }
        string key = root.ReadString((int)payloadBytes);
        var address = new SyntheticAddress(root.ReadUInt32LE(), root.ReadUInt64LE());
        if (!address.IsNumericallyValid || !decoded.TryAdd(key, address)) {
            throw new InvalidDataException("Invalid address or duplicate decoded key.");
        }
    }
    var result = decoded.ToFrozenDictionary(StringComparer.Ordinal);
    var committed = reader;
    committed.ReadRawBytes(root.ConsumedCount);
    reader = committed;
    return result;
}

sealed class MaterializationObservations {
    public int MapConstructions;
    public int KeyReadCalls;
}

readonly record struct SyntheticAddress(uint FileId, ulong Packed) {
    public bool IsNumericallyValid => FileId != 0 &&
        SizedPtr.FromPacked(Packed).Offset >= RbfScanBoundary.Empty.EndExclusive &&
        SizedPtr.FromPacked(Packed).Length >= RbfFile.MeasureWriteSize(0).Value.FrameLength;
}
