using Atelia.EventJournal;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

string path = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a fresh journal directory.");
// Every append exceeds this small valid threshold, forcing both event and ref rotations.
var options = new EventJournalOptions {
    EventSegmentStoreOptions = new() { SegmentSizeThresholdBytes = 8 },
    RefSegmentStoreOptions = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat, SegmentSizeThresholdBytes = 8 },
    RefStoreCacheCapacity = 1
};
EventAddress root;
EventAddress child;
EventAddress tip;
RefId main;
RefId fork;
using (var journal = EventJournal.CreateNew(path, options)) {
    root = journal.AppendEventFrame(null, "root"u8).Unwrap();
    main = journal.CreateBranch("saved", root).Unwrap();
    child = journal.CommitToRef(main, root, "child"u8).Unwrap().EventAddress;
    tip = journal.CommitToRef(main, child, "tip"u8).Unwrap().EventAddress;
    journal.CreateTag("saved", child).Unwrap();
    journal.CreateTag("tip", tip).Unwrap();
    fork = journal.ForkBranch("fork", main, tip).Unwrap();
    if (journal.ActiveSegmentNumber != tip.SegmentNumber || tip.SegmentNumber <= root.SegmentNumber ||
        journal.ResolveTag("saved").Unwrap() != child || journal.CreateTag("saved", child).IsSuccess) {
        throw new InvalidDataException("Event rotation or immutable tag binding disagrees with writes.");
    }
    Verify(journal, tip, expectedMainMoves: 3);
}
// OpenExisting now performs strict v2 tail checks, with no recovery option.
using (var reopened = EventJournal.OpenExisting(path, options)) {
    Verify(reopened, tip, expectedMainMoves: 3);
    reopened.MoveRef(main, tip, root).Unwrap();
    Verify(reopened, root, expectedMainMoves: 4);
}
using (var reopened = EventJournal.OpenReadOnlyExisting(path, options)) {
    Verify(reopened, root, expectedMainMoves: 4);
}
// Inspect the ref object's segments through the public lower-layer API after releasing the journal.
using (var refStore = SegmentStore.OpenReadOnlyExisting(Path.Combine(path, "refs", "objects", main.ToHexString()), options.RefSegmentStoreOptions)) {
    if (refStore.Layout != RbfSegmentStoreLayout.Flat || refStore.ActiveSegmentNumber <= 1) {
        throw new InvalidDataException("Ref appends did not rotate their flat segment store.");
    }
}
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Atelia.")).OrderBy(a => a.GetName().Name)) {
    Console.WriteLine($"Loaded {assembly.GetName().Name}: {assembly.Location}");
}
Console.WriteLine("EventJournal public PackageReference v2 smoke passed: branch/ref/tag identity, event/ref rotation, fork, rewind, body/Parent, strict writable and read-only reopen.");

void Verify(EventJournal journal, EventAddress mainHead, int expectedMainMoves) {
    if (journal.OpenBranch("saved").Unwrap() != main || journal.OpenBranch("fork").Unwrap() != fork ||
        journal.GetHead(main) != mainHead || journal.GetHead(fork) != tip ||
        journal.ResolveTag("saved").Unwrap() != child || journal.ResolveTag("tip").Unwrap() != tip) {
        throw new InvalidDataException("Reopened ref identity, selected head or tag binding changed.");
    }
    VerifyFrame(journal, root, null, "root"u8, 1);
    VerifyFrame(journal, child, root, "child"u8, 2);
    VerifyFrame(journal, tip, child, "tip"u8, 3);
    if (!journal.ReadChronologicalChain(tip, checkedRead: true).Unwrap().SequenceEqual(new[] { root, child, tip }) ||
        !journal.ReadChronologicalChain(fork, checkedRead: true).Unwrap().SequenceEqual(new[] { root, child, tip })) {
        throw new InvalidDataException("Full selected replay disagrees with authoritative Parent history.");
    }
    var moves = journal.ReadReflog(main).Unwrap();
    if (moves.Count != expectedMainMoves || moves.Any(move => move.RefId != main) ||
        moves[^1].MoveSequenceNumber != (ulong)expectedMainMoves || moves[^1].NewTarget != mainHead) {
        throw new InvalidDataException("Ref rotation lost its move identity, sequence or target.");
    }
}

static void VerifyFrame(EventJournal journal, EventAddress address, EventAddress? parent, ReadOnlySpan<byte> payload, ulong sequence) {
    using EventFrame frame = journal.ReadEvent(address).Unwrap();
    if (!frame.Payload.SequenceEqual(payload) || frame.Header.Parent != parent || frame.Header.SequenceNumber != sequence) {
        throw new InvalidDataException("Checked event body, Parent or sequence differs from its append identity.");
    }
}
