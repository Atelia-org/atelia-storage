using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Records;

/// <summary>共同首帧检查取得的局部资格；不拥有关联的 RBF 句柄。</summary>
internal readonly struct CheckedHeadHeader {
    private readonly IRbfFile? _file;

    internal HeadId HeadId { get; }
    internal SizedPtr HeaderTicket { get; }
    internal long RecordsOffset { get; }
    internal ForkOrigin? Origin { get; }

    internal CheckedHeadHeader(IRbfFile file, HeadHeader header, SizedPtr ticket, long recordsOffset) {
        _file = file;
        HeadId = header.HeadId;
        HeaderTicket = ticket;
        RecordsOffset = recordsOffset;
        Origin = header.Origin;
    }

    internal bool IsFor(IRbfFile file) => ReferenceEquals(_file, file);

    // A root starts with a real SingleEntry; a fork's first local publication may
    // be either kind, and the header alone is a complete initialized fork.
    internal bool IsAllowedPublicationKind(long offset, uint tag) =>
        offset >= RecordsOffset
        && tag is HeadRecordCodec.SingleEntryTag or HeadRecordCodec.ConditionalUpdateTag
        && (offset != RecordsOffset || Origin.HasValue || tag == HeadRecordCodec.SingleEntryTag);

    internal bool IsAllowedPublication(RbfFrameInfo info) =>
        IsAllowedPublicationKind(info.Ticket.Offset, info.Tag) && HeadRecordCodec.IsPublicationShape(info);
}
