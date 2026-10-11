using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Tags;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    // Named operations own one short-lived bucket handle, separate from history's ref slot.
    private IRbfFile? _tagOperationFile;

    /// <summary>按 Ordinal 名称 create-only 冻结一份完整 root payload，成功恒为 true。</summary>
    /// <remarks>匿名 tag 不分配 ref 身份；必选 out 在任何 guard 之前初始化。</remarks>
    public AteliaResult<bool> CreateTag(string name, ReadOnlySpan<byte> payload, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (!TagNameCodec.IsValidName(name)) { return InvalidTagName(); }
        if (payload.Length is < 1 or > MaxRootPayloadLength) {
            return new VersionStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }

        // Prepare the lossless name and exact metadata width without allocating the final metadata.
        // The timestamp is installed once after definite preparation and before the data barrier.
        TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare(name, 0, _identity);
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
        EnsureUsable();
        return result;
    }

    /// <summary>解析唯一可信名称并读取完整 checked root bytes；结果自有且不需要 Dispose。</summary>
    public AteliaResult<RootPayload> ResolveTag(string name) {
        EnsureUsable();
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
        EnsureUsable();
        return result;
    }

    private AteliaResult<bool> CreateTagCore(string name, byte[] captured, TagMetadataEncodingPlan plan,
        scoped ref PublicationOutcome outcome, scoped ref bool mutationStarted) {
        IVersionStoreTagFiles files = GetTagFiles();
        uint bucketId = TagNameCodec.GetBucketId(name);
        var resolved = files.ResolveTagBucket(bucketId, _markOwnedCleanupFault);
        EnsureUsable();

        QualifiedTagBucketTarget target;
        bool newBucket = resolved is null;
        long recordsOffset;
        if (resolved is QualifiedTagBucketTarget existing) {
            target = existing;
            IRbfFile file = OpenTagOperation(target, readOnly: false);
            var header = TagRecordReader.CheckHeader(file, _identity, bucketId, EnsureUsable);
            if (header.IsFailure) { return header.Error!; }
            var lookup = TagRecordReader.ScanNamed(file, header.Value, name, EnsureUsable);
            if (lookup.IsFailure) { return lookup.Error!; }
            if (lookup.Value!.MatchCount != 0) {
                return new TagDuplicateNameError("The requested tag name is already occupied.");
            }
            if (lookup.Value.UnknownName) {
                return new TagLookupUncertainError("An unreadable record name prevents safe create-only publication.");
            }
            recordsOffset = file.TailOffset;
            EnsureUsable();
        }
        else {
            var prepared = files.PrepareNewTagBucket(bucketId, _markOwnedCleanupFault);
            EnsureUsable();
            if (prepared.IsFailure) { return prepared.Error!; }
            target = prepared.Value;
            var headerSize = RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize, 0);
            if (headerSize.IsFailure) { return headerSize.Error!; }
            long headerOffset = RbfScanBoundary.Empty.EndExclusive;
            recordsOffset = checked(headerOffset + (long)headerSize.Value.AppendLength);
            if (!SizedPtr.TryCreate(headerOffset, headerSize.Value.FrameLength, out _)) {
                return new VersionStoreRuntimeError("TagCapacityExceeded", "The new BucketHeader cannot represent a complete ticket.");
            }
        }

        var recordSize = TagRecordCodec.MeasureAppend(captured.Length, plan.EncodedLength, recordsOffset);
        if (recordSize.IsFailure) { return recordSize.Error!; }
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var finalPlan = new TagMetadataEncodingPlan(new TagMetadata(name, recordedAt, null, null),
            plan.NamePlan, plan.EncodedLength);
        byte[] tailMeta = TagMetadataCodec.Encode(in finalPlan);
        Span<byte> headerPayload = stackalloc byte[TagBucketHeaderCodec.PayloadSize];
        if (newBucket && !TagBucketHeaderCodec.TryWrite(_identity, bucketId, headerPayload)) {
            throw new InvalidOperationException("Cannot encode the prepared tag BucketHeader.");
        }

        mutationStarted = true;
        if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
        else { _data.ConfirmDurable(); }
        EnsureUsable();
        if (newBucket) {
            files.EnsureTagsDirectory(_markOwnedCleanupFault);
            EnsureUsable();
            _pendingRef = _files.CreatePending(_markOwnedCleanupFault);
            EnsureUsable();
            _pendingRef.Append(TagRecordCodec.HeaderTag, headerPayload).Unwrap();
            TagRecordReader.CheckHeader(_pendingRef, _identity, bucketId, EnsureUsable).Unwrap();
            if (_pendingRef.TailOffset != recordsOffset) {
                throw new InvalidOperationException("The private BucketHeader did not end at its prepared initialization boundary.");
            }
            _pendingRef.DurableFlush();
            EnsureUsable();
            IRbfFile toClose = _pendingRef;
            _pendingRef = null;
            toClose.Dispose();
            EnsureUsable();
            _testHooks?.BeforeNewTagBucketRename?.Invoke();
            EnsureUsable();
            files.PublishPendingTagBucket(target, _markOwnedCleanupFault);
            EnsureUsable();
            _testHooks?.AfterTagBucketPublished?.Invoke();
            EnsureUsable();

            // An empty bucket is infrastructure, not evidence that this tag was attempted.
            // Reopen through the normal public factory and independently check the formal header.
            IRbfFile file = OpenTagOperation(target, readOnly: false);
            TagRecordReader.CheckHeader(file, _identity, bucketId, EnsureUsable).Unwrap();
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
        _tagOperationFile.DurableFlush();
        outcome = PublicationOutcome.Confirmed;
        _testHooks?.AfterPublicationConfirmed?.Invoke();
        return true;
    }

    private AteliaResult<RootPayload> ResolveTagCore(string name) {
        uint bucketId = TagNameCodec.GetBucketId(name);
        var resolved = GetTagFiles().ResolveTagBucket(bucketId, _markOwnedCleanupFault);
        EnsureUsable();
        if (resolved is not QualifiedTagBucketTarget target) {
            return new TagNotFoundError("The requested tag bucket does not exist.");
        }
        IRbfFile file = OpenTagOperation(target, IsReadOnly);
        var header = TagRecordReader.CheckHeader(file, _identity, bucketId, EnsureUsable);
        if (header.IsFailure) { return header.Error!; }
        return TagRecordReader.ResolveNamed(file, header.Value, name, EnsureUsable);
    }

    private IVersionStoreTagFiles GetTagFiles() => _tagFiles
        ?? throw new InvalidOperationException("This internal VersionStore backend does not support tag files.");

    private IRbfFile OpenTagOperation(QualifiedTagBucketTarget target, bool readOnly) {
        if (_tagOperationFile is not null) { throw new InvalidOperationException("A named tag operation is already active."); }
        _tagOperationFile = GetTagFiles().OpenTagBucket(target, readOnly, _markOwnedCleanupFault);
        EnsureUsable();
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
