using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;

string directory = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected a fresh RBF data directory."));
if (Directory.Exists(directory) || File.Exists(directory)) { throw new IOException("RBF smoke directory must be fresh."); }
Directory.CreateDirectory(directory);
string pathA = Path.Combine(directory, "a.rbf");
string pathB = Path.Combine(directory, "b.rbf");
const int payloadLength = sizeof(long) + sizeof(int);
byte[] meta = [0xA1, 0xB2, 0xC3];
RbfWriteSize size = RbfFile.MeasureWriteSize(payloadLength, meta.Length).Unwrap();
if (size.FrameLength != 44 || size.AppendLength != 48 ||
    !RbfFile.TryGetMaxPayloadLengthForAppendBudget(size.AppendLength, meta.Length, out int maximum) || maximum != 13) {
    throw new InvalidDataException("Public sizing or inverse append budget disagrees with the RBF3 wire geometry.");
}

SizedPtr ticketA;
SizedPtr ticketB;
using (IRbfFile fileA = RbfFile.CreateNew(pathA))
using (IRbfFile fileB = RbfFile.CreateNew(pathB)) {
    if (fileA.Format != RbfFormat.Rbf3 || fileB.Format != RbfFormat.Rbf3) { throw new InvalidDataException("New files must use RBF3."); }
    fileB.Append(7, "prefix"u8).Unwrap();
    long startA = fileA.TailOffset;
    long startB = fileB.TailOffset;
    using var builderA = fileA.BeginAppend(payloadLength, meta.Length, out ticketA);
    using var builderB = fileB.BeginAppend(payloadLength, meta.Length, out ticketB);
    if (ticketA.Offset != startA || ticketB.Offset != startB || ticketA.Length != size.FrameLength ||
        ticketB.Length != size.FrameLength || fileA.TailOffset != startA || fileB.TailOffset != startB) {
        throw new InvalidDataException("Begin must bind a ticket without advancing the tail.");
    }
    builderA.PayloadAndMeta.Write(Encode(ticketB));
    builderA.PayloadAndMeta.Write(meta);
    builderB.PayloadAndMeta.Write(Encode(ticketA));
    builderB.PayloadAndMeta.Write(meta);
    if (builderA.EndAppend(101).Unwrap() != ticketA || builderB.EndAppend(102, meta.Length).Unwrap() != ticketB ||
        fileA.TailOffset != startA + size.AppendLength || fileB.TailOffset != startB + size.AppendLength) {
        throw new InvalidDataException("Completed tickets or tail advancement disagree with Begin declarations.");
    }
    Verify(fileA, ticketA, ticketB, 101, meta);
    Verify(fileB, ticketB, ticketA, 102, meta);
    fileA.DurableFlush();
    fileB.DurableFlush();
}
using (IRbfFile fileA = RbfFile.OpenReadOnlyExisting(pathA))
using (IRbfFile fileB = RbfFile.OpenReadOnlyExisting(pathB)) {
    Verify(fileA, ticketA, ticketB, 101, meta);
    Verify(fileB, ticketB, ticketA, 102, meta);
}
// Also check healthy writable reopen without adding another frame.
using (IRbfFile reopened = RbfFile.OpenExisting(pathA, out var recovery)) {
    if (recovery.Action != RbfTailRecoveryAction.None) { throw new InvalidDataException("Healthy reopen must not repair the tail."); }
    Verify(reopened, ticketA, ticketB, 101, meta);
}
Console.WriteLine("Rbf public package smoke passed: sizing, inverse budget, early tickets, split TailMeta, mutual references, reads and cold reopen.");
Console.WriteLine($"Retained smoke files: {directory}");

static byte[] Encode(SizedPtr ticket) {
    byte[] bytes = new byte[sizeof(long) + sizeof(int)];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, ticket.Offset);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(long)), ticket.Length);
    return bytes;
}

static void Verify(IRbfFile file, SizedPtr ticket, SizedPtr reference, uint tag, byte[] meta) {
    if (file.Format != RbfFormat.Rbf3) { throw new InvalidDataException("Reopened format changed."); }
    using var frame = file.ReadPooledFrame(ticket).Unwrap();
    byte[] expected = Encode(reference).Concat(meta).ToArray();
    if (frame.Ticket != ticket || frame.Tag != tag || frame.IsTombstone || frame.TailMetaLength != meta.Length ||
        !frame.PayloadAndMeta.SequenceEqual(expected)) { throw new InvalidDataException("Public read did not preserve the frame and its reference."); }
}
