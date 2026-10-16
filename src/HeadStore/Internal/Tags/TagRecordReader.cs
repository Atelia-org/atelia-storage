using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Tags;

/// <summary>在已准入、Idle 的公共 RBF 句柄上检查桶与请求范围 tag 记录。</summary>
/// <remarks>不打开、恢复、关闭文件；调用方负责串行、句柄生命周期和最终 owned 资源清理。</remarks>
internal static class TagRecordReader {
    internal static AteliaResult<CheckedTagBucketHeader> CheckHeader(
        IRbfFile file, StoreIdentity expectedIdentity, uint expectedBucketId, Action? checkState = null
    ) {
        ArgumentNullException.ThrowIfNull(file);
        if (expectedIdentity == default) { throw new ArgumentException("A checked store identity is required.", nameof(expectedIdentity)); }
        if (expectedBucketId >= TagNameCodec.BucketCount) { throw new ArgumentOutOfRangeException(nameof(expectedBucketId)); }

        checkState?.Invoke();
        if (file.Format != RbfFormat.Rbf3) { return new TagHeaderError("Tag bucket files must use RBF3."); }
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        checkState?.Invoke();
        bool hasHeader = forward.MoveNext();
        checkState?.Invoke();
        if (!hasHeader) {
            return forward.TerminationError ?? new TagHeaderError("The first physical BucketHeader is missing.");
        }

        RbfFrameInfo info = forward.Current;
        if (!TagRecordCodec.IsHeaderShape(info)) { return new TagHeaderError("The first physical frame has an invalid BucketHeader shape."); }

        // Exact shape bounds this complete read before the stack buffer is obtained.
        Span<byte> buffer = stackalloc byte[info.Ticket.Length];
        checkState?.Invoke();
        var read = info.ReadFrame(buffer);
        checkState?.Invoke();
        if (read.IsFailure) { return read.Error; }
        if (read.Value.Tag != TagRecordCodec.HeaderTag || read.Value.IsTombstone
            || read.Value.TailMetaLength != 0
            || !TagBucketHeaderCodec.TryRead(read.Value.PayloadAndMeta, expectedIdentity, expectedBucketId, out _)) {
            return new TagHeaderError("The checked header version, identity, bucket, or envelope does not match the admitted context.");
        }

        var size = RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize, 0).Value;
        long recordsOffset = file.GetPhysicalOffsetImmediatelyAfter(info.Ticket);
        checkState?.Invoke();
        if (recordsOffset != RbfScanBoundary.Empty.EndExclusive + size.AppendLength) {
            return new TagHeaderError("The checked BucketHeader has an invalid physical successor boundary.");
        }
        return new CheckedTagBucketHeader(file, expectedIdentity, expectedBucketId, info.Ticket, recordsOffset);
    }

    /// <summary>真实正扫至正常 EOF；局部坏项只增加 unknownName，不猜其名称或成员表内容。</summary>
    internal static AteliaResult<TagNameLookup> ScanNamed(
        IRbfFile file, CheckedTagBucketHeader header, string name, Action? checkState = null
    ) {
        RequireHeader(file, header);
        if (!TagNameCodec.IsValidName(name)) { return new TagNameError("The requested tag name must contain 1..1024 UTF-16 code units."); }
        if (TagNameCodec.GetBucketId(name) != header.BucketId) {
            throw new ArgumentException("The requested tag name must route to the checked bucket.", nameof(name));
        }

        checkState?.Invoke();
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        checkState?.Invoke();
        bool hasHeader = forward.MoveNext();
        checkState?.Invoke();
        if (!hasHeader) {
            return forward.TerminationError ?? new TagHeaderError("The checked BucketHeader is missing from the physical chain.");
        }
        if (forward.Current.Ticket != header.HeaderTicket || !TagRecordCodec.IsHeaderShape(forward.Current)) {
            return new TagHeaderError("The physical first frame does not match this handle's checked BucketHeader.");
        }

        bool unknownName = false;
        int matchCount = 0;
        RbfFrameInfo target = default;
        TagMetadata targetMetadata = default;
        while (true) {
            checkState?.Invoke();
            bool hasRecord = forward.MoveNext();
            checkState?.Invoke();
            if (!hasRecord) {
                // A false MoveNext is never sufficient evidence for a healthy missing name.
                if (forward.TerminationError is { } error) { return error; }
                break;
            }

            RbfFrameInfo info = forward.Current;
            var metadata = ReadMetadata(info, header, checkState);
            if (metadata.IsFailure) {
                // Only errors issued by this layer for reliable, completed record content
                // are locally isolated. RBF framing, short-read and lifecycle errors escape.
                if (metadata.Error is TagRecordError) { unknownName = true; continue; }
                return metadata.Error;
            }
            if (!StringComparer.Ordinal.Equals(metadata.Value.Name, name)) { continue; }

            if (matchCount == 0) {
                target = info;
                targetMetadata = metadata.Value;
            }
            if (matchCount < 2) { matchCount++; }
            if (matchCount == 2) {
                // Duplicate state needs no retained target or name beyond the caller's request.
                target = default;
                targetMetadata = default;
            }
        }

        checkState?.Invoke();
        return new TagNameLookup(unknownName, matchCount, target, targetMetadata);
    }

    /// <summary>严格读取一条小元信息；全桶扫描直接传播本层内容错误。</summary>
    /// <remarks>
    /// info 必须来自 header 所绑定句柄的本次真实主链枚举，不能混入其他文件的 FrameInfo。
    /// 成员表长度或 CRC 不影响占名资格。底层读取失败原样返回，运行期异常不捕获。
    /// </remarks>
    internal static AteliaResult<TagMetadata> ReadMetadata(
        RbfFrameInfo info, CheckedTagBucketHeader header, Action? checkState = null
    ) {
        checkState?.Invoke();
        if (info.Ticket.Offset < header.RecordsOffset || !TagRecordCodec.IsMetadataShape(info)) {
            return new TagRecordError("The physical record has an invalid TagRecord kind, metadata size, or tombstone state.");
        }

        // Capacity is bounded before renting only the small TailMeta; never rent the member table here.
        var read = info.ReadPooledTailMeta();
        if (read.IsFailure) {
            checkState?.Invoke();
            return read.Error;
        }
        TagMetadata metadata;
        using (RbfPooledTailMeta tail = read.Value!) {
            checkState?.Invoke();
            if (tail.Ticket != info.Ticket || tail.Tag != TagRecordCodec.RecordTag || tail.IsTombstone
                || tail.TailMeta.Length != info.TailMetaLength
                || !TagMetadataCodec.TryRead(tail.TailMeta, header.Identity, header.BucketId, out metadata)) {
                return new TagRecordError("The independently checked tag metadata has invalid content, route, or member count.");
            }
        }
        checkState?.Invoke();
        return metadata;
    }

    internal static AteliaResult<TagMetadata> ReadNamedMetadata(
        IRbfFile file, CheckedTagBucketHeader header, string name, Action? checkState = null
    ) {
        var lookup = ScanNamed(file, header, name, checkState);
        if (lookup.IsFailure) { return lookup.Error; }
        var result = lookup.Value!.GetUniqueMetadata();
        checkState?.Invoke();
        return result;
    }

    internal static AteliaResult<HeadSetTag> ReadNamed(
        IRbfFile file, CheckedTagBucketHeader header, string name, Action? checkState = null
    ) {
        var lookup = ScanNamed(file, header, name, checkState);
        if (lookup.IsFailure) { return lookup.Error; }
        var unique = lookup.Value!.GetUniqueMetadata();
        checkState?.Invoke();
        if (unique.IsFailure) { return unique.Error; }
        return ReadCompleteRecord(lookup.Value.UniqueTarget, header, unique.Value, checkState);
    }

    /// <summary>完整检查主链 TagRecord 并恢复自有成员表；不读取成员文件。</summary>
    internal static AteliaResult<HeadSetTag> CheckCompleteRecord(
        RbfFrameInfo info, CheckedTagBucketHeader header, Action? checkState = null
    ) => ReadCompleteRecord(info, header, null, checkState);

    private static AteliaResult<HeadSetTag> ReadCompleteRecord(
        RbfFrameInfo info, CheckedTagBucketHeader header, TagMetadata? expectedMetadata, Action? checkState
    ) {
        checkState?.Invoke();
        if (info.Ticket.Offset < header.RecordsOffset || !TagRecordCodec.IsEntriesShape(info)) {
            return new TagEntriesError("The TagRecord member table length, kind, metadata size, or frame size is invalid.");
        }
        // Named lookup already checked the independent metadata. Reject its known
        // length contradiction before renting or reading the complete member table.
        if (expectedMetadata is TagMetadata declared
            && info.PayloadLength != (long)TagEntriesCodec.MemberEncodedSize * declared.EntryCount) {
            return new TagEntriesError("The member table length does not match its trusted entry count.");
        }

        // 完整帧尺寸在取得 lease 前受公共 RBF 自然容量约束。
        var read = info.ReadPooledFrame();
        if (read.IsFailure) {
            checkState?.Invoke();
            return read.Error;
        }
        TagMetadata metadata;
        Dictionary<HeadId, HeadEntryId> entries;
        using (RbfPooledFrame frame = read.Value!) {
            checkState?.Invoke();
            int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
            if (frame.Ticket != info.Ticket || frame.Tag != TagRecordCodec.RecordTag || frame.IsTombstone
                || frame.TailMetaLength != info.TailMetaLength || payloadLength != info.PayloadLength
                || !TagMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], header.Identity, header.BucketId, out metadata)) {
                return new TagRecordError("The completely checked tag envelope or independent metadata is invalid.");
            }
            if (expectedMetadata is TagMetadata expected && metadata != expected) {
                return new TagEntriesError("The unique target metadata differs from its trusted name declaration.");
            }
            if (!TagEntriesCodec.TryRead(frame.PayloadAndMeta[..payloadLength], header.Identity, metadata.EntryCount, out entries)) {
                return new TagEntriesError("The complete member table does not match its count or canonical member encoding.");
            }
        }
        // 只有自有字典和值在 pooled frame 的归还后继续存活。
        checkState?.Invoke();
        return new HeadSetTag(metadata.Name, metadata.RecordedAtUnixMs, entries);
    }

    private static void RequireHeader(IRbfFile file, CheckedTagBucketHeader header) {
        ArgumentNullException.ThrowIfNull(file);
        if (!header.IsFor(file)) { throw new ArgumentException("The checked bucket header must belong to this RBF handle.", nameof(header)); }
    }
}

internal sealed record TagHeaderError(string Detail) : AteliaError("HeadStore.InvalidTagBucketHeader", Detail);
internal sealed record TagRecordError(string Detail) : AteliaError("HeadStore.InvalidTagRecord", Detail);
internal sealed record TagEntriesError(string Detail) : AteliaError("HeadStore.InvalidTagEntries", Detail);
internal sealed record TagNameError(string Detail) : AteliaError("HeadStore.InvalidTagName", Detail);
