using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Records;

/// <summary>共同首帧检查取得的局部资格；不拥有关联的 RBF 句柄。</summary>
internal readonly struct CheckedHeadHeader {
    private readonly IRbfFile? _file;

    internal HeadId HeadId { get; }
    internal SizedPtr HeaderTicket { get; }
    internal long InitialEntryOffset { get; }
    internal ForkOrigin? Origin { get; }

    internal CheckedHeadHeader(IRbfFile file, HeadHeader header, SizedPtr ticket, long initialEntryOffset) {
        _file = file;
        HeadId = header.HeadId;
        HeaderTicket = ticket;
        InitialEntryOffset = initialEntryOffset;
        Origin = header.Origin;
    }

    internal bool IsFor(IRbfFile file) => ReferenceEquals(_file, file);
}
