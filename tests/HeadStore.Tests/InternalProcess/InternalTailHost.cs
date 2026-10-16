using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Internal.Tags;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.InternalProcess;

// Internal + Process: the child remains inside AfterWrite with every owner alive.
// Its parent kills it; no byte edits or managed unwinding manufacture the tail.
internal static class InternalTailHost {
    internal const string TagName = "V4-F::首条-tag";
    internal const long RecordedAt = 1_800_000_000_123;
    internal const uint LocalHeadId = 1;

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
    private static string VersionRoot(string root) => Path.Combine(root, "heads");
    private static string HeadPath(string root) => Path.Combine(VersionRoot(root), HeadStorePaths.GetHeadRelativePath(LocalHeadId));
    private static string TagPath(string root) => Path.Combine(VersionRoot(root), "tags", TagNameCodec.GetBucketFileName(TagNameCodec.GetBucketId(TagName)));

    private static void Seed(string command, string root) {
        Directory.CreateDirectory(root);
        using var data = DataStore.Create(DataRoot(root));
        using var store = Store.Create(VersionRoot(root), data, reservedHeadIdUpperBound: LocalHeadId);
        var entryId = store.CreateHead(LocalHeadId, OldPayload(), out PublicationOutcome outcome).Unwrap();
        Require(outcome == PublicationOutcome.Confirmed, "Seed ref was not confirmed.");
        InternalProcessHost.Complete(command, new {
            headId = entryId.HeadId.LocalId, ticketOffset = entryId.Ticket.Offset,
            ticketLength = entryId.Ticket.Length, headLength = new FileInfo(HeadPath(root)).Length
        });
    }

    private static void Interrupt(string command, string root, string scenario, bool tag) {
        using var data = DataStore.Open(DataRoot(root));
        using var store = HeadStoreFactory.Open(VersionRoot(root), data, readOnly: false,
            testHooks: new HeadStoreTestHooks { GetRecordedAtUnixMs = () => RecordedAt });
        var source = store.ReadHead(LocalHeadId).Unwrap();
        byte[] payload = NewPayload();
        int metaLength = RecordMetadataCodec.EncodedSize;
        string target = HeadPath(root);
        long frameOffset = new FileInfo(target).Length;
        if (tag) {
            Require(StoreIdentity.TryRead(store.HeadStoreId, out StoreIdentity identity), "Missing VS identity.");
            metaLength = TagMetadataCodec.Prepare(TagName, RecordedAt, identity,
                1).EncodedLength;
            target = TagPath(root);
            Require(!File.Exists(target), "The tag bucket must be absent before the first tag request.");
            frameOffset = RbfScanBoundary.Empty.EndExclusive
                + RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize).Unwrap().AppendLength;
        }
        RbfWriteSize size = RbfFile.MeasureWriteSize(tag ? 12 : payload.Length, metaLength).Unwrap();
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
                    oldTicketOffset = source.Id.Ticket.Offset, oldTicketLength = source.Id.Ticket.Length,
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
            if (tag) { store.CreateTag(TagName, [source.Id.HeadId], out _).Unwrap(); }
            else { store.PublishHead(source.Id.HeadId, payload, out _).Unwrap(); }
            throw new InvalidOperationException("The writer returned without parking inside its short-write observation.");
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    private static void RejectReadOnly(string command, string root, string scenario, bool tag) {
        using var data = DataStore.OpenReadOnly(DataRoot(root));
        using var store = Store.OpenReadOnly(VersionRoot(root), data);
        Require(store.ListHeads().Unwrap().Single().LocalId == LocalHeadId, "Formal ref root discovery failed.");
        Exception? rejection = null;
        try {
            if (tag) { store.ReadTagInfo(TagName).Unwrap(); }
            else { store.ReadHead(LocalHeadId).Unwrap(); }
        }
        catch (InvalidDataException error) { rejection = error; }
        Require(rejection is not null, "Fresh RO must reject an incomplete physical tail.");
        InternalProcessHost.Complete(command, new {
            scenario, rejected = true, stage = tag ? "tag-content-open" : "ref-content-open",
            errorType = rejection!.GetType().Name, error = rejection.Message,
            physicalLength = new FileInfo(tag ? TagPath(root) : HeadPath(root)).Length
        });
    }

