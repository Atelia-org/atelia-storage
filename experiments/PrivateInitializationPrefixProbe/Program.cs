using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Atelia.Data.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

string workspace = Path.GetFullPath(Path.Combine("artifacts", "private-initialization-prefix-probe", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(workspace);
var groups = new Dictionary<string, int>(StringComparer.Ordinal);
string group = "";
void Check(bool condition, string label) {
    if (!condition) { throw new InvalidOperationException(label); }
    groups[group] = groups.GetValueOrDefault(group) + 1;
}
byte[] Payload(bool markers, uint id = 1) {
    byte[] bytes = new byte[24];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
    for (int i = 0; i < 16; i++) { bytes[4 + i] = (byte)(i + 1); }
    if (markers) {
        for (uint i = 0; i < 4; i++) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (int)i * 4), RbfLayout.GetFenceWord(RbfProfile.Rbf3) ^ i);
        }
    }
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), id);
    return bytes;
}
void AppendHeader(IRbfFile file, byte[] payload, uint? forcedKey) {
    if (forcedKey is uint key) {
        ((RbfFileImpl)file).Append(0, payload, default, (_, _, _, _) => key).Unwrap();
    }
    else { file.Append(0, payload).Unwrap(); }
}
byte[] Complete(string path, byte[] payload, uint? forcedKey) {
    using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
        AppendHeader(file, payload, forcedKey);
        file.DurableFlush();
    }
    return File.ReadAllBytes(path);
}
int initializationLength = checked((int)(RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(24).Unwrap().AppendLength));
var fixtures = new[] {
    (Name: "zero-key", Header: Payload(false), ForcedKey: (uint?)null, ExpectedKey: 0u),
    (Name: "marker-key", Header: Payload(true), ForcedKey: (uint?)null, ExpectedKey: 4u),
    (Name: "alternative-high-key", Header: Payload(false), ForcedKey: (uint?)0x81234567u, ExpectedKey: 0x81234567u)
};
var images = new List<object>();
foreach (var fixture in fixtures) {
    string completePath = Path.Combine(workspace, fixture.Name + ".rbf");
    byte[] image = Complete(completePath, fixture.Header, fixture.ForcedKey);
    group = fixture.Name + "/complete-public-read";
    Check(image.Length == initializationLength && image.Length == 60, "public size");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(image.Length - 8)) == fixture.ExpectedKey, "observed key");
    using (var file = RbfFile.OpenReadOnlyExisting(completePath, RbfCacheMode.Off)) {
        var scan = file.ScanForward(showTombstone: true).GetEnumerator();
        Check(scan.MoveNext(), "first physical frame");
        using var frame = scan.Current.ReadPooledFrame().Unwrap();
        Check(frame.PayloadAndMeta.SequenceEqual(fixture.Header), "full CRC and payload");
        Check(!scan.MoveNext() && scan.TerminationError is null, "single-frame EOF");
    }
    images.Add(new { fixture.Name, fixture.ExpectedKey, bytes = Convert.ToHexString(image) });

    group = fixture.Name + "/actual-partial-writes";
    for (int length = 0; length <= image.Length; length++) {
        string path = Path.Combine(workspace, fixture.Name + "-" + length + ".rbf");
        int writes = 0;
        bool partialFailed = false;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => {
                writes++;
                if (request.Offset == 0) {
                    if (request.RequestedBytes != 4) { throw new InvalidOperationException("Header write shape changed"); }
                    return Math.Min(length, 4);
                }
                if (request.Offset != 4 || request.RequestedBytes != image.Length - 4) {
                    throw new InvalidOperationException("Initialization is no longer a single forward small-frame write");
                }
                return length - 4;
            }
        };
        try {
            using var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
            if (length > 4) { AppendHeader(file, fixture.Header, fixture.ForcedKey); }
        }
        catch (IOException) { partialFailed = true; }
        finally { RbfWriteInstrumentation.Current = null; }
        byte[] actual = File.ReadAllBytes(path);
        Check(partialFailed == (length < 4 || (length > 4 && length < image.Length)), "partial failure category");
        Check(writes == (length <= 4 ? 1 : 2), "write ordering");
        Check(actual.AsSpan().SequenceEqual(image.AsSpan(0, length)), "actual sequential prefix");
        Check(MatchesInitialFramePrefix(actual, 0, fixture.Header), "accept observed prefix");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(actual), "predicate did not recover or mutate file");
    }

    group = fixture.Name + "/full-image-corruption";
    for (int index = 0; index < image.Length; index++) {
        for (int bit = 0; bit < 8; bit++) {
            byte[] bad = (byte[])image.Clone();
            bad[index] ^= (byte)(1 << bit);
            Check(!MatchesInitialFramePrefix(bad, 0, fixture.Header), "reject one-bit full-image corruption");
        }
    }
    group = fixture.Name + "/context-and-suffix";
    byte[] wrongStore = (byte[])fixture.Header.Clone();
    wrongStore[4] ^= 1;
    byte[] wrongId = (byte[])fixture.Header.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(wrongId.AsSpan(20), 2);
    byte[] wrongVersion = (byte[])fixture.Header.Clone();
    wrongVersion[0] = 2;
    Check(!MatchesInitialFramePrefix(image, 0, wrongStore), "wrong expected store");
    Check(!MatchesInitialFramePrefix(image, 0, wrongId), "wrong expected file");
    Check(!MatchesInitialFramePrefix(image, 0, wrongVersion), "wrong expected version");
    Check(!MatchesInitialFramePrefix(image, 1, fixture.Header), "wrong expected tag");
    Check(!MatchesInitialFramePrefix([.. image, 0], 0, fixture.Header), "reject any suffix");
    Check(!MatchesInitialFramePrefix("RBF1"u8, 0, fixture.Header), "reject RBF1");
    byte[] forbiddenKeyPrefix = image.AsSpan(0, 12).ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(forbiddenKeyPrefix.AsSpan(8),
        BinaryPrimitives.ReadUInt32LittleEndian(fixture.Header) ^ RbfLayout.GetFenceWord(RbfProfile.Rbf3));
    Check(!MatchesInitialFramePrefix(forbiddenKeyPrefix, 0, fixture.Header), "reject inferred raw fence key");

    // A prefix with fewer than four encoded body bytes does not uniquely determine a key.
    byte[] ambiguous = image.AsSpan(0, 11).ToArray();
    ambiguous[8] ^= 0x80;
    Check(MatchesInitialFramePrefix(ambiguous, 0, fixture.Header), "legal partial key completion");
}
group = "unknown-legal-content";
string foreignPath = Path.Combine(workspace, "other-small-valid-frame.rbf");
using (var file = RbfFile.CreateNew(foreignPath, RbfCacheMode.Off)) {
    file.Append(0, []).Unwrap();
    file.DurableFlush();
}
byte[] foreign = File.ReadAllBytes(foreignPath);
Check(foreign.Length <= initializationLength, "counterexample fits length-only rule");
using (var file = RbfFile.OpenReadOnlyExisting(foreignPath, RbfCacheMode.Off)) {
    var scan = file.ScanForward().GetEnumerator();
    Check(scan.MoveNext(), "counterexample is real RBF");
    using var frame = scan.Current.ReadPooledFrame().Unwrap();
    Check(frame.PayloadAndMeta.IsEmpty, "counterexample has valid CRC");
}
Check(!MatchesInitialFramePrefix(foreign, 0, fixtures[0].Header), "reject length-only counterexample");
string userPath = Path.Combine(workspace, "header-plus-user.rbf");
using (var file = RbfFile.CreateNew(userPath, RbfCacheMode.Off)) {
    file.Append(0, fixtures[0].Header).Unwrap();
    file.Append(0, new byte[] { 42 }).Unwrap();
    file.DurableFlush();
}
Check(!MatchesInitialFramePrefix(File.ReadAllBytes(userPath), 0, fixtures[0].Header), "header alone does not authorize suffix deletion");

