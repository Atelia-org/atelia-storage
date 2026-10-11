using System.Collections;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Platform;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Tags;

namespace Atelia.VersionStore.Internal.Storage;

internal sealed partial class DirectoryVersionStoreFiles {
    public IEnumerator<QualifiedTagBucketTarget> EnumerateTagBucketTargets(Action checkState,
        Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(checkState);
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        IEnumerator<string>? entries = null;
        CleanupErrors errors = default;
        try {
            checkState();
            bool exists = VersionStorePlatform.TryRequireDirectory(TagsPath, markOwnedCleanupFault);
            checkState();
            if (exists) {
                VersionStorePlatform.RequireSameFileSystem(_root, TagsPath, markOwnedCleanupFault);
                checkState();
                VersionStorePlatform.RequireEnumeratedDirectory(TagsPath, markOwnedCleanupFault);
                checkState();
                entries = _operations.EnumerateDirectory(TagsPath);
                checkState();
            }
            var output = new TagBucketEnumerator(this, entries, checkState, markOwnedCleanupFault);
            entries = null;
            return output;
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref entries, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
        throw new InvalidOperationException("Unreachable tag enumeration handoff.");
    }

    public QualifiedTagBucketTarget? ResolveTagBucket(uint bucketId, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bucketId, (uint)TagNameCodec.BucketCount);
        if (!VersionStorePlatform.TryRequireDirectory(TagsPath, markOwnedCleanupFault)) {
            return null;
        }
        VersionStorePlatform.RequireSameFileSystem(_root, TagsPath, markOwnedCleanupFault);
        QualifiedTagBucketTarget? target = null;
        EnumerateDirect(TagsPath, _operations, path => {
            string name = Path.GetFileName(path);
            if (!VersionStorePaths.TryParseTagBucketFileName(name, out uint foundBucket)) { throw InvalidEntry(name); }
            VersionStorePlatform.RequireEnumeratedFile(path, markOwnedCleanupFault);
            VersionStorePlatform.RequireFileOnFileSystem(_root, path, markOwnedCleanupFault);
            if (foundBucket == bucketId) { target = new QualifiedTagBucketTarget(foundBucket, path); }
        }, markOwnedCleanupFault);
        return target;
    }

    public IRbfFile OpenTagBucket(QualifiedTagBucketTarget target, bool readOnly, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(target.BucketId, (uint)TagNameCodec.BucketCount);
        VersionStorePlatform.RequireDirectory(TagsPath, markOwnedCleanupFault);
        VersionStorePlatform.RequireSameFileSystem(_root, TagsPath, markOwnedCleanupFault);
        VersionStorePlatform.RequireEnumeratedFile(target.Path, markOwnedCleanupFault);
        VersionStorePlatform.RequireFileOnFileSystem(_root, target.Path, markOwnedCleanupFault);
        long initializedLength = RbfScanBoundary.Empty.EndExclusive
            + RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize, 0).Unwrap().AppendLength;
        if (!readOnly) { CheckTagBucketLength(target.Path, initializedLength, markOwnedCleanupFault); }
        if (!readOnly) { _recoveryLog.PrepareAppend(); }

        IRbfFile? file = null;
        CleanupErrors errors = default;
        try {
            try {
                file = _operations.OpenTagBucket(target.Path, readOnly, out RbfTailRecoveryReport recovery);
                if (!readOnly) { _recoveryLog.Record(VersionStoreRecoveryFileKind.TagBucket, target.BucketId, recovery); }
                if (!readOnly && recovery.Action != RbfTailRecoveryAction.None
                    && (recovery.AffectedFrameOffset is not { } affected || affected < initializedLength)) {
                    throw new InvalidDataException("Tag bucket recovery must not alter its initialization prefix.");
                }
            }
            catch {
                // Preflight above cannot mutate the bucket; a writable public factory can.
                if (!readOnly) { markOwnedCleanupFault(); }
                throw;
            }
            IRbfFile output = file;
            file = null;
            return output;
        }
        catch (Exception error) { errors.Add(error); }
        CloseOwned(ref file, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
        throw new InvalidOperationException("Unreachable tag bucket handoff.");
    }

