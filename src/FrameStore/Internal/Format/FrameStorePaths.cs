using System.Globalization;

namespace Atelia.FrameStore.Internal.Format;

/// <summary>版本 1 的严格路径组件 codec；词法成功不证明类型、no-follow 或同文件系统资格。</summary>
internal static class FrameStorePaths {
    internal const string ActiveDirectoryName = "active";
    internal const string ArchiveDirectoryName = "archive";
    internal const int BucketShift = 10;

    private const uint MaxBucket = uint.MaxValue >> BucketShift;

    internal static string GetFileName(uint fileId) {
        ArgumentOutOfRangeException.ThrowIfZero(fileId);
        return fileId.ToString("x8", CultureInfo.InvariantCulture) + ".rbf";
    }

    internal static string GetBucketName(uint fileId) {
        ArgumentOutOfRangeException.ThrowIfZero(fileId);
        return FormatBucket(fileId >> BucketShift);
    }

    internal static string GetActiveRelativePath(uint fileId) {
        return Path.Combine(ActiveDirectoryName, GetFileName(fileId));
    }

    internal static string GetArchiveRelativePath(uint fileId) {
        return Path.Combine(ArchiveDirectoryName, GetBucketName(fileId), GetFileName(fileId));
    }

    internal static bool TryParseFileName(ReadOnlySpan<char> name, out uint fileId) {
        fileId = 0;
        if (name.Length != 12 || !name[8..].SequenceEqual(".rbf")) { return false; }
        if (!TryParseHex(name[..8], out uint value) || value == 0 || !name.SequenceEqual(GetFileName(value))) { return false; }
        fileId = value;
        return true;
    }

    internal static bool TryParseBucketName(ReadOnlySpan<char> name, out uint bucket) {
        bucket = 0;
        if (name.Length != 6 || !TryParseHex(name, out uint value) || value > MaxBucket || !name.SequenceEqual(FormatBucket(value))) {
            return false;
        }

        bucket = value;
        return true;
    }

    internal static bool TryParseArchiveFileName(ReadOnlySpan<char> bucketName, ReadOnlySpan<char> fileName, out uint fileId) {
        fileId = 0;
        if (!TryParseBucketName(bucketName, out uint bucket) ||
            !TryParseFileName(fileName, out uint value) ||
            bucket != value >> BucketShift) {
            return false;
        }

        fileId = value;
        return true;
    }

    private static string FormatBucket(uint bucket) {
        return bucket.ToString("x6", CultureInfo.InvariantCulture);
    }

    private static bool TryParseHex(ReadOnlySpan<char> source, out uint value) {
        value = 0;
        foreach (char digit in source) {
            uint nibble;
            if (digit is >= '0' and <= '9') { nibble = (uint)(digit - '0'); }
            else if (digit is >= 'a' and <= 'f') { nibble = (uint)(digit - 'a' + 10); }
            else { value = 0; return false; }
            value = (value << 4) | nibble;
        }

        return true;
    }
}