group = "bounded-generic-oracle";
foreach (int length in new[] { 0, 1, 2, 3, 4, 231, 232 }) {
    byte[] payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
    string path = Path.Combine(workspace, "tiny-" + length + ".rbf");
    byte[] image = Complete(path, payload, null);
    for (int prefix = 0; prefix <= image.Length; prefix++) {
        Check(MatchesInitialFramePrefix(image.AsSpan(0, prefix), 0, payload), "bounded payload oracle");
    }
}
bool oversizedRejected = false;
try { MatchesInitialFramePrefix([], 0, new byte[233]); }
catch (ArgumentOutOfRangeException) { oversizedRejected = true; }
Check(oversizedRejected, "do not silently extend beyond bounded proof");

var result = new {
    designBaseline = "a7794cf",
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    workspace,
    scope = "Internal RBF-core prototype plus real partial-write injection and public checked reads; no production public prefix API, FrameStore, root/no-follow, deletion, process kill, Linux, rename, power-loss or package qualification.",
    initializationLength,
    checkedAssertions = groups.Values.Sum(),
    groups,
    images
};
string evidence = args.Length == 0
    ? Path.Combine("experiments", "PrivateInitializationPrefixProbe", "evidence", "2026-10-09-result.json")
    : args.Single();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidence))!);
