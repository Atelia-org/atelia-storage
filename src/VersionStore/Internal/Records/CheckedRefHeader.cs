using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore.Internal.Records;

/// <summary>共同首帧检查取得的局部资格；不拥有关联的 RBF 句柄。</summary>
internal readonly struct CheckedRefHeader {
    private readonly IRbfFile? _file;

    internal RefId RefId { get; }
    internal SizedPtr HeaderTicket { get; }
    internal long InitialSnapshotOffset { get; }
    internal ForkOrigin? Origin { get; }

    internal CheckedRefHeader(IRbfFile file, RefHeader header, SizedPtr ticket, long initialSnapshotOffset) {
        _file = file;
        RefId = header.RefId;
        HeaderTicket = ticket;
        InitialSnapshotOffset = initialSnapshotOffset;
        Origin = header.Origin;
    }

    internal bool IsFor(IRbfFile file) => ReferenceEquals(_file, file);
}
