using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    /// <summary>在自动区创建只含 header 的 fork，固定引用已签发的准确来源 Entry。</summary>
    public AteliaResult<HeadId> ForkHead(HeadEntryId sourceEntryId, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (!IsLocalEntryId(sourceEntryId)) { return InvalidEntryId(); }
        if (_autoHeadIdHighWatermark >= MaxLocalHeadId) {
            return new HeadStoreRuntimeError("HeadIdExhausted", "The automatic head identifier domain is exhausted.");
        }
        return ForkNewHead(checked(_autoHeadIdHighWatermark + 1), sourceEntryId, automatic: true, ref outcome);
    }

    /// <summary>在空闲预留号 create-only 创建 fork；来源仍为已签发的 exact entryId。</summary>
    public AteliaResult<HeadId> ForkHead(uint destLocalHeadId, HeadEntryId sourceEntryId,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (!IsLocalEntryId(sourceEntryId)) { return InvalidEntryId(); }
        if (destLocalHeadId == 0 || destLocalHeadId > ReservedHeadIdUpperBound) {
            return new HeadStoreRuntimeError("ArgumentError", "A specified head identifier must belong to the reserved low-number domain.");
        }
        return ForkNewHead(destLocalHeadId, sourceEntryId, automatic: false, ref outcome);
    }

    private AteliaResult<HeadId> ForkNewHead(uint localId, HeadEntryId sourceEntryId,
        bool automatic, scoped ref PublicationOutcome outcome) {
        // Reject an occupied target before source access can begin writable recovery.
        var prepared = PrepareNewTarget(localId);
        if (prepared.IsFailure) { return prepared.Error!; }

        HeadId sourceHeadId = sourceEntryId.HeadId;
        var opened = GetOrOpen(sourceHeadId.GetLocalId(_identity));
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile sourceFile = opened.Value!;
        var header = HeadRecordReader.CheckHeader(sourceFile, _identity, sourceHeadId);
        if (header.IsFailure) { return header.Error!; }
        var dependencies = new HashSet<uint>();
        var read = ReadExactPublication(sourceFile, header.Value, sourceEntryId.Ticket, dependencies: dependencies);
        if (read.IsFailure) { return read.Error!; }
        HeadEntry? entry = read.Value.Entry;
        if (entry is null) { return InvalidHistoryOrigin("The exact source CU is not a published entry."); }

        var origin = new ForkOrigin(sourceHeadId, sourceEntryId.Ticket);
        return PublishNewHead(prepared.Value, [], automatic, origin, dependencies, ref outcome, out _);
    }

    private bool IsLocalEntryId(HeadEntryId entryId) =>
        entryId.HeadId.TryGetLocalId(_identity, out _) && entryId.Ticket.Packed != 0;

    private static AteliaError InvalidEntryId() =>
        new HeadStoreRuntimeError("HeadNotFound", "The entry owner cannot be located in this HeadStore.");
}
