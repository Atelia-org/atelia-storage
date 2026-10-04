using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;

const int ticketBytes = sizeof(long) + sizeof(int);
string parentDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : Path.GetTempPath());
Directory.CreateDirectory(parentDirectory);
string parentPrefix = Path.TrimEndingDirectorySeparator(parentDirectory);
if (!parentPrefix.EndsWith(Path.DirectorySeparatorChar)) {
    parentPrefix += Path.DirectorySeparatorChar;
}
string workDirectory = Path.GetFullPath(Path.Combine(parentDirectory, $"rbf-sized-append-smoke-{Guid.NewGuid():N}"));
if (!workDirectory.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase)) {
    throw new InvalidOperationException("The smoke working directory must be a child of the selected parent directory.");
}
if (Directory.Exists(workDirectory)) {
    throw new IOException("The generated smoke working directory already exists.");
}
Directory.CreateDirectory(workDirectory);
string pathA = Path.Combine(workDirectory, "a.rbf");
string pathB = Path.Combine(workDirectory, "b.rbf");

try {
    RbfWriteSize size = RbfFile.MeasureWriteSize(ticketBytes, 0).Unwrap();
    if (!RbfFile.TryGetMaxPayloadLengthForAppendBudget(size.AppendLength, 0, out int maxPayload) || maxPayload != ticketBytes) {
        throw new InvalidOperationException("Measure and inverse append-budget calculation did not agree.");
    }
    Console.WriteLine($"A {ticketBytes}-byte address uses {size.FrameLength} frame bytes and {size.AppendLength} append bytes; inverse budget returns {maxPayload} payload bytes.");

    SizedPtr ticketA;
    SizedPtr ticketB;
    using (IRbfFile fileA = RbfFile.CreateNew(pathA))
    using (IRbfFile fileB = RbfFile.CreateNew(pathB)) {
        fileB.Append(2, new byte[] { 0xB0, 0xB1, 0xB2, 0xB3 }).Unwrap();

        using var builderA = fileA.BeginAppend(ticketBytes, 0, out ticketA);
        using var builderB = fileB.BeginAppend(ticketBytes, 0, out ticketB);
        builderA.PayloadAndMeta.Write(EncodeTicket(ticketB));
        builderB.PayloadAndMeta.Write(EncodeTicket(ticketA));

        if (builderA.EndAppend(101).Unwrap() != ticketA || builderB.EndAppend(102).Unwrap() != ticketB) {
            throw new InvalidOperationException("The completed ticket differed from the early ticket.");
        }
        fileA.DurableFlush();
        fileB.DurableFlush();
    }

    using (IRbfFile fileA = RbfFile.OpenReadOnlyExisting(pathA))
    using (IRbfFile fileB = RbfFile.OpenReadOnlyExisting(pathB)) {
        using var frameA = fileA.ReadPooledFrame(ticketA).Unwrap();
        using var frameB = fileB.ReadPooledFrame(ticketB).Unwrap();
        if (DecodeTicket(frameA.PayloadAndMeta) != ticketB || DecodeTicket(frameB.PayloadAndMeta) != ticketA) {
            throw new InvalidDataException("Cold reopen did not preserve the mutual references.");
        }
    }

    Console.WriteLine("Two independent RBF files retained their mutual SizedPtr references after cold reopen.");
}
finally {
    string resolvedWorkDirectory = Path.GetFullPath(workDirectory);
    if (resolvedWorkDirectory.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedWorkDirectory)) {
        Directory.Delete(resolvedWorkDirectory, recursive: true);
    }
}

static byte[] EncodeTicket(SizedPtr ticket) {
    byte[] bytes = new byte[sizeof(long) + sizeof(int)];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, ticket.Offset);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(long)), ticket.Length);
    return bytes;
}

static SizedPtr DecodeTicket(ReadOnlySpan<byte> bytes) => SizedPtr.Create(
    BinaryPrimitives.ReadInt64LittleEndian(bytes),
    BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(long)..]));
