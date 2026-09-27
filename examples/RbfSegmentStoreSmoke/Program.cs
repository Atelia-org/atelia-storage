using Atelia.RbfSegmentStore;

string path = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a fresh segment store directory.");
uint segmentNumber;
Atelia.Data.SizedPtr ticket;
using (var store = RbfSegmentStore.CreateNew(path)) {
    using (var writer = store.OpenActiveWriter()) {
        segmentNumber = writer.SegmentNumber;
        ticket = writer.File.Append(7, "segment smoke"u8).Unwrap();
    }
    store.ConfirmDurable(segmentNumber);
    using var reader = store.OpenReader(segmentNumber);
    using var frame = reader.File.ReadPooledFrame(ticket).Unwrap();
    if (frame.Tag != 7 || !frame.PayloadAndMeta.SequenceEqual("segment smoke"u8)) {
        throw new InvalidDataException("Immediate segment read disagrees with append.");
    }
}
using (var reopened = RbfSegmentStore.OpenReadOnlyExisting(path)) {
    if (reopened.ActiveSegmentNumber != segmentNumber) {
        throw new InvalidDataException("Reopened active segment number changed.");
    }
    using var reader = reopened.OpenReader(segmentNumber);
    using var frame = reader.File.ReadPooledFrame(ticket).Unwrap();
    if (frame.Tag != 7 || !frame.PayloadAndMeta.SequenceEqual("segment smoke"u8)) {
        throw new InvalidDataException("Read-only reopen lost the segment frame.");
    }
}
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Atelia.")).OrderBy(a => a.GetName().Name)) {
    Console.WriteLine($"Loaded {assembly.GetName().Name}: {assembly.Location}");
}
Console.WriteLine("RbfSegmentStore public PackageReference smoke passed: create / append / ConfirmDurable / read / close / read-only reopen.");
