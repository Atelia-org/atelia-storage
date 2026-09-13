using Atelia.EventJournal;

string path = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a fresh journal directory.");
EventAddress root;
EventAddress child;
using (var journal = EventJournal.CreateNew(path)) {
    root = journal.AppendEventFrame(null, "root"u8).Unwrap();
    child = journal.AppendEventFrame(root, "child"u8).Unwrap();
    using EventFrame frame = journal.ReadEvent(child).Unwrap();
    if (!frame.Payload.SequenceEqual("child"u8) || frame.Header.Parent != root) {
        throw new InvalidDataException("Immediate checked read disagrees with append.");
    }
}
using (var reopened = EventJournal.OpenReadOnlyExisting(path)) {
    using EventFrame frame = reopened.ReadEvent(child).Unwrap();
    if (!frame.Payload.SequenceEqual("child"u8) || frame.Header.Parent != root ||
        reopened.ReadAncestorChain(child, checkedRead: true).Unwrap().Count != 2) {
        throw new InvalidDataException("Closed and reopened journal lost its payload or parent chain.");
    }
}
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Atelia.")).OrderBy(a => a.GetName().Name)) {
    Console.WriteLine($"Loaded {assembly.GetName().Name}: {assembly.Location}");
}
Console.WriteLine("EventJournal public PackageReference smoke passed: create / append / checked read / close / read-only reopen.");
