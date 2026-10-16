using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Tags;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    // Named operations own one short-lived bucket handle, separate from history's head slot.
    private IRbfFile? _tagOperationFile;

    /// <summary>按 Ordinal 名称 create-only 冻结一份完整 root payload，成功恒为 true。</summary>
    /// <remarks>匿名 tag 不分配 head 身份；必选 out 在任何 guard 之前初始化。</remarks>
    public AteliaResult<bool> CreateTag(string name, ReadOnlySpan<byte> payload, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        return CreateTagInput(name, payload, null, ref outcome);
    }

    /// <summary>冻结实际输入 bytes，并记录同库已签发 entryId 的可选关联注记。</summary>
    /// <remarks>关联只保存坐标；不读取、比较或 flush 来源，也不授予精确 clone 资格。</remarks>
    public AteliaResult<bool> CreateTag(string name, ReadOnlySpan<byte> payload, HeadEntryId relatedRevision,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        return CreateTagInput(name, payload, relatedRevision, ref outcome);
    }

    private AteliaResult<bool> CreateTagInput(string name, ReadOnlySpan<byte> payload, HeadEntryId? relatedRevision,
        scoped ref PublicationOutcome outcome) {
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }
        if (relatedRevision is HeadEntryId related
            && (!IsLocalEntryId(related) || related.Ticket.Length == 0)) { return InvalidEntryId(); }
        if (payload.Length is < 1 or > MaxHeadEntryPayloadLength) {
            return new HeadStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }

        // Prepare the lossless name and exact metadata width without allocating the final metadata.
        // The timestamp is installed once after definite preparation and before the data barrier.
        TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare(name, 0, _identity,
            relatedRevision?.HeadId, relatedRevision?.Ticket);
        byte[] captured = payload.ToArray();
        AteliaResult<bool> result = default;
        CleanupErrors errors = default;
        bool mutationStarted = false;
        try { result = CreateTagCore(name, captured, plan, ref outcome, ref mutationStarted); }
        catch (Exception error) {
            if (mutationStarted) { _faulted = true; }
            errors.Add(error);
        }
        finally {
            CloseTagOperation(ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        EnsureOwnerUsable();
        return result;
    }

    /// <summary>解析唯一可信名称并读取完整 checked root bytes；结果自有且不需要 Dispose。</summary>
    public AteliaResult<RootPayload> ResolveTag(string name) {
        EnsureOwnerUsable();
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }

        AteliaResult<RootPayload> result = default;
        CleanupErrors errors = default;
        try { result = ResolveTagCore(name); }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseTagOperation(ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        EnsureOwnerUsable();
        return result;
    }

    /// <summary>读取唯一可信名称的自有元信息；不读取 root 或核验关联来源。</summary>
    public AteliaResult<TagInfo> ReadTagInfo(string name) {
        EnsureOwnerUsable();
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }

        AteliaResult<TagInfo> result = default;
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

    private AteliaResult<bool> CreateTagCore(string name, byte[] captured, TagMetadataEncodingPlan plan,
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

        var recordSize = TagRecordCodec.MeasureAppend(captured.Length, plan.EncodedLength, recordsOffset);
        if (recordSize.IsFailure) { return recordSize.Error!; }
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var finalPlan = new TagMetadataEncodingPlan(new TagMetadata(name, recordedAt,
            plan.Metadata.RelatedRefId, plan.Metadata.RelatedTicket),
            plan.NamePlan, plan.EncodedLength);
        byte[] tailMeta = TagMetadataCodec.Encode(in finalPlan);
        Span<byte> headerPayload = stackalloc byte[TagBucketHeaderCodec.PayloadSize];
        if (newBucket && !TagBucketHeaderCodec.TryWrite(_identity, bucketId, headerPayload)) {
            throw new InvalidOperationException("Cannot encode the prepared tag BucketHeader.");
        }

        mutationStarted = true;
        if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
        else { _data.ConfirmDurable(); }
        EnsureOwnerUsable();
        if (newBucket) {
            files.EnsureTagsDirectory(_markOwnedCleanupFault);
            EnsureOwnerUsable();
            _pendingHead = _files.CreatePending(_markOwnedCleanupFault);
            EnsureOwnerUsable();
            _pendingHead.Append(TagRecordCodec.HeaderTag, headerPayload).Unwrap();
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
        var append = _tagOperationFile!.Append(TagRecordCodec.RecordTag, captured, tailMeta);
        if (append.IsFailure) {
            // A public RBF rejection proves that the sole TagRecord output attempt did no I/O.
            outcome = PublicationOutcome.NotAttempted;
            return append.Error!;
        }
        _testHooks?.AfterTagRecordAppended?.Invoke(append.Value);
        _tagOperationFile.DurableFlush();
        outcome = PublicationOutcome.Confirmed;
        _testHooks?.AfterPublicationConfirmed?.Invoke();
        return true;
    }

    private AteliaResult<RootPayload> ResolveTagCore(string name) {
        var opened = GetNamedTagBucket(name);
        if (opened.IsFailure) { return opened.Error!; }
        return TagRecordReader.ResolveNamed(opened.Value.File, opened.Value.Header, name, EnsureOwnerUsable);
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

    private static TagInfo CreateTagInfo(TagMetadata metadata) =>
        new(metadata.Name, metadata.RecordedAtUnixMs, metadata.RelatedRefId, metadata.RelatedTicket);

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
