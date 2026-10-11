using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore.Internal.Tags;

/// <summary>共同首帧检查取得的桶身份与初始化边界；不拥有借入的 RBF 句柄。</summary>
internal readonly struct CheckedTagBucketHeader {
    private readonly IRbfFile? _file;

    internal StoreIdentity Identity { get; }
    internal uint BucketId { get; }
    internal SizedPtr HeaderTicket { get; }
    internal long RecordsOffset { get; }

    internal CheckedTagBucketHeader(IRbfFile file, StoreIdentity identity, uint bucketId,
        SizedPtr headerTicket, long recordsOffset) {
        _file = file;
        Identity = identity;
        BucketId = bucketId;
        HeaderTicket = headerTicket;
        RecordsOffset = recordsOffset;
    }

    internal bool IsFor(IRbfFile file) => ReferenceEquals(_file, file);
}
