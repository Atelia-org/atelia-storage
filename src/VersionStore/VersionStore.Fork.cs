using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    /// <summary>在自动区创建 fork，完整复制已签发 exact 源 revision 的 root payload。</summary>
    public AteliaResult<RefRevision> ForkRef(RefRevision sourceRevision, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (!IsLocalRevision(sourceRevision)) { return InvalidRevision(); }
        if (_autoRefIdHighWatermark == uint.MaxValue) {
            return new VersionStoreRuntimeError("RefIdExhausted", "The automatic ref identifier domain is exhausted.");
        }
        return ForkNewRef(checked(_autoRefIdHighWatermark + 1), sourceRevision, automatic: true, ref outcome);
    }

    /// <summary>在空闲预留号 create-only 创建 fork；来源仍为已签发的 exact revision。</summary>
    public AteliaResult<RefRevision> ForkRef(uint destLocalRefId, RefRevision sourceRevision,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (!IsLocalRevision(sourceRevision)) { return InvalidRevision(); }
        if (destLocalRefId == 0 || destLocalRefId > ReservedRefIdUpperBound) {
            return new VersionStoreRuntimeError("ArgumentError", "A specified ref identifier must belong to the reserved low-number domain.");
        }
        return ForkNewRef(destLocalRefId, sourceRevision, automatic: false, ref outcome);
    }

    private AteliaResult<RefRevision> ForkNewRef(uint localId, RefRevision sourceRevision,
        bool automatic, scoped ref PublicationOutcome outcome) {
        // Reject an occupied target before source access can begin writable recovery.
        var prepared = PrepareNewTarget(localId);
        if (prepared.IsFailure) { return prepared.Error!; }

        RefId sourceRefId = sourceRevision.RefId;
        var opened = GetOrOpen(sourceRefId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile sourceFile = opened.Value!;
        var header = RefRecordReader.CheckHeader(sourceFile, _identity, sourceRefId);
        if (header.IsFailure) { return header.Error!; }
        var read = ReadExactPublication(sourceFile, header.Value, sourceRevision.Ticket);
        if (read.IsFailure) { return read.Error!; }
        RefBytesSnapshot? snapshot = read.Value.Snapshot;
        if (snapshot is null) { return InvalidHistoryOrigin("The exact source CU is not a published snapshot."); }

        var origin = new ForkOrigin(sourceRefId, sourceRevision.Ticket);
        return PublishNewRef(prepared.Value, snapshot.Payload, automatic, origin, sourceFile, ref outcome);
    }

    private bool IsLocalRevision(RefRevision revision) =>
        revision.RefId.BelongsTo(_identity) && revision.Ticket.Packed != 0;

    private static AteliaError InvalidRevision() =>
        new VersionStoreRuntimeError("ArgumentError", "A nondefault published RefRevision bound to this VersionStore is required.");
}
