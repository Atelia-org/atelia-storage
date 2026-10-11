using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Storage;
using Atelia.VersionStore.Internal.Tags;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.InternalProcess;

// Internal + Process: the child remains inside AfterWrite with every owner alive.
// Its parent kills it; no byte edits or managed unwinding manufacture the tail.
internal static class InternalTailHost {
    internal const string TagName = "V4-F::首条-tag";
    internal const long RecordedAt = 1_800_000_000_123;
    internal const uint LocalRefId = 1;

    internal static int Run(string[] args) {
        if (args.Length != 3) { throw new ArgumentException("Tail commands require command, root, and scenario."); }
        string command = args[0], root = Path.GetFullPath(args[1]), scenario = args[2];
        bool tag = IsTag(scenario);
        switch (command) {
            case "tail.seed": Seed(command, root); break;
            case "tail.interrupt": Interrupt(command, root, scenario, tag); break;
            case "tail.readonly-reject": RejectReadOnly(command, root, scenario, tag); break;
            case "tail.recover": ReadState(command, root, scenario, tag, readOnly: false); break;
            case "tail.verify": ReadState(command, root, scenario, tag, readOnly: true); break;
            default: throw new ArgumentException($"Unknown tail command '{command}'.");
        }
        return 0;
    }

    internal static byte[] OldPayload() => Enumerable.Range(0, 48).Select(index => (byte)(0x40 + index)).ToArray();

