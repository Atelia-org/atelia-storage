using System.Globalization;
using Atelia.Binary;

namespace Atelia.HeadStore.Internal.Tags;

/// <summary>保留原 UTF-16 code units 的名称准入与格式版本 1 的稳定路由。</summary>
internal static class TagNameCodec {
    internal const int BucketCount = 1024;
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    internal static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= HeadStore.MaxTagNameCodeUnits };

    internal static StringEncodingPlan PrepareName(string name) {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsValidName(name)) {
            throw new ArgumentException("A tag name must contain 1 to 1,024 UTF-16 code units.", nameof(name));
        }
        return BareValueEncoding.PrepareString(name);
    }

    internal static ulong GetHash(string name) {
        if (!IsValidName(name)) {
            throw new ArgumentException("A tag name must contain 1 to 1,024 UTF-16 code units.", nameof(name));
        }
        ulong hash = FnvOffsetBasis;
        foreach (char codeUnit in name) {
            hash = unchecked((hash ^ (byte)codeUnit) * FnvPrime);
            hash = unchecked((hash ^ (byte)(codeUnit >> 8)) * FnvPrime);
        }
        return hash;
    }

    internal static uint GetBucketId(string name) {
        ulong hash = GetHash(name);
        return (uint)((hash ^ (hash >> 10)) & (BucketCount - 1));
    }

    internal static string GetBucketFileName(uint bucketId) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bucketId, (uint)BucketCount);
        return bucketId.ToString("x8", CultureInfo.InvariantCulture) + ".rbf";
    }
}
