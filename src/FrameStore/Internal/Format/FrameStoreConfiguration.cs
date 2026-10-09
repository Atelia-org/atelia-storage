using System.Text.Json;

namespace Atelia.FrameStore.Internal.Format;

/// <summary>纯配置解析；缺失文件与访问模式由调用方裁决，不在此执行 I/O。</summary>
internal readonly struct FrameStoreConfiguration {
    internal static FrameStoreConfiguration Default => new(32);
    internal int MaxOutstandingBuilders { get; }

    private FrameStoreConfiguration(int maxOutstandingBuilders) {
        MaxOutstandingBuilders = maxOutstandingBuilders;
    }

    internal static bool TryParse(ReadOnlySpan<byte> utf8Json, out FrameStoreConfiguration configuration) {
        configuration = default;
        try {
            var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { return false; }
            bool seenLimit = false;
            int limit = Default.MaxOutstandingBuilders;
            while (reader.Read()) {
                if (reader.TokenType == JsonTokenType.EndObject) {
                    if (reader.Read()) { return false; }
                    configuration = new FrameStoreConfiguration(limit);
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName ||
                    seenLimit || !reader.ValueTextEquals("MaxOutstandingBuilders"u8)) {
                    return false;
                }

                seenLimit = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out limit) || limit < 1) {
                    return false;
                }
            }
        }
        catch (JsonException) {
            return false;
        }

        return false;
    }
}
