using Atelia.Data;
using Atelia.RbfSegmentStore;

string path = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a fresh segment store directory.");
var options = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8, HistoricalReaderPoolCapacity = 1 };
var addresses = new List<(uint SegmentNumber, SizedPtr Ticket)>();
uint active;
using (var store = RbfSegmentStore.CreateNew(path, options)) {
    for (int index = 0; index < 3; index++) {
        uint segmentNumber;
        using (var writer = store.OpenActiveWriter()) {
            segmentNumber = writer.SegmentNumber;
            addresses.Add((segmentNumber, writer.File.Append((uint)(7 + index), Body(index)).Unwrap()));
        }
        // Release the writer/reader leases before durable confirmation.
        store.ConfirmDurable(segmentNumber);
    }
    active = store.ActiveSegmentNumber;
    if (active != 3 || addresses.Select(a => a.SegmentNumber).Distinct().Count() != 3) {
        throw new InvalidDataException("Segment appends did not rotate into three distinct identities.");
    }
    Verify(store);
}
using (var reopened = RbfSegmentStore.OpenExisting(path, options)) {
    Verify(reopened);
}
using (var reopened = RbfSegmentStore.OpenReadOnlyExisting(path, options)) {
    Verify(reopened);
}
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Atelia.")).OrderBy(a => a.GetName().Name)) {
    Console.WriteLine($"Loaded {assembly.GetName().Name}: {assembly.Location}");
}
Console.WriteLine("RbfSegmentStore public PackageReference v2 smoke passed: create, three segment identities, rotation, ConfirmDurable, checked body, strict writable and read-only reopen.");

void Verify(RbfSegmentStore store) {
    if (store.ActiveSegmentNumber != active || store.Layout != RbfSegmentStoreLayout.Bucketed) {
        throw new InvalidDataException("Reopened locator changed active segment or layout identity.");
    }
    for (int index = 0; index < addresses.Count; index++) {
        var address = addresses[index];
        using var reader = store.OpenReader(address.SegmentNumber);
        using var frame = reader.File.ReadPooledFrame(address.Ticket).Unwrap();
        if (frame.Tag != (uint)(7 + index) || frame.IsTombstone || frame.TailMetaLength != 0 ||
            !frame.PayloadAndMeta.SequenceEqual(Body(index))) {
            throw new InvalidDataException("Segment checked body or frame identity changed across rotation/reopen.");
        }
        var boundary = reader.File.GetScanBoundaryAfter(address.Ticket).Unwrap();
        var suffix = reader.File.ScanForward(boundary, showTombstone: true).Unwrap().GetEnumerator();
        if (suffix.MoveNext() || suffix.TerminationError is not null || boundary.EndExclusive != reader.File.TailOffset) {
            throw new InvalidDataException("Checked scan boundary does not describe the persisted segment tail.");
        }
    }
}

static ReadOnlySpan<byte> Body(int index) => index switch {
    0 => "segment smoke zero"u8,
    1 => "segment smoke one"u8,
    2 => "segment smoke two"u8,
    _ => throw new ArgumentOutOfRangeException(nameof(index))
};
