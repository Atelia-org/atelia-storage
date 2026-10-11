using System.Buffers;
using Atelia.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore.Internal.Tags;

// 这些自有声明值没有源读取能力，也不能独立签发 RefRevision。
internal readonly record struct TagMetadata(
    string Name, long RecordedAtUnixMs, RefId? RelatedRefId, SizedPtr? RelatedTicket);

/// <summary>物化输出前保存的轻量名称计划与精确元信息尺寸。</summary>
internal readonly struct TagMetadataEncodingPlan {
    internal TagMetadataEncodingPlan(TagMetadata metadata, StringEncodingPlan namePlan, int encodedLength) {
        Metadata = metadata;
        NamePlan = namePlan;
        EncodedLength = encodedLength;
    }

    internal TagMetadata Metadata { get; }
    internal StringEncodingPlan NamePlan { get; }
    internal int EncodedLength { get; }
}

/// <summary>独立 CRC 的可变 TailMeta；只处理已解 escape 的逻辑 buffer。</summary>
internal static class TagMetadataCodec {
    internal const int MinEncodedSize = 15;
    internal const int MaxEncodedSize = 3104;
    internal const int MaxNamePayloadByteCount = 3072;
    internal const int MaxNameCodewordLength = 3077;

    internal static TagMetadataEncodingPlan Prepare(string name, long recordedAtUnixMs,
        StoreIdentity identity, RefId? relatedRefId = null, SizedPtr? relatedTicket = null) {
        StringEncodingPlan namePlan = TagNameCodec.PrepareName(name);
        if (identity == default) {
            throw new ArgumentException("A checked VersionStore identity is required.", nameof(identity));
        }
        if (relatedRefId.HasValue != relatedTicket.HasValue
            || (relatedRefId is RefId refId && !refId.BelongsTo(identity))
            || (relatedTicket is SizedPtr ticket && !IsValidDeclaredTicket(ticket))) {
            throw new ArgumentException("Related coordinates must contain a same-store nondefault RefId and a nonempty ticket.");
        }

        long nameLength = namePlan.EncodedLength;
        uint localId = relatedRefId?.LocalId ?? 0;
        long encodedLength = checked(sizeof(long) + (long)BareValueEncoding.MeasureVarUInt32(localId)
            + (relatedTicket is SizedPtr related ? BareValueEncoding.MeasureVarUInt64(related.Serialize()) : 0)
            + nameLength + sizeof(uint));
        if (nameLength > MaxNameCodewordLength || encodedLength is < MinEncodedSize or > MaxEncodedSize) {
            throw new ArgumentOutOfRangeException(nameof(name), "The prepared tag metadata exceeds the format capacity.");
        }
        return new TagMetadataEncodingPlan(
            new TagMetadata(name, recordedAtUnixMs, relatedRefId, relatedTicket), namePlan, checked((int)encodedLength));
    }

    // 调用方先消费 EncodedLength 完成公共 RBF 尺寸及真实起点预检，再物化最终 meta。
    internal static byte[] Encode(in TagMetadataEncodingPlan plan) {
        if (plan.EncodedLength is < MinEncodedSize or > MaxEncodedSize) {
            throw new ArgumentException("An initialized tag metadata plan is required.", nameof(plan));
        }
        var output = new MetadataBuffer(plan.EncodedLength);
        var writer = new BareValueWriter(output);
        TagMetadata metadata = plan.Metadata;
        writer.WriteInt64LE(metadata.RecordedAtUnixMs);
        writer.WriteVarUInt32(metadata.RelatedRefId?.LocalId ?? 0);
        if (metadata.RelatedTicket is SizedPtr ticket) { writer.WriteVarUInt64(ticket.Serialize()); }
        StringEncodingPlan namePlan = plan.NamePlan;
        writer.WriteString(in namePlan);
        writer.WriteUInt32LE(0);
        byte[] bytes = output.TakeCompleted();
        RollingCrc.SealCodewordForward(bytes);
        return bytes;
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, StoreIdentity identity,
        uint expectedBucketId, out TagMetadata metadata) {
        metadata = default;
        if (source.Length is < MinEncodedSize or > MaxEncodedSize || identity == default
            || expectedBucketId >= TagNameCodec.BucketCount || !RollingCrc.CheckCodewordForward(source)) {
            return false;
        }

        // 这两个异常只来自下方完整逻辑 buffer 的 codec；真实文件读取不在此 try 内。
        try {
            var reader = new BareValueReader(source[..^sizeof(uint)]);
            long recordedAtUnixMs = reader.ReadInt64LE();
            uint localId = reader.ReadVarUInt32();
            RefId? relatedRefId = null;
            SizedPtr? relatedTicket = null;
            if (localId != 0) {
                SizedPtr ticket = SizedPtr.Deserialize(reader.ReadVarUInt64());
                if (!IsValidDeclaredTicket(ticket)) { return false; }
                relatedRefId = RefId.Create(identity, localId);
                relatedTicket = ticket;
            }
            int nameStart = reader.ConsumedCount;
            string name = reader.ReadString(MaxNamePayloadByteCount);
            int actualNameLength = reader.ConsumedCount - nameStart;
            reader.EnsureFullyConsumed();
            if (actualNameLength > MaxNameCodewordLength || !TagNameCodec.IsValidName(name)
                || TagNameCodec.GetBucketId(name) != expectedBucketId) { return false; }
            metadata = new TagMetadata(name, recordedAtUnixMs, relatedRefId, relatedTicket);
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (EndOfStreamException) { return false; }
    }

    private static bool IsValidDeclaredTicket(SizedPtr ticket) => ticket != default && ticket.Length > 0;

    // Scalar BPV writers request their maximum width. A 10B scratch satisfies a late hint
    // without growing the exact final meta array; only the actually advanced bytes are copied.
    private sealed class MetadataBuffer(int length) : IBufferWriter<byte> {
        private readonly byte[] _bytes = new byte[length];
        private byte[]? _scratch;
        private int _position;
        private bool _usingScratch;

        public void Advance(int count) {
            if (count < 0 || count > _bytes.Length - _position
                || (_usingScratch && count > _scratch!.Length)) {
                throw new InvalidOperationException("The tag metadata writer exceeded its measured size.");
            }
            if (_usingScratch) { _scratch.AsSpan(0, count).CopyTo(_bytes.AsSpan(_position)); }
            _position += count;
            _usingScratch = false;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            int requested = Math.Max(sizeHint, 1);
            _usingScratch = requested > _bytes.Length - _position;
            if (!_usingScratch) { return _bytes.AsMemory(_position); }
            if (requested > 10) { throw new InvalidOperationException("The tag metadata writer exceeded its measured size."); }
            _scratch ??= new byte[10];
            return _scratch;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        internal byte[] TakeCompleted() {
            if (_position != _bytes.Length) {
                throw new InvalidOperationException("The tag metadata writer did not fill its measured size.");
            }
            return _bytes;
        }
    }
}