File.WriteAllText(evidence, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"PASS {groups.Values.Sum()} assertions; {initializationLength}B initialization; evidence={Path.GetFullPath(evidence)}");

// Research prototype: all wire operations stay with existing RBF layout/footer and Data CRC/XOR.
// The proposed production public predicate has not been added to RbfFile.
static bool MatchesInitialFramePrefix(ReadOnlySpan<byte> prefix, uint tag, ReadOnlySpan<byte> payload) {
    if (payload.Length > 232) { throw new ArgumentOutOfRangeException(nameof(payload)); }
    FrameLayout layout = FrameLayout.TryCreate(RbfProfile.Rbf3, payload.Length, 0).Unwrap();
    int bodyLength = layout.FrameLength - FrameLayout.HeadLenSize - RbfLayout.TailKeySize;
    int total = RbfLayout.HeaderOnlyLength + layout.FrameLength + RbfLayout.FenceSize;
    if (prefix.Length > total) { return false; }
    Span<byte> image = stackalloc byte[268];
    RbfLayout.GetFence(RbfProfile.Rbf3).CopyTo(image);
    BinaryPrimitives.WriteUInt32LittleEndian(image[RbfLayout.FirstFrameOffset..], layout.WireFrameLength);
    int bodyStart = RbfLayout.FirstFrameOffset + FrameLayout.HeadLenSize;
    int fixedLength = Math.Min(bodyStart, prefix.Length);
    if (!prefix[..fixedLength].SequenceEqual(image[..fixedLength])) { return false; }

    Span<byte> plain = stackalloc byte[252];
    plain = plain[..bodyLength];
    payload.CopyTo(plain);
    plain.Slice(payload.Length, layout.PaddingLength).Clear();
    uint crc = RollingCrc.CrcForward(RollingCrc.DefaultInitValue, plain[..(payload.Length + layout.PaddingLength)])
        ^ RollingCrc.DefaultFinalXor;
    RbfFrameWriteCore.WritePlaintextTail(plain[(payload.Length + layout.PaddingLength)..], in layout, tag, false, crc);
    if (prefix.Length < bodyStart + sizeof(uint)) {
        // At most 3 constrained key bytes leave >=256 choices; <=63 body words + raw Fence exclude <=64.
        return true;
    }
    uint key = BinaryPrimitives.ReadUInt32LittleEndian(prefix[bodyStart..])
        ^ BinaryPrimitives.ReadUInt32LittleEndian(plain);
    uint fence = RbfLayout.GetFenceWord(RbfProfile.Rbf3);
    if (key == fence) { return false; }
    Span<byte> encoded = image.Slice(bodyStart, bodyLength);
    XorEscape.Copy(plain, encoded, key);
    for (int offset = 0; offset < encoded.Length; offset += sizeof(uint)) {
        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[offset..]) == fence) { return false; }
    }
    int keyStart = bodyStart + bodyLength;
    BinaryPrimitives.WriteUInt32LittleEndian(image[keyStart..], key);
    RbfLayout.GetFence(RbfProfile.Rbf3).CopyTo(image[(keyStart + RbfLayout.TailKeySize)..]);
    return prefix.SequenceEqual(image[..prefix.Length]);
}
