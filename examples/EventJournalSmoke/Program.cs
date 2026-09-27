using Atelia.EventJournal;

string path = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a fresh journal directory.");
EventAddress root;
EventAddress child;
using (var journal = EventJournal.CreateNew(path)) {
    root = journal.AppendEventFrame(null, "root"u8).Unwrap();
    child = journal.AppendEventFrame(root, "child"u8).Unwrap();
    journal.CreateTag("saved", child).Unwrap();
    RefId branch = journal.CreateBranch("saved", child).Unwrap();
    journal.MoveRef(branch, child, root).Unwrap();
    if (journal.ResolveTag("saved").Unwrap() != child || journal.CreateTag("saved", child).IsSuccess) {
        throw new InvalidDataException("Tag binding was mutable or duplicate creation succeeded.");
    }
    using EventFrame frame = journal.ReadEvent(child).Unwrap();
    if (!frame.Payload.SequenceEqual("child"u8) || frame.Header.Parent != root) {
        throw new InvalidDataException("Immediate checked read disagrees with append.");
    }
}
using (var reopened = EventJournal.OpenReadOnlyExisting(path)) {
    EventAddress resolved = reopened.ResolveTag("saved").Unwrap();
    if (resolved != child) { throw new InvalidDataException("Reopened tag lost its binding."); }
    using EventFrame frame = reopened.ReadEvent(resolved).Unwrap();
    if (!frame.Payload.SequenceEqual("child"u8) || frame.Header.Parent != root ||
        reopened.ReadAncestorChain(child, checkedRead: true).Unwrap().Count != 2) {
        throw new InvalidDataException("Closed and reopened journal lost its payload or parent chain.");
    }
}
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Atelia.")).OrderBy(a => a.GetName().Name)) {
    Console.WriteLine($"Loaded {assembly.GetName().Name}: {assembly.Location}");
}
Console.WriteLine("EventJournal public PackageReference smoke passed: create / append / immutable tag / branch move / checked read / close / read-only reopen / resolve.");