    private static void ReadState(string command, string root, string scenario, bool tag, bool readOnly) {
        using var data = readOnly ? DataStore.OpenReadOnly(DataRoot(root)) : DataStore.Open(DataRoot(root));
        var operations = new TailRecoveryOperations();
        using var store = HeadStoreFactory.Open(VersionRoot(root), data, readOnly,
            operations, testHooks: new HeadStoreTestHooks());
        Require(store.ListHeads().Unwrap().Single().LocalId == LocalHeadId, "Formal ref root discovery failed.");
        var entry = store.ReadHead(LocalHeadId).Unwrap();
        bool keepsNew = scenario.EndsWith("body-complete", StringComparison.Ordinal);
        Require(entry.Payload.SequenceEqual(!tag && keepsNew ? NewPayload() : OldPayload()),
            "The recovered ref root was neither the expected complete old nor complete new payload.");
        if (!tag && keepsNew) { Require(entry.RecordedAtUnixMs == RecordedAt, "Recovered ref metadata was not retained."); }
        var infoResult = tag ? store.ReadTagInfo(TagName) : default;
        object? tagInfo = null;
        bool metadataRechecked = false;
        if (tag && keepsNew) {
            HeadSetTagInfo info = infoResult.Unwrap();
            Require(info.Name == TagName && info.RecordedAtUnixMs == RecordedAt
                && info.EntryCount == 1,
                "CompletedTail tag metadata was not independently qualified by the VS reader.");
            long frameOffset = RbfScanBoundary.Empty.EndExclusive
                + RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize).Unwrap().AppendLength;
            Require(StoreIdentity.TryRead(store.HeadStoreId, out StoreIdentity identity), "Missing VS identity.");
            int metaLength = TagMetadataCodec.Prepare(TagName, RecordedAt, identity,
                1).EncodedLength;
            var firstTagOpen = operations.Opened.Single(open => SamePath(open.Path, TagPath(root)));
            metadataRechecked = firstTagOpen.Reads.Any(read => !read.Raw
                && read.Offset == frameOffset + sizeof(uint) + 12 && read.Length == metaLength);
            Require(metadataRechecked, "The recovered tag's actual small TailMeta was not read by ReadTagInfo.");
            Require(!firstTagOpen.Reads.Any(read => !read.Raw && read.Offset == frameOffset
                && read.Length == RbfFile.MeasureWriteSize(12, metaLength).Unwrap().FrameLength),
                "ReadTagInfo unexpectedly read the complete root frame.");
            tagInfo = new {
                name = info.Name, recordedAtUnixMs = info.RecordedAtUnixMs,
                entryCount = info.EntryCount
            };
            HeadSetTag recovered = store.ReadTag(TagName).Unwrap();
            Require(recovered.Entries.Count == 1 && recovered.Entries[entry.Id.HeadId] == entry.Id, "Recovered tag member identity differs.");
            Require(store.ReadEntry(recovered.Entries[entry.Id.HeadId]).Unwrap().Payload.SequenceEqual(OldPayload()), "Recovered tagged Entry bytes differ.");
        }
        else if (tag) {
            Require(infoResult.IsFailure && infoResult.Error is TagNotFoundError, "The truncated first tag must be absent.");
            var resolve = store.ReadTag(TagName);
            Require(resolve.IsFailure && resolve.Error is TagNotFoundError, "An empty bucket must not provide tag root bytes.");
        }
        var tagNames = new List<string>();
        Require(store.ScanTags(info => { tagNames.Add(info.Name); return true; }).Unwrap() == HeadSetTagScanEnd.Complete,
            "Tag scan did not reach normal EOF.");
        Require(tagNames.Count == (tag && keepsNew ? 1 : 0), "Recovered tag count differs.");
        object? audit = null;
        if (readOnly) {
            HeadStoreAudit checkedAudit = store.Audit().Unwrap();
            Require(checkedAudit.HeadCount == 1 && checkedAudit.EntryCount == (!tag && keepsNew ? 2 : 1)
                && checkedAudit.OriginCount == 0 && checkedAudit.TagBucketCount == (tag ? 1 : 0)
                && checkedAudit.TagCount == (tag && keepsNew ? 1 : 0), "Full RO Audit counts differ.");
            audit = new {
                headCount = checkedAudit.HeadCount, entryCount = checkedAudit.EntryCount,
                originCount = checkedAudit.OriginCount, tagBucketCount = checkedAudit.TagBucketCount,
                tagCount = checkedAudit.TagCount
            };
        }
        string target = tag ? TagPath(root) : HeadPath(root);
        InternalProcessHost.Complete(command, new {
            scenario, readOnly, headPayload = Convert.ToHexString(entry.Payload),
            headTicketOffset = entry.Id.Ticket.Offset, headTicketLength = entry.Id.Ticket.Length,
            recordedAtUnixMs = entry.RecordedAtUnixMs, tagFound = tag && keepsNew, tagInfo,
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

    private sealed class TailRecoveryOperations : HeadStoreFactoryOperations {
        internal List<TailOpen> Opened { get; } = [];

        internal override IRbfFile OpenHead(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
            IRbfFile file = base.OpenHead(path, readOnly, out recovery);
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