    internal static byte[] NewPayload() {
        byte[] bytes = Enumerable.Range(0, 64).Select(index => (byte)(0xa0 + index)).ToArray();
        // An aligned plaintext Fence forces the production writer's XOR escape path.
        RbfLayout.GetFence(RbfProfile.Rbf3).CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static bool IsTag(string scenario) => scenario switch {
        "ref-body-partial" or "ref-body-complete" => false,
        "tag-body-partial" or "tag-body-complete" => true,
        _ => throw new ArgumentException($"Unknown physical-tail scenario '{scenario}'.")
    };

    private static string DataRoot(string root) => Path.Combine(root, "data");
    private static string VersionRoot(string root) => Path.Combine(root, "versions");
    private static string RefPath(string root) => Path.Combine(VersionRoot(root), VersionStorePaths.GetRefRelativePath(LocalRefId));
    private static string TagPath(string root) => Path.Combine(VersionRoot(root), "tags", TagNameCodec.GetBucketFileName(TagNameCodec.GetBucketId(TagName)));

    private static void Seed(string command, string root) {
        Directory.CreateDirectory(root);
        using var data = DataStore.Create(DataRoot(root));
        using var store = Store.Create(VersionRoot(root), data, reservedRefIdUpperBound: LocalRefId);
        var revision = store.CreateRef(LocalRefId, OldPayload(), out PublicationOutcome outcome).Unwrap();
        Require(outcome == PublicationOutcome.Confirmed, "Seed ref was not confirmed.");
        InternalProcessHost.Complete(command, new {
            refId = revision.RefId.LocalId, ticketOffset = revision.Ticket.Offset,
            ticketLength = revision.Ticket.Length, refLength = new FileInfo(RefPath(root)).Length
        });
    }

    private static void Interrupt(string command, string root, string scenario, bool tag) {
        using var data = DataStore.Open(DataRoot(root));
        using var store = VersionStoreFactory.Open(VersionRoot(root), data, readOnly: false,
            testHooks: new VersionStoreTestHooks { GetRecordedAtUnixMs = () => RecordedAt });
        var source = store.ReadRef(LocalRefId).Unwrap();
        byte[] payload = NewPayload();
        int metaLength = RecordMetadataCodec.EncodedSize;
        string target = RefPath(root);
        long frameOffset = new FileInfo(target).Length;
        if (tag) {
            Require(StoreIdentity.TryRead(store.VersionStoreId, out StoreIdentity identity), "Missing VS identity.");
            metaLength = TagMetadataCodec.Prepare(TagName, RecordedAt, identity,
                source.Revision.RefId, source.Revision.Ticket).EncodedLength;
            target = TagPath(root);
            Require(!File.Exists(target), "The tag bucket must be absent before the first tag request.");
            frameOffset = RbfScanBoundary.Empty.EndExclusive
                + RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize).Unwrap().AppendLength;
        }
        RbfWriteSize size = RbfFile.MeasureWriteSize(payload.Length, metaLength).Unwrap();
        bool completeBody = scenario.EndsWith("body-complete", StringComparison.Ordinal);
        long cutLength = completeBody ? frameOffset + size.FrameLength - sizeof(uint) : frameOffset + sizeof(uint) + 7;
        long completeLength = frameOffset + size.AppendLength;
        var writes = new List<RbfWriteObservation>();
        int flushes = 0;
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            BeforeWrite = request => {
                if (!SamePath(request.Path, target)) { return request.RequestedBytes; }
                Require(request.Offset >= frameOffset && request.Offset < cutLength,
                    "Unexpected target-file output outside the single frame prefix.");
                return checked((int)Math.Min(request.RequestedBytes, cutLength - request.Offset));
            },
            AfterWrite = observation => {
                if (!SamePath(observation.Path, target)) { return; }
                writes.Add(observation);
                long actualLength = new FileInfo(target).Length;
                Require(actualLength == observation.Offset + observation.WrittenBytes,
                    "Real write's physical length did not match its observation.");
                if (actualLength != cutLength) { return; }
                Require(observation.WrittenBytes < observation.RequestedBytes, "Stop must be after a real short write.");
                Require(flushes == 0, "The interrupted record crossed a durable flush.");
                InternalProcessHost.Park(command, new {
                    scenario, path = target, frameOffset, frameLength = size.FrameLength,
                    cutLength, actualLength, completeLength, metadataLength = metaLength,
                    oldTicketOffset = source.Revision.Ticket.Offset, oldTicketLength = source.Revision.Ticket.Length,
                    expectedRecovery = completeBody ? "CompletedTail" : "Truncated",
                    ownersAlive = true, appendReturned = false, targetFlushes = flushes,
                    writes = writes.Select(write => new {
                        path = write.Path, offset = write.Offset,
                        requestedBytes = write.RequestedBytes, writtenBytes = write.WrittenBytes
                    }).ToArray()
                });
            },
            BeforeFlush = request => { if (SamePath(request.Path, target)) { flushes++; } }
        };
        try {
            if (tag) { store.CreateTag(TagName, payload, source.Revision, out _).Unwrap(); }
            else { store.PublishRef(source.Revision.RefId, payload, out _).Unwrap(); }
            throw new InvalidOperationException("The writer returned without parking inside its short-write observation.");
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    private static void RejectReadOnly(string command, string root, string scenario, bool tag) {
        using var data = DataStore.OpenReadOnly(DataRoot(root));
        using var store = Store.OpenReadOnly(VersionRoot(root), data);
        Require(store.ListRefs().Unwrap().Single().LocalId == LocalRefId, "Formal ref root discovery failed.");
        Exception? rejection = null;
        try {
            if (tag) { store.ReadTagInfo(TagName).Unwrap(); }
            else { store.ReadRef(LocalRefId).Unwrap(); }
        }
        catch (InvalidDataException error) { rejection = error; }
        Require(rejection is not null, "Fresh RO must reject an incomplete physical tail.");
        InternalProcessHost.Complete(command, new {
            scenario, rejected = true, stage = tag ? "tag-content-open" : "ref-content-open",
            errorType = rejection!.GetType().Name, error = rejection.Message,
            physicalLength = new FileInfo(tag ? TagPath(root) : RefPath(root)).Length
        });
    }

    private static void ReadState(string command, string root, string scenario, bool tag, bool readOnly) {
        using var data = readOnly ? DataStore.OpenReadOnly(DataRoot(root)) : DataStore.Open(DataRoot(root));
        var operations = new TailRecoveryOperations();
        using var store = VersionStoreFactory.Open(VersionRoot(root), data, readOnly,
            operations, testHooks: new VersionStoreTestHooks());
        Require(store.ListRefs().Unwrap().Single().LocalId == LocalRefId, "Formal ref root discovery failed.");
        var snapshot = store.ReadRef(LocalRefId).Unwrap();
        bool keepsNew = scenario.EndsWith("body-complete", StringComparison.Ordinal);
        Require(snapshot.Payload.SequenceEqual(!tag && keepsNew ? NewPayload() : OldPayload()),
            "The recovered ref root was neither the expected complete old nor complete new payload.");
        if (!tag && keepsNew) { Require(snapshot.RecordedAtUnixMs == RecordedAt, "Recovered ref metadata was not retained."); }
        var infoResult = tag ? store.ReadTagInfo(TagName) : default;
        object? tagInfo = null;
        bool metadataRechecked = false;
        if (tag && keepsNew) {
            TagInfo info = infoResult.Unwrap();
            Require(info.Name == TagName && info.RecordedAtUnixMs == RecordedAt
                && info.RelatedRefId == snapshot.Revision.RefId && info.RelatedTicket == snapshot.Revision.Ticket,
                "CompletedTail tag metadata was not independently qualified by the VS reader.");
            long frameOffset = RbfScanBoundary.Empty.EndExclusive
                + RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize).Unwrap().AppendLength;
            Require(StoreIdentity.TryRead(store.VersionStoreId, out StoreIdentity identity), "Missing VS identity.");
            int metaLength = TagMetadataCodec.Prepare(TagName, RecordedAt, identity,
                snapshot.Revision.RefId, snapshot.Revision.Ticket).EncodedLength;
            var firstTagOpen = operations.Opened.Single(open => SamePath(open.Path, TagPath(root)));
            metadataRechecked = firstTagOpen.Reads.Any(read => !read.Raw
                && read.Offset == frameOffset + sizeof(uint) + NewPayload().Length && read.Length == metaLength);
            Require(metadataRechecked, "The recovered tag's actual small TailMeta was not read by ReadTagInfo.");
            Require(!firstTagOpen.Reads.Any(read => !read.Raw && read.Offset == frameOffset
                && read.Length == RbfFile.MeasureWriteSize(NewPayload().Length, metaLength).Unwrap().FrameLength),
                "ReadTagInfo unexpectedly read the complete root frame.");
            tagInfo = new {
                name = info.Name, recordedAtUnixMs = info.RecordedAtUnixMs,
                relatedRefId = info.RelatedRefId?.LocalId,
                relatedTicketOffset = info.RelatedTicket?.Offset, relatedTicketLength = info.RelatedTicket?.Length
            };
            Require(store.ResolveTag(TagName).Unwrap().Bytes.SequenceEqual(NewPayload()), "Recovered tag root bytes differ.");
        }
        else if (tag) {
            Require(infoResult.IsFailure && infoResult.Error is TagNotFoundError, "The truncated first tag must be absent.");
            var resolve = store.ResolveTag(TagName);
            Require(resolve.IsFailure && resolve.Error is TagNotFoundError, "An empty bucket must not provide tag root bytes.");
        }
        var tagNames = new List<string>();
        Require(store.ScanTags(info => { tagNames.Add(info.Name); return true; }).Unwrap() == TagScanEnd.Complete,
            "Tag scan did not reach normal EOF.");
        Require(tagNames.Count == (tag && keepsNew ? 1 : 0), "Recovered tag count differs.");
        object? audit = null;
        if (readOnly) {
            VersionStoreAudit checkedAudit = store.Audit().Unwrap();
            Require(checkedAudit.RefCount == 1 && checkedAudit.SnapshotCount == (!tag && keepsNew ? 2 : 1)
                && checkedAudit.ForkCount == 0 && checkedAudit.TagBucketCount == (tag ? 1 : 0)
                && checkedAudit.TagCount == (tag && keepsNew ? 1 : 0), "Full RO Audit counts differ.");
            audit = new {
                refCount = checkedAudit.RefCount, snapshotCount = checkedAudit.SnapshotCount,
                forkCount = checkedAudit.ForkCount, tagBucketCount = checkedAudit.TagBucketCount,
                tagCount = checkedAudit.TagCount
            };
        }
        string target = tag ? TagPath(root) : RefPath(root);
        InternalProcessHost.Complete(command, new {
            scenario, readOnly, refPayload = Convert.ToHexString(snapshot.Payload),
            refTicketOffset = snapshot.Revision.Ticket.Offset, refTicketLength = snapshot.Revision.Ticket.Length,
            recordedAtUnixMs = snapshot.RecordedAtUnixMs, tagFound = tag && keepsNew, tagInfo,
            metadataRechecked, tagNames, audit, physicalLength = new FileInfo(target).Length,
            dataRecoveryActions = data.RecoveryReports.Values.Select(report => report.Action.ToString()).ToArray(),
            reports = operations.Opened.Select(open => new {
                path = open.Path, readOnly = open.ReadOnly, reportAvailable = !open.ReadOnly,
                action = open.ReadOnly ? null : open.Report.Action.ToString(),
                originalLength = open.ReadOnly ? (long?)null : open.Report.OriginalLength,
                finalLength = open.ReadOnly ? (long?)null : open.Report.FinalLength,
                affectedFrameOffset = open.ReadOnly ? null : open.Report.AffectedFrameOffset,
                ticketOffset = open.ReadOnly ? null : open.Report.FrameTicket?.Offset,
                ticketLength = open.ReadOnly ? null : open.Report.FrameTicket?.Length,
                reads = open.Reads.Select(read => new { offset = read.Offset, length = read.Length, raw = read.Raw }).ToArray()
            }).ToArray()
        });
    }

    private static bool SamePath(string? left, string right) => left is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class TailRecoveryOperations : VersionStoreFactoryOperations {
        internal List<TailOpen> Opened { get; } = [];

        internal override IRbfFile OpenRef(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
            IRbfFile file = base.OpenRef(path, readOnly, out recovery);
            Observe(path, readOnly, recovery, file);
            return file;
        }

        internal override IRbfFile OpenTagBucket(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
            IRbfFile file = base.OpenTagBucket(path, readOnly, out recovery);
            Observe(path, readOnly, recovery, file);
            return file;
        }

        private void Observe(string path, bool readOnly, RbfTailRecoveryReport recovery, IRbfFile file) {
            var observed = new TailOpen(path, readOnly, recovery);
            Opened.Add(observed);
            ((RbfFileImpl)file).ReadObserver = (offset, length, raw) => observed.Reads.Add(new(offset, length, raw));
        }
    }

    private sealed record TailOpen(string Path, bool ReadOnly, RbfTailRecoveryReport Report) {
        internal List<TailRead> Reads { get; } = [];
    }
    private readonly record struct TailRead(long Offset, int Length, bool Raw);
}
