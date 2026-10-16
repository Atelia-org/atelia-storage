using Atelia.Rbf;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Tags;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    private IRbfFile? _tagScanFile;
    private CheckedTagBucketHeader _tagScanHeader;
    private IEnumerator<QualifiedTagBucketTarget>? _tagScanTargets;
    private bool _tagScanActive;

    /// <summary>流式交付全部实际已建桶的自有元信息，不读取成员表或证明全库名称唯一。</summary>
    /// <remarks>所到坏元信息直接失败；visitor 的 false 接收当前项后正常停止，已交付值不回滚。</remarks>
    public AteliaResult<HeadSetTagScanEnd> ScanTags(Func<HeadSetTagInfo, bool> visitor,
        CancellationToken cancellationToken = default) {
        EnsureOwnerUsable();
        if (visitor is null) {
            return new HeadStoreRuntimeError("ArgumentError", "A nonnull tag visitor is required.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_tagScanActive) { return TagScanActive(); }

        _tagScanActive = true;
        try {
            void CheckState() {
                EnsureOwnerUsable();
                cancellationToken.ThrowIfCancellationRequested();
            }
            AteliaResult<HeadSetTagScanEnd> result = default;
            CleanupErrors errors = default;
            try { result = ScanTagsCore(visitor, CheckState); }
            catch (Exception error) { errors.Add(error); }
            finally {
                CloseTagScanBucket(ref errors);
                CloseTagScanTargets(ref errors);
                if (_faulted) { CleanupOwned(ref errors); }
            }
            errors.ThrowIfAny();
            CheckState();
            return result;
        }
        finally { _tagScanActive = false; }
    }

    private AteliaResult<HeadSetTagScanEnd> ScanTagsCore(Func<HeadSetTagInfo, bool> visitor, Action checkState) {
        checkState();
        _tagScanTargets = GetTagFiles().EnumerateTagBucketTargets(checkState, _markOwnedCleanupFault);
        checkState();
        while (true) {
            checkState();
            bool hasBucket = _tagScanTargets.MoveNext();
            checkState();
            if (!hasBucket) { return HeadSetTagScanEnd.Complete; }
            QualifiedTagBucketTarget target = _tagScanTargets.Current;
            checkState();
            _tagScanFile = GetTagFiles().OpenTagBucket(target, IsReadOnly, _markOwnedCleanupFault);
            checkState();
            var header = TagRecordReader.CheckHeader(_tagScanFile, _identity, target.BucketId, checkState);
            if (header.IsFailure) { return header.Error!; }
            _tagScanHeader = header.Value;
            var result = ScanTagBucket(_tagScanFile, _tagScanHeader, visitor, checkState);
            CloseTagScanBucket();
            checkState();
            if (result.IsFailure || result.Value == HeadSetTagScanEnd.VisitorStopped) { return result; }
        }
    }

    private static AteliaResult<HeadSetTagScanEnd> ScanTagBucket(IRbfFile file, CheckedTagBucketHeader header,
        Func<HeadSetTagInfo, bool> visitor, Action checkState) {
        checkState();
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        checkState();
        bool hasHeader = forward.MoveNext();
        checkState();
        if (!hasHeader) {
            return forward.TerminationError ?? new TagHeaderError("The checked BucketHeader is missing from the physical chain.");
        }
        if (forward.Current.Ticket != header.HeaderTicket || !TagRecordCodec.IsHeaderShape(forward.Current)) {
            return new TagHeaderError("The physical first frame does not match this handle's checked BucketHeader.");
        }

        while (true) {
            checkState();
            bool hasRecord = forward.MoveNext();
            checkState();
            if (!hasRecord) { return forward.TerminationError is { } error ? error : HeadSetTagScanEnd.Complete; }
            var metadata = TagRecordReader.ReadMetadata(forward.Current, header, checkState);
            if (metadata.IsFailure) { return metadata.Error!; }
            // ReadMetadata has returned its small pooled TailMeta before any application callback.
            HeadSetTagInfo info = CreateTagInfo(metadata.Value);
            checkState();
            bool keepGoing = visitor(info);
            checkState();
            if (!keepGoing) { return HeadSetTagScanEnd.VisitorStopped; }
        }
    }

    private IRbfFile? TakeTagScanBucket() {
        IRbfFile? toClose = _tagScanFile;
        _tagScanFile = null;
        _tagScanHeader = default;
        return toClose;
    }

    private void CloseTagScanBucket() {
        IRbfFile? toClose = TakeTagScanBucket();
        try { toClose?.Dispose(); }
        catch { _faulted = true; throw; }
    }

    private void CloseTagScanBucket(ref CleanupErrors errors) {
        IRbfFile? toClose = TakeTagScanBucket();
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private void CloseTagScanTargets(ref CleanupErrors errors) {
        IEnumerator<QualifiedTagBucketTarget>? toClose = _tagScanTargets;
        _tagScanTargets = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private static AteliaError TagScanActive() =>
        new HeadStoreRuntimeError("TagScanActive", "This operation is not allowed during an active tag visitor.");
}
