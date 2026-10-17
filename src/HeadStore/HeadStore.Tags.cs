using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Tags;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    // Named operations own one short-lived bucket handle, separate from history's head slot.
    private IRbfFile? _tagOperationFile;

    private bool _tagCreateActive;
    private IRbfFile? _tagDependencyFile;

    /// <summary>按 Ordinal 名称 create-only 捕获非空 Head 集合的当前已接受 Entry。</summary>
    /// <remarks>输入私有复制；结果不随 Head 移动，不持 owner 或 pooled lease。</remarks>
    public AteliaResult<HeadSetTag> CreateTag(string name, ReadOnlySpan<HeadId> heads,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }

        _tagCreateActive = true;
        try {
            if (heads.IsEmpty) {
                return new HeadStoreRuntimeError("ArgumentError", "A tag must select at least one Head.");
            }
            TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare(name, 0, _identity, heads.Length);
            var measured = TagRecordCodec.MeasurePayloadLength(heads.Length, plan.EncodedLength);
            if (measured.IsFailure) { return measured.Error!; }
            HeadId[] captured = heads.ToArray();
            foreach (HeadId head in captured) {
                if (!head.TryGetLocalId(_identity, out _)) { return InvalidHead(); }
            }
            Array.Sort(captured, (left, right) => left.GetLocalId(_identity).CompareTo(right.GetLocalId(_identity)));
            for (int index = 1; index < captured.Length; index++) {
                if (captured[index - 1] == captured[index]) {
                    return new HeadStoreRuntimeError("ArgumentError", "A tag cannot select the same Head twice.");
                }
            }

            AteliaResult<HeadSetTag> result = default;
            CleanupErrors errors = default;
            bool mutationStarted = false;
            try { result = CreateTagCore(name, captured, measured.Value, plan, ref outcome, ref mutationStarted); }
            catch (Exception error) {
                if (mutationStarted) { _faulted = true; }
                errors.Add(error);
            }
            finally {
                CloseTagDependency(ref errors);
                CloseTagOperation(ref errors);
                if (_faulted) { CleanupOwned(ref errors); }
            }
            errors.ThrowIfAny();
            EnsureOwnerUsable();
            return result;
        }
        finally { _tagCreateActive = false; }
    }

    /// <summary>解析唯一可信名称并恢复完整 checked 成员表；不访问成员文件。</summary>
    public AteliaResult<HeadSetTag> ReadTag(string name) {
        EnsureOwnerUsable();
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }

        AteliaResult<HeadSetTag> result = default;
        CleanupErrors errors = default;
        try { result = ReadTagCore(name); }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseTagOperation(ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        EnsureOwnerUsable();
        return result;
    }

    /// <summary>读取唯一可信名称的自有元信息；不读取成员表或成员文件。</summary>
    public AteliaResult<HeadSetTagInfo> ReadTagInfo(string name) {
        EnsureOwnerUsable();
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }

        AteliaResult<HeadSetTagInfo> result = default;
        CleanupErrors errors = default;
        try {
            var opened = GetNamedTagBucket(name);
            if (opened.IsFailure) { result = opened.Error!; }
            else {
                var metadata = TagRecordReader.ReadNamedMetadata(opened.Value.File, opened.Value.Header, name, EnsureOwnerUsable);
                result = metadata.IsFailure ? metadata.Error! : CreateTagInfo(metadata.Value);
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseTagOperation(ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        EnsureOwnerUsable();
        return result;
    }

    private AteliaResult<HeadSetTag> CreateTagCore(string name, HeadId[] captured, int payloadLength,
        TagMetadataEncodingPlan plan,
        scoped ref PublicationOutcome outcome, scoped ref bool mutationStarted) {
        IHeadStoreTagFiles files = GetTagFiles();
        uint bucketId = TagNameCodec.GetBucketId(name);
        var resolved = files.ResolveTagBucket(bucketId, _markOwnedCleanupFault);
        EnsureOwnerUsable();

        QualifiedTagBucketTarget target;
        bool newBucket = resolved is null;
        long recordsOffset;
        if (resolved is QualifiedTagBucketTarget existing) {
            target = existing;
            IRbfFile file = OpenTagOperation(target, readOnly: false);
            var header = TagRecordReader.CheckHeader(file, _identity, bucketId, EnsureOwnerUsable);
            if (header.IsFailure) { return header.Error!; }
            var lookup = TagRecordReader.ScanNamed(file, header.Value, name, EnsureOwnerUsable);
            if (lookup.IsFailure) { return lookup.Error!; }
            if (lookup.Value!.MatchCount != 0) {
                return new TagDuplicateNameError("The requested tag name is already occupied.");
            }
            if (lookup.Value.UnknownName) {
                return new TagLookupUncertainError("An unreadable record name prevents safe create-only publication.");
            }
            recordsOffset = file.TailOffset;
            EnsureOwnerUsable();
        }
        else {
            var prepared = files.PrepareNewTagBucket(bucketId, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            if (prepared.IsFailure) { return prepared.Error!; }
            target = prepared.Value;
            var headerSize = RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize, 0);
            if (headerSize.IsFailure) { return headerSize.Error!; }
            long headerOffset = RbfScanBoundary.Empty.EndExclusive;
            recordsOffset = checked(headerOffset + (long)headerSize.Value.AppendLength);
            if (!SizedPtr.TryCreate(headerOffset, headerSize.Value.FrameLength, out _)) {
                return new HeadStoreRuntimeError("TagCapacityExceeded", "The new BucketHeader cannot represent a complete ticket.");
            }
        }

        var recordSize = TagRecordCodec.MeasureAppend(payloadLength, plan.EncodedLength, recordsOffset);
        if (recordSize.IsFailure) { return recordSize.Error!; }
        var dependencies = new HashSet<uint>();
        var entries = new HeadEntryId[captured.Length];
        var ownedEntries = new Dictionary<HeadId, HeadEntryId>(captured.Length);
        AteliaError? BeforeHeadWork() { EnsureOwnerUsable(); return null; }
        for (int index = 0; index < captured.Length; index++) {
            HeadId head = captured[index];
            var opened = GetOrOpen(head.GetLocalId(_identity));
            if (opened.IsFailure) { return opened.Error!; }
            IRbfFile source = opened.Value!;
            var header = HeadRecordReader.CheckHeader(source, _identity, head,
                BeforeHeadWork, EnsureOwnerUsable);
            if (header.IsFailure) { return header.Error!; }
            dependencies.Add(head.GetLocalId(_identity));
            var current = ReadPublishedCurrent(source, header.Value, dependencies);
            if (current.IsFailure) { return current.Error!; }
            entries[index] = current.Value!.Id;
            ownedEntries.Add(head, entries[index]);
            _testHooks?.AfterTagHeadSelected?.Invoke(entries[index]);
            EnsureOwnerUsable();
        }
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EnsureOwnerUsable();
        var finalPlan = new TagMetadataEncodingPlan(new TagMetadata(name, recordedAt, entries.Length),
            plan.NamePlan, plan.EncodedLength);
        byte[] tailMeta = TagMetadataCodec.Encode(in finalPlan);
        byte[] payload = TagEntriesCodec.Encode(captured, entries, _identity);
        var result = new HeadSetTag(name, recordedAt, ownedEntries);
        uint[] dependencyIds = dependencies.ToArray();
        Array.Sort(dependencyIds);
        Span<byte> headerPayload = stackalloc byte[TagBucketHeaderCodec.PayloadSize];
        if (newBucket && !TagBucketHeaderCodec.TryWrite(_identity, bucketId, headerPayload)) {
            throw new InvalidOperationException("Cannot encode the prepared tag BucketHeader.");
        }

        mutationStarted = true;
        if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
        else { _data.ConfirmDurable(); }
        EnsureOwnerUsable();
        foreach (uint localId in dependencyIds) {
            var confirmed = ConfirmTagDependency(localId);
            if (confirmed.IsFailure) { return confirmed.Error!; }
        }
        _testHooks?.AfterTagDependenciesConfirmed?.Invoke();
        EnsureOwnerUsable();
        if (newBucket) {
            files.EnsureTagsDirectory(_markOwnedCleanupFault);
            EnsureOwnerUsable();
            _pendingHead = _files.CreatePending(_markOwnedCleanupFault);
            EnsureOwnerUsable();
            _pendingHead.Append(TagRecordCodec.HeaderTag, headerPayload).Unwrap();
            EnsureOwnerUsable();
            TagRecordReader.CheckHeader(_pendingHead, _identity, bucketId, EnsureOwnerUsable).Unwrap();
            if (_pendingHead.TailOffset != recordsOffset) {
                throw new InvalidOperationException("The private BucketHeader did not end at its prepared initialization boundary.");
            }
            _pendingHead.DurableFlush();
            EnsureOwnerUsable();
            IRbfFile toClose = _pendingHead;
            _pendingHead = null;
            toClose.Dispose();
            EnsureOwnerUsable();
            _testHooks?.BeforeNewTagBucketRename?.Invoke();
            EnsureOwnerUsable();
            files.PublishPendingTagBucket(target, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            _testHooks?.AfterTagBucketPublished?.Invoke();
            EnsureOwnerUsable();

            // An empty bucket is infrastructure, not evidence that this tag was attempted.
            // Reopen through the normal public factory and independently check the formal header.
            IRbfFile file = OpenTagOperation(target, readOnly: false);
            TagRecordReader.CheckHeader(file, _identity, bucketId, EnsureOwnerUsable).Unwrap();
            if (file.TailOffset != recordsOffset) {
                throw new InvalidOperationException("The published empty bucket does not end at its prepared initialization boundary.");
            }
        }

        outcome = PublicationOutcome.Unknown;
        var append = _tagOperationFile!.Append(TagRecordCodec.RecordTag, payload, tailMeta);
        if (append.IsFailure) {
            // A public RBF rejection proves that the sole TagRecord output attempt did no I/O.
            outcome = PublicationOutcome.NotAttempted;
            return append.Error!;
        }
        EnsureOwnerUsable();
        _testHooks?.AfterTagRecordAppended?.Invoke(append.Value);
        EnsureOwnerUsable();
        _tagOperationFile.DurableFlush();
        outcome = PublicationOutcome.Confirmed;
        EnsureOwnerUsable();
        _testHooks?.AfterPublicationConfirmed?.Invoke();
        EnsureOwnerUsable();
        return result;
    }

    private AteliaResult<HeadSetTag> ReadTagCore(string name) {
        var opened = GetNamedTagBucket(name);
        if (opened.IsFailure) { return opened.Error!; }
        return TagRecordReader.ReadNamed(opened.Value.File, opened.Value.Header, name, EnsureOwnerUsable);
    }

    private AteliaResult<(IRbfFile File, CheckedTagBucketHeader Header)> GetNamedTagBucket(string name) {
        uint bucketId = TagNameCodec.GetBucketId(name);
        // Every named call still qualifies all direct directory entries, including calls
        // from a scan visitor. Borrowing a content handle does not waive path qualification.
        var resolved = GetTagFiles().ResolveTagBucket(bucketId, _markOwnedCleanupFault);
        EnsureOwnerUsable();
        if (resolved is not QualifiedTagBucketTarget target) {
            return new TagNotFoundError("The requested tag bucket does not exist.");
        }
        // A scan visitor borrows the already qualified current bucket; the named finally must
        // neither replace its slot nor close this handle. Other buckets retain their own slot.
        if (_tagScanFile is { } scanFile && _tagScanHeader.IsFor(scanFile) && _tagScanHeader.BucketId == bucketId) {
            EnsureOwnerUsable();
            return (scanFile, _tagScanHeader);
        }
        IRbfFile file = OpenTagOperation(target, IsReadOnly);
        var header = TagRecordReader.CheckHeader(file, _identity, bucketId, EnsureOwnerUsable);
        if (header.IsFailure) { return header.Error!; }
        return (file, header.Value);
    }

    private static HeadSetTagInfo CreateTagInfo(TagMetadata metadata) =>
        new(metadata.Name, metadata.RecordedAtUnixMs, metadata.EntryCount);

    // Fork and tag share this dependency confirmation. Selected/source Heads may be
    // cached; CU peers are confirmed individually without retaining extra owned state.
    private AteliaResult<bool> ConfirmTagDependency(uint localId) {
        EnsureOwnerUsable();
        IRbfFile file;
        if (_ownedHeads.TryGetValue(localId, out OwnedHead? owned)) { file = owned.File!; }
        else {
            var resolved = _files.ResolveHead(localId, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            if (resolved.IsFailure) { return resolved.Error!; }
            _tagDependencyFile = _files.OpenHead(resolved.Value, readOnly: false, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            file = _tagDependencyFile;
            AteliaError? BeforeHeadWork() { EnsureOwnerUsable(); return null; }
            var header = HeadRecordReader.CheckHeader(file, _identity, HeadId.Create(_identity, localId),
                BeforeHeadWork, EnsureOwnerUsable);
            if (header.IsFailure) { return header.Error!; }
        }
        _testHooks?.BeforeTagDependencyFlush?.Invoke(localId);
        EnsureOwnerUsable();
        file.DurableFlush();
        EnsureOwnerUsable();
        CleanupErrors errors = default;
        CloseTagDependency(ref errors);
        errors.ThrowIfAny();
        EnsureOwnerUsable();
        return true;
    }

    private void CloseTagDependency(ref CleanupErrors errors) {
        IRbfFile? toClose = _tagDependencyFile;
        _tagDependencyFile = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private IHeadStoreTagFiles GetTagFiles() => _tagFiles
        ?? throw new InvalidOperationException("This internal HeadStore backend does not support tag files.");

    private IRbfFile OpenTagOperation(QualifiedTagBucketTarget target, bool readOnly) {
        if (_tagOperationFile is not null) { throw new InvalidOperationException("A named tag operation is already active."); }
        _tagOperationFile = GetTagFiles().OpenTagBucket(target, readOnly, _markOwnedCleanupFault);
        EnsureOwnerUsable();
        return _tagOperationFile;
    }

    private void CloseTagOperation(ref CleanupErrors errors) {
        IRbfFile? toClose = _tagOperationFile;
        _tagOperationFile = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private static AteliaError InvalidTagName() =>
        new TagNameError("A tag name must contain 1..1024 UTF-16 code units.");
}