    public void EnsureTagsDirectory(Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        if (!VersionStorePlatform.TryRequireDirectory(TagsPath, markOwnedCleanupFault)) {
            _operations.CreateTagsDirectory(TagsPath, markOwnedCleanupFault);
        }
        VersionStorePlatform.RequireDirectory(TagsPath, markOwnedCleanupFault);
        VersionStorePlatform.RequireSameFileSystem(_root, TagsPath, markOwnedCleanupFault);
    }

    public AteliaResult<QualifiedTagBucketTarget> PrepareNewTagBucket(uint bucketId, Action markOwnedCleanupFault) {
        if (ResolveTagBucket(bucketId, markOwnedCleanupFault) is not null) {
            return new VersionStoreRuntimeError("TagBucketAlreadyExists", "The formal tag bucket already exists.");
        }
        return new QualifiedTagBucketTarget(bucketId, Path.Combine(TagsPath, TagNameCodec.GetBucketFileName(bucketId)));
    }

    public void PublishPendingTagBucket(QualifiedTagBucketTarget target, Action markOwnedCleanupFault) {
        ArgumentNullException.ThrowIfNull(markOwnedCleanupFault);
        _operations.PublishPendingTagBucket(PendingPath, target.Path, markOwnedCleanupFault);
    }

    private string TagsPath => Path.Combine(_root, VersionStorePaths.TagsDirectoryName);

    private void CheckTagBucketLength(string path, long initializedLength, Action markOwnedCleanupFault) {
        FileStream? probe = null;
        CleanupErrors errors = default;
        try {
            probe = _operations.ProbeTagBucket(path);
            if (probe.Length < initializedLength) {
                throw new InvalidDataException("A formal tag bucket must contain its entire initialized header before writable recovery.");
            }
        }
        catch (Exception error) { errors.Add(error); }
        // Dispose must succeed before the independent exclusive RBF factory is entered.
        CloseOwned(ref probe, ref errors, markOwnedCleanupFault);
        errors.ThrowIfAny();
    }

    // The owner holds this cursor in an independent slot, so callback Dispose also releases it
    // before the root lock. No iterator finally can replace a MoveNext error with a close error.
    private sealed class TagBucketEnumerator(DirectoryVersionStoreFiles owner, IEnumerator<string>? entries,
        Action checkState, Action markOwnedCleanupFault) : IEnumerator<QualifiedTagBucketTarget> {
        private IEnumerator<string>? _entries = entries;
        private QualifiedTagBucketTarget _current;
        private bool _hasCurrent;

        public QualifiedTagBucketTarget Current => _hasCurrent
            ? _current
            : throw new InvalidOperationException("The tag bucket cursor has no current target.");

        object IEnumerator.Current => Current;

        public bool MoveNext() {
            checkState();
            _hasCurrent = false;
            if (_entries is null) { return false; }
            bool hasEntry = _entries.MoveNext();
            checkState();
            if (!hasEntry) { return false; }
            string path = _entries.Current;
            checkState();
            string name = Path.GetFileName(path);
            if (!VersionStorePaths.TryParseTagBucketFileName(name, out uint bucketId)) { throw InvalidEntry(name); }
            VersionStorePlatform.RequireEnumeratedFile(path, markOwnedCleanupFault);
            checkState();
            VersionStorePlatform.RequireFileOnFileSystem(owner._root, path, markOwnedCleanupFault);
            checkState();
            _current = new QualifiedTagBucketTarget(bucketId, path);
            _hasCurrent = true;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() {
            IEnumerator<string>? toClose = _entries;
            _entries = null;
            _hasCurrent = false;
            try { toClose?.Dispose(); }
            catch { markOwnedCleanupFault(); throw; }
        }
    }
}
