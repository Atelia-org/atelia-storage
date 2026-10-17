using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Records;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    /// <summary>在当前库中精确读取 entryId 的自有 HeadEntry payload bytes、原时间与完整位置。</summary>
    /// <remarks>
    /// 不改选当前值或更旧记录，不额外确认 data/源文件。合法未成立 CU 返回读取错误。
    /// 可写 owner 的惰性打开仍可能执行物理尾恢复；
    /// 成功恢复动作记入 <see cref="RecoveryReports"/>，与业务发布结果独立。
    /// entryId 可由公开 codec 无 owner 恢复；每次使用仍检查准确记录和发布资格。
    /// 紧凑 Handle 仅概率检出跨库误用；调用方负责选择正确库。
    /// </remarks>
    public AteliaResult<HeadEntry> ReadEntry(HeadEntryId entryId) {
        EnsureOwnerUsable();
        if (!IsLocalEntryId(entryId)) { return InvalidEntryId(); }
        var opened = GetOrOpen(entryId.HeadId.GetLocalId(_identity));
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile file = opened.Value!;
        var header = HeadRecordReader.CheckHeader(file, _identity, entryId.HeadId);
        if (header.IsFailure) { return header.Error!; }
        var read = ReadExactPublication(file, header.Value, entryId.Ticket);
        if (read.IsFailure) { return read.Error!; }
        return read.Value.Entry is { } entry
            ? entry
            : new HeadRecordError("The exact entryId CU is not a published entry.");
    }

    /// <summary>重验 exact 定位值；null 只表示完整合法但未成立的 CU。</summary>
    private AteliaResult<PublicationRead> ReadExactPublication(IRbfFile file, CheckedHeadHeader header, SizedPtr ticket,
        Action? checkState = null, bool deferOwnedCleanup = false, HashSet<uint>? dependencies = null) {
        CheckConditionalState(checkState);
        if (ticket.Offset < header.RecordsOffset
            || file.GetPhysicalOffsetImmediatelyAfter(ticket) > file.TailOffset) {
            return new HeadRecordError("The exact publication lies outside the checked header's completed range.");
        }

        var readInfo = file.ReadFrameInfo(ticket);
        CheckConditionalState(checkState);
        if (readInfo.IsFailure) { return readInfo.Error!; }
        RbfFrameInfo info = readInfo.Value;
        if (!header.IsAllowedPublication(info)) {
            return new HeadRecordError("The exact physical record has an invalid publication shape.");
        }
        var read = ReadPublication(file, header, info, checkState: checkState,
            deferOwnedCleanup: deferOwnedCleanup, dependencies: dependencies);
        if (read.IsFailure || read.Value.Entry is null) { return read; }

        // CRC does not cover the following Fence. Retain the successful exact anchor
        // check; a valid witness of nonpublication above does not require this work.
        var boundary = file.GetScanBoundaryAfter(ticket);
        CheckConditionalState(checkState);
        if (boundary.IsFailure) { return boundary.Error!; }
        return read;
    }
}
