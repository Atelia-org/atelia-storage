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

    /// <summary>真实正扫至正常 EOF；局部坏项只增加 unknownName，不猜其名称或 root 内容。</summary>
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
    /// root 长度或 CRC 不影响占名资格。底层读取失败原样返回，运行期异常不捕获。
    /// </remarks>
    internal static AteliaResult<TagMetadata> ReadMetadata(
        RbfFrameInfo info, CheckedTagBucketHeader header, Action? checkState = null
    ) {
        checkState?.Invoke();
        if (info.Ticket.Offset < header.RecordsOffset || !TagRecordCodec.IsMetadataShape(info)) {
            return new TagRecordError("The physical record has an invalid TagRecord kind, metadata size, or tombstone state.");
        }

        // Capacity is bounded before renting only the small TailMeta; never rent the root here.
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
                return new TagRecordError("The independently checked tag metadata has invalid content, route, or related coordinates.");
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

    internal static AteliaResult<RootPayload> ResolveNamed(
        IRbfFile file, CheckedTagBucketHeader header, string name, Action? checkState = null
    ) {
        var lookup = ScanNamed(file, header, name, checkState);
        if (lookup.IsFailure) { return lookup.Error; }
        var unique = lookup.Value!.GetUniqueMetadata();
        checkState?.Invoke();
        if (unique.IsFailure) { return unique.Error; }
        return ReadRoot(lookup.Value.UniqueTarget, header, unique.Value, checkState);
    }

    /// <summary>完整检查一条实际主链 TagRecord 的 root 包络与独立元信息，不复制 opaque root bytes。</summary>
    internal static AteliaResult<TagMetadata> CheckCompleteRecord(
        RbfFrameInfo info, CheckedTagBucketHeader header, Action? checkState = null
    ) {
        checkState?.Invoke();
        if (info.Ticket.Offset < header.RecordsOffset || !TagRecordCodec.IsRootShape(info)) {
            return new TagRootError("The physical TagRecord has an invalid root length, kind, metadata size, or frame size.");
        }
        var read = info.ReadPooledFrame();
        if (read.IsFailure) {
            checkState?.Invoke();
            return read.Error;
        }
        using RbfPooledFrame frame = read.Value!;
        checkState?.Invoke();
        int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
        if (frame.Ticket != info.Ticket || frame.Tag != TagRecordCodec.RecordTag || frame.IsTombstone
            || frame.TailMetaLength != info.TailMetaLength || payloadLength != info.PayloadLength
            || !TagMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], header.Identity, header.BucketId,
                out TagMetadata metadata)) {
            return new TagRecordError("The completely checked tag envelope or independent metadata is invalid.");
        }
        return metadata;
    }

    private static AteliaResult<RootPayload> ReadRoot(
        RbfFrameInfo info, CheckedTagBucketHeader header, TagMetadata expectedMetadata, Action? checkState
    ) {
        checkState?.Invoke();
        if (!TagRecordCodec.IsRootShape(info)) {
            return new TagRootError("The unique target's root length or physical frame size is invalid.");
        }

        // Complete frame capacity is checked only after normal EOF proved a unique target.
        var read = info.ReadPooledFrame();
        if (read.IsFailure) {
            checkState?.Invoke();
            return read.Error;
        }
        byte[] ownedBytes;
        using (RbfPooledFrame frame = read.Value!) {
            checkState?.Invoke();
            int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
            if (frame.Ticket != info.Ticket || frame.Tag != TagRecordCodec.RecordTag || frame.IsTombstone
                || frame.TailMetaLength != info.TailMetaLength || payloadLength != info.PayloadLength
                || !TagMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], header.Identity, header.BucketId, out TagMetadata metadata)
                || metadata != expectedMetadata) {
                return new TagRootError("The unique target's completely checked envelope or metadata is invalid.");
            }
            ownedBytes = frame.PayloadAndMeta[..payloadLength].ToArray();
        }
        // Only independent GC bytes survive the pooled frame's disposal.
        checkState?.Invoke();
        return new RootPayload(ownedBytes);
    }

    private static void RequireHeader(IRbfFile file, CheckedTagBucketHeader header) {
        ArgumentNullException.ThrowIfNull(file);
        if (!header.IsFor(file)) { throw new ArgumentException("The checked bucket header must belong to this RBF handle.", nameof(header)); }
    }
}

internal sealed record TagHeaderError(string Detail) : AteliaError("HeadStore.InvalidTagBucketHeader", Detail);
internal sealed record TagRecordError(string Detail) : AteliaError("HeadStore.InvalidTagRecord", Detail);
internal sealed record TagRootError(string Detail) : AteliaError("HeadStore.InvalidTagRoot", Detail);
internal sealed record TagNameError(string Detail) : AteliaError("HeadStore.InvalidTagName", Detail);
