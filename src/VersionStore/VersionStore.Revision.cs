using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Records;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    /// <summary>精确读取同库已签发 revision 的自有 root bytes、原时间与完整位置。</summary>
    /// <remarks>
    /// 不改选当前值或更旧记录，不额外确认 data/源文件。合法未成立 CU 返回读取错误。
    /// 可写 owner 的惰性打开仍可能执行物理尾恢复；
    /// 成功恢复动作记入 <see cref="RecoveryReports"/>，与业务发布结果独立。
    /// 保存的 revision 可用于同库重开 owner；不接受裸 ticket 或外部构造的位置。
    /// </remarks>
    public AteliaResult<RefBytesSnapshot> ReadRevision(RefRevision revision) {
        EnsureOwnerUsable();
        if (!IsLocalRevision(revision)) { return InvalidRevision(); }
        var opened = GetOrOpen(revision.RefId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile file = opened.Value!;
        var header = RefRecordReader.CheckHeader(file, _identity, revision.RefId);
        if (header.IsFailure) { return header.Error!; }
        var read = ReadExactPublication(file, header.Value, revision.Ticket);
        if (read.IsFailure) { return read.Error!; }
        return read.Value.Snapshot is { } snapshot
            ? snapshot
            : new RefRecordError("The exact revision CU is not a published snapshot.");
    }

    /// <summary>重验已签发 exact 位置；null 只表示完整合法但未成立的 CU。</summary>
    private AteliaResult<PublicationRead> ReadExactPublication(IRbfFile file, CheckedRefHeader header, SizedPtr ticket) {
        if (ticket.Offset < header.InitialSnapshotOffset
            || file.GetPhysicalOffsetImmediatelyAfter(ticket) > file.TailOffset) {
            return new RefRecordError("The exact publication lies outside the checked header's completed range.");
        }

        var readInfo = file.ReadFrameInfo(ticket);
        EnsureOwnerUsable();
        if (readInfo.IsFailure) { return readInfo.Error!; }
        RbfFrameInfo info = readInfo.Value;
        if (!RefRecordCodec.IsPublicationShape(info)
            || (ticket.Offset == header.InitialSnapshotOffset && !RefRecordCodec.IsSnapshotShape(info))) {
            return new RefRecordError("The exact physical record has an invalid publication shape.");
        }
        var read = ReadPublication(file, header, info, checkState: EnsureOwnerUsable);
        if (read.IsFailure || read.Value.Snapshot is null) { return read; }

        // CRC does not cover the following Fence. Retain the successful exact anchor
        // check; a valid witness of nonpublication above does not require this work.
        var boundary = file.GetScanBoundaryAfter(ticket);
        EnsureOwnerUsable();
        if (boundary.IsFailure) { return boundary.Error!; }
        return read;
    }
}
