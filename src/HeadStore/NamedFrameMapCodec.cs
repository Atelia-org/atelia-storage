using System.Collections.Frozen;
using Atelia.Binary;
using Atelia.FrameStore;
using Atelia.HeadStore.Internal.NamedFrameMap;

namespace Atelia.HeadStore;

/// <summary>可选的 string→FrameAddress 应用 payload codec；不访问 store 或发布状态。</summary>
public static class NamedFrameMapCodec {
    private const int MinAddressLength = 3;
    private const int MinEntryLength = 1 + MinAddressLength;

    /// <summary>捕获实际枚举内容并生成调用方拥有的精确长度数组。</summary>
    /// <remarks>
    /// key 使用有效 Unicode 和 Ordinal 比较，允许空字串和控制字符，不规范化。
    /// 调用方须在整个同步调用期间保持集合稳定；自定义枚举与清理异常直接传播。
    /// </remarks>
    public static AteliaResult<byte[]> Encode(IReadOnlyDictionary<string, FrameAddress> frames) {
        if (frames is null) { return InvalidInput("The root dictionary is null."); }

        // Neither source Count nor its comparer or lookup methods participate in capture.
        var captured = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
        long fieldsLength = 0;
        foreach (var entry in frames) {
            if (entry.Key is null || entry.Value == default) {
                return InvalidInput("A root key is null or its address is default.");
            }
            // Every valid code unit needs at least one payload byte in either Bare encoding.
            if ((long)entry.Key.Length + 1 + MinAddressLength + 1 > HeadStore.MaxHeadEntryPayloadLength) {
                return TooLarge();
            }
            if (!IsValidUnicode(entry.Key)) {
                return InvalidInput("A root key contains an unpaired UTF-16 surrogate.");
            }
            if (!captured.TryAdd(entry.Key, entry.Value)) {
                return InvalidInput("The actual root enumeration contains duplicate Ordinal keys.");
            }
            fieldsLength = checked(fieldsLength + BareValueEncoding.PrepareString(entry.Key).EncodedLength
                + entry.Value.MeasureVarInt());
            long totalLength = checked(fieldsLength + BareValueEncoding.MeasureVarUInt32((uint)captured.Count));
            if (totalLength > HeadStore.MaxHeadEntryPayloadLength) { return TooLarge(); }
        }

        // Enumeration and its necessary Dispose have completed before any output allocation.
        long encodedLength = checked(fieldsLength + BareValueEncoding.MeasureVarUInt32((uint)captured.Count));
        var output = new ExactNamedFrameMapBuffer(checked((int)encodedLength));
        var writer = new BareValueWriter(output);
        writer.WriteVarUInt32((uint)captured.Count);
        foreach (var entry in captured) {
            StringEncodingPlan plan = BareValueEncoding.PrepareString(entry.Key);
            writer.WriteString(in plan);
            entry.Value.WriteVarInt(writer);
        }
        return output.TakeCompletedBody();
    }

    /// <summary>解码完整 codeword，拒绝尾随 bytes，返回自有 Ordinal 冻结字典。</summary>
    /// <remarks>输入须在同步调用期间有效且稳定；结果独立于输入，不需要 Dispose。</remarks>
    public static AteliaResult<IReadOnlyDictionary<string, FrameAddress>> Decode(ReadOnlySpan<byte> payload) {
        if (payload.IsEmpty) { return InvalidFormat("A NamedFrameMap codeword must contain at least one byte."); }
        if (payload.Length > HeadStore.MaxHeadEntryPayloadLength) { return TooLarge(); }

        try {
            var cursor = new BareValueReader(payload);
            uint count = cursor.ReadVarUInt32();
            long remainingBudget = HeadStore.MaxHeadEntryPayloadLength - (long)cursor.ConsumedCount;
            if (MinEntryLength * (long)count > Math.Min(cursor.RemainingCount, remainingBudget)) {
                return InvalidFormat("The entry count cannot fit in the actual remaining codeword.");
            }
            var decoded = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
            for (uint i = 0; i < count; i++) {
                // Preview the actual, possibly redundant header before ReadString allocates.
                var preview = cursor;
                uint header = preview.ReadVarUInt32();
                uint declaredPayloadLength = (header & 1) == 0 ? header : header >> 1;
                long requiredSuffix = MinAddressLength + MinEntryLength * (long)(count - i - 1);
                remainingBudget = HeadStore.MaxHeadEntryPayloadLength - (long)preview.ConsumedCount;
                if (declaredPayloadLength > int.MaxValue
                    || declaredPayloadLength + requiredSuffix > Math.Min(preview.RemainingCount, remainingBudget)) {
                    return InvalidFormat("The string declaration leaves insufficient bytes for the required suffix.");
                }
                string key = cursor.ReadString((int)declaredPayloadLength);
                if (!IsValidUnicode(key)) {
                    return InvalidFormat("A decoded root key contains an unpaired UTF-16 surrogate.");
                }
                FrameAddress address = FrameAddress.ReadVarInt(ref cursor);
                if (!decoded.TryAdd(key, address)) {
                    return InvalidFormat("The codeword contains duplicate decoded Ordinal keys.");
                }
            }
            cursor.EnsureFullyConsumed();
            return AteliaResult<IReadOnlyDictionary<string, FrameAddress>>.Success(
                decoded.ToFrozenDictionary(StringComparer.Ordinal));
        }
        catch (InvalidDataException exception) { return InvalidFormat(exception.Message); }
        catch (EndOfStreamException exception) { return InvalidFormat(exception.Message); }
    }

    private static bool IsValidUnicode(string key) {
        for (int i = 0; i < key.Length; i++) {
            char codeUnit = key[i];
            if (char.IsHighSurrogate(codeUnit)) {
                if (i + 1 == key.Length || !char.IsLowSurrogate(key[i + 1])) { return false; }
                i++;
            }
            else if (char.IsLowSurrogate(codeUnit)) { return false; }
        }
        return true;
    }

    private static NamedFrameMapError InvalidInput(string message) {
        return new NamedFrameMapError("HeadStore.NamedFrameMapInvalidInput", message);
    }

    private static NamedFrameMapError InvalidFormat(string message) {
        return new NamedFrameMapError("HeadStore.NamedFrameMapInvalidFormat", message);
    }

    private static NamedFrameMapError TooLarge() {
        return new NamedFrameMapError("HeadStore.NamedFrameMapTooLarge", "The NamedFrameMap codeword exceeds 1,048,576 bytes.");
    }

    private sealed record NamedFrameMapError(string ErrorCode, string Message) : AteliaError(ErrorCode, Message);
}
