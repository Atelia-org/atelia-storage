using System.Buffers;
using Atelia.Binary;

// This application's schema owns the version, key ordering and address interpretation.
// It is a synthetic consumer, not the future FrameStore/VersionStore record format.
var addressBytes = new ArrayBufferWriter<byte>();
var addressWriter = new BareValueWriter(addressBytes);
WriteAddress(addressWriter, new Address(1, 0x0102030405060708));
Require(addressBytes.WrittenSpan.SequenceEqual(new byte[] { 1, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1 }),
    "The application address schema must be exactly 12 bytes in little endian.");

var roots = new Dictionary<string, Address>(StringComparer.Ordinal) {
    ["agent"] = new Address(2, 0x1020304050607080),
    ["世界"] = new Address(uint.MaxValue, ulong.MaxValue),
    ["gym/branch"] = new Address(17, 0),
};
var entries = roots.OrderBy(pair => pair.Key, StringComparer.Ordinal)
    .Select(pair => (Key: pair.Key, KeyPlan: BareValueEncoding.PrepareString(pair.Key), Value: pair.Value)).ToArray();
foreach (var entry in entries) {
    Require(entry.KeyPlan.EncodedLength == BareValueEncoding.MeasureString(entry.Key),
        "The string plan and default string measure must agree.");
}

string text = string.Concat(Enumerable.Repeat("agent state 世界;", 400));
var textPlan = BareValueEncoding.PrepareControlledString(text, ValueCompression.Brotli);
byte[] mutable = Enumerable.Repeat((byte)0xAB, 8192).ToArray();
var bytesPlan = BareValueEncoding.PrepareControlledBytes(mutable, ValueCompression.Brotli);
byte[] tinyMutable = [0xDE, 0xAD, 0xBE, 0xEF];
var rawPlan = BareValueEncoding.PrepareControlledBytes(tinyMutable);
Array.Fill(mutable, (byte)0);
Array.Fill(tinyMutable, (byte)0);

long budget = checked(1L + BareValueEncoding.MeasureVarUInt32((uint)entries.Length) +
    entries.Sum(entry => checked(entry.KeyPlan.EncodedLength + 12)) +
    textPlan.EncodedLength + bytesPlan.EncodedLength + rawPlan.EncodedLength + ControlledValueEncodingPlan.Null.EncodedLength);
Require(budget <= int.MaxValue, "The application must check its complete record before converting a long budget to int.");
var record = new ArrayBufferWriter<byte>((int)budget);
var writer = new BareValueWriter(record);
writer.WriteByte(1);
writer.WriteVarUInt32((uint)entries.Length);
foreach (var entry in entries) {
    writer.WriteString(entry.KeyPlan);
    WriteAddress(writer, entry.Value);
}
int controlledOffset = record.WrittenCount;
writer.WritePreparedValue(textPlan);
writer.WritePreparedValue(bytesPlan);
writer.WritePreparedValue(rawPlan);
writer.WritePreparedValue(ControlledValueEncodingPlan.Null);
Require(record.WrittenCount == budget, "Prepared values and plain fields must match the measured complete record budget.");
Require(record.WrittenSpan[controlledOffset] == 2 && textPlan.EncodedLength < 1 + BareValueEncoding.MeasureString(text),
    "The selected Brotli plan must store a smaller complete encoding for this repetitive fixture.");

