using Atelia.FrameStore.Internal.Format;
using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Runtime;

internal sealed partial class FrameStoreCore {
    internal AteliaResult<long> Inventory(Action<FrameInfo> visitor, CancellationToken cancellationToken = default) {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(visitor);
        return Scan(visitor, null, cancellationToken);
    }

    internal AteliaResult<long> Audit(Action<FrameFileAudit> visitor, CancellationToken cancellationToken = default) {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(visitor);
        return Scan(null, visitor, cancellationToken);
    }

    private AteliaResult<long> Scan(Action<FrameInfo>? inventory, Action<FrameFileAudit>? audit,
        CancellationToken cancellationToken) {
        if (_scanActive) { throw new InvalidOperationException("The FrameStore owner is already running a physical scan."); }
        if (_outstandingBuilders != 0) { throw new InvalidOperationException("Physical scans require zero outstanding FrameStore builders."); }
        _scanActive = true;
        _handoffComplete = true;
        long total = 0;
        AteliaError? failedResult = null;
        CleanupErrors errors = default;
        try {
            ScanCheckpoint(cancellationToken);
            failedResult = _files.VisitScanFiles(
                (fileId, archived) => ScanFile(fileId, archived, inventory, audit, cancellationToken, ref total),
                () => ScanCheckpoint(cancellationToken), _markOwnedCleanupFault);
            if (failedResult is null) { ScanCheckpoint(cancellationToken); }
        }
        catch (Exception primary) { errors.Add(primary); }
        finally {
            try { CloseScanFile(ref errors); }
            finally { _scanActive = false; }
        }
        errors.ThrowIfAny();
        if (failedResult is not null) { return failedResult; }
        return total;
    }

    private AteliaError? ScanFile(uint fileId, bool archived, Action<FrameInfo>? inventory,
        Action<FrameFileAudit>? audit, CancellationToken cancellationToken, ref long total) {
        AteliaError? failedResult = null;
        byte[]? headerPayload = null;
        long fileCount = 0;
        CleanupErrors errors = default;
        try {
            ScanCheckpoint(cancellationToken);
            int index = FindInsertionIndex(fileId);
            IRbfFile file;
            if (!archived && index < _active.Count && _active[index].FileId == fileId) {
                file = _active[index].File!;
            }
            else {
                // No callback or validation can run between transfer and registration of this owned handle.
                file = _files.OpenScanFile(fileId, archived);
                _scanFile = file;
                ScanCheckpoint(cancellationToken);
            }
            failedResult = ScanFrames(file, fileId, inventory, audit is not null, cancellationToken,
                out fileCount, out headerPayload);
        }
        catch (Exception primary) { errors.Add(primary); }
        finally { CloseScanFile(ref errors); }
        errors.ThrowIfAny();
        if (failedResult is not null) { return failedResult; }
        // A file report requires both normal EOF and successful temporary-reader close.
        ScanCheckpoint(cancellationToken);
        total = checked(total + fileCount);
        if (audit is not null) {
            audit(new FrameFileAudit(fileId, headerPayload!, fileCount));
            ScanCheckpoint(cancellationToken);
        }
        return null;
    }

    private AteliaError? ScanFrames(IRbfFile file, uint fileId, Action<FrameInfo>? inventory, bool audit,
        CancellationToken cancellationToken, out long fileCount, out byte[]? headerPayload) {
        fileCount = 0;
        headerPayload = null;
        ScanCheckpoint(cancellationToken);
        var header = _files.CheckScanHeader(file, fileId);
        if (header.IsFailure) { return header.Error; }
        ScanCheckpoint(cancellationToken);
        var headerTicket = header.Value;
        if (audit) {
            // The checked fixed-size header cannot induce a payload-sized allocation before qualification.
            Span<byte> buffer = stackalloc byte[FileHeaderCodec.HeaderFrameLength];
            var read = file.ReadFrame(headerTicket, buffer);
            if (read.IsFailure) { return read.Error; }
            ScanCheckpoint(cancellationToken);
            headerPayload = read.Value.PayloadAndMeta.ToArray();
        }
        ScanCheckpoint(cancellationToken);
        var enumerator = file.ScanForward(showTombstone: true).GetEnumerator();
        while (true) {
            ScanCheckpoint(cancellationToken);
            if (!enumerator.MoveNext()) {
                // Every false includes the header-only/empty-user case; corruption is never normal EOF.
                if (enumerator.TerminationError is { } error) { return error; }
                ScanCheckpoint(cancellationToken);
                return null;
            }
            ScanCheckpoint(cancellationToken);
            var info = enumerator.Current;
            if (info.Ticket == headerTicket) { continue; }
            if (audit) {
                var read = file.ReadPooledFrame(info.Ticket);
                if (read.IsFailure) { return read.Error; }
                var frame = read.Value!;
                CleanupErrors errors = default;
                try { ScanCheckpoint(cancellationToken); }
                catch (Exception primary) { errors.Add(primary); }
                finally {
                    try { frame.Dispose(); }
                    catch (Exception cleanup) {
                        MarkFaulted();
                        errors.Add(cleanup);
                    }
                }
                errors.ThrowIfAny();
            }
            fileCount = checked(fileCount + 1);
            if (inventory is not null) {
                inventory(new FrameInfo(FrameAddress.Create(fileId, info.Ticket), info.Tag,
                    info.PayloadLength, info.TailMetaLength, info.IsTombstone));
                ScanCheckpoint(cancellationToken);
            }
        }
    }

    private void ScanCheckpoint(CancellationToken cancellationToken) {
        EnsureUsable();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void CloseScanFile(ref CleanupErrors errors) {
        var file = _scanFile;
        _scanFile = null;
        if (file is null) { return; }
        try { file.Dispose(); }
        catch (Exception cleanup) {
            MarkFaulted();
            errors.Add(cleanup);
        }
    }
}