var reader = new BareValueReader(record.WrittenSpan);
Require(reader.ReadByte() == 1, "Wrong application record version.");
Require(reader.ReadVarUInt32() == entries.Length, "Wrong application root count.");
foreach (var entry in entries) {
    Require(reader.ReadString(1024) == entry.Key && ReadAddress(ref reader) == entry.Value,
        "The sorted key-to-address dictionary failed public API readback.");
}
Require(reader.ReadControlledString(65536, 65536) == text, "The controlled string failed readback.");
byte[] owned = reader.ReadControlledBytes(65536, 65536) ?? throw new InvalidDataException("Unexpected null bytes.");
Require(owned.Length == 8192 && owned.All(value => value == 0xAB), "The prepared compressed byte snapshot changed with its source.");
Require(reader.ReadControlledBytes(65536, 65536)!.AsSpan().SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    "The prepared raw byte snapshot changed with its source.");
Require(reader.ReadControlledBytes(0, 0) is null, "Null must be accepted without decoded-body allocation.");
reader.EnsureFullyConsumed();
Require(reader.ConsumedCount == budget && reader.End, "The consumer must bound and completely parse its record.");

var repeated = new ArrayBufferWriter<byte>();
var repeatedWriter = new BareValueWriter(repeated);
repeatedWriter.WritePreparedValue(bytesPlan);
repeatedWriter.WritePreparedValue(bytesPlan);
Require(repeated.WrittenCount == checked(bytesPlan.EncodedLength * 2) &&
    repeated.WrittenSpan[..(int)bytesPlan.EncodedLength].SequenceEqual(repeated.WrittenSpan[(int)bytesPlan.EncodedLength..]),
    "Repeated writes of one prepared value must use the same frozen encoding.");

byte[] lz4Source = Enumerable.Repeat((byte)0x7B, 16384).ToArray();
var lz4Plan = BareValueEncoding.PrepareControlledBytes(lz4Source, ValueCompression.Lz4Block);
var lz4TextPlan = BareValueEncoding.PrepareControlledString(text, ValueCompression.Lz4Block);
Array.Fill(lz4Source, (byte)0);
var mixed = new ArrayBufferWriter<byte>();
var mixedWriter = new BareValueWriter(mixed);
mixedWriter.WritePreparedValue(textPlan); // Existing Brotli followed by the new method.
int lz4Offset = mixed.WrittenCount;
mixedWriter.WritePreparedValue(lz4Plan);
mixedWriter.WritePreparedValue(lz4TextPlan);
Require(mixed.WrittenSpan[lz4Offset] == 3 && mixed.WrittenCount ==
    textPlan.EncodedLength + lz4Plan.EncodedLength + lz4TextPlan.EncodedLength, "LZ4 block plan/control/budget mismatch.");
var mixedReader = new BareValueReader(mixed.WrittenSpan);
Require(mixedReader.ReadControlledString(65536, 65536) == text, "Mixed Brotli readback failed.");
byte[] lz4Owned = mixedReader.ReadControlledBytes(65536, 65536)!;
Require(lz4Owned.Length == 16384 && lz4Owned.All(value => value == 0x7B), "LZ4 owned snapshot changed.");
Require(mixedReader.ReadControlledString(65536, 65536) == text, "LZ4 string readback failed.");
mixedReader.EnsureFullyConsumed();
var invalidLz4Reader = new BareValueReader(Convert.FromHexString("030C212F20AA00000750AAAAAAAAAA"));
bool rejectedInvalidOffset = false;
try { invalidLz4Reader.ReadControlledBytes(); }
catch (InvalidDataException) { rejectedInvalidOffset = true; }
Require(rejectedInvalidOffset && invalidLz4Reader.ConsumedCount == 0, "Invalid LZ4 offset must fail without advancing.");

Console.WriteLine($"Binary public package smoke passed: 12B address, keyed roots, exact {budget}B base record, controlled Brotli/LZ4 block/raw/null, owned snapshots and complete readback.");

static void WriteAddress(BareValueWriter writer, Address address) {
    writer.WriteUInt32LE(address.FileId);
    writer.WriteUInt64LE(address.PackedTicket);
}

static Address ReadAddress(ref BareValueReader reader) => new(reader.ReadUInt32LE(), reader.ReadUInt64LE());
static void Require(bool condition, string message) {
    if (!condition) { throw new InvalidDataException(message); }
}

readonly record struct Address(uint FileId, ulong PackedTicket);
