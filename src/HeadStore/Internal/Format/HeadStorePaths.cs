using System.Globalization;
using Atelia.HeadStore.Internal.Tags;

namespace Atelia.HeadStore.Internal.Format;

/// <summary>版本 1 的严格路径词法 codec；成功不证明 ordinary/no-follow、同文件系统或发布资格。</summary>
internal static class HeadStorePaths {
    internal const string FormatFileName = "versionstore.format";
    internal const string LockFileName = "versionstore.lock";
    internal const string HeadsDirectoryName = "refs";
    internal const string CreatingDirectoryName = "creating";
    internal const string TagsDirectoryName = "tags";
    internal const string PendingFileName = "pending.rbf";

    internal static string GetHeadFileName(uint localHeadId) {
        ArgumentOutOfRangeException.ThrowIfZero(localHeadId);
        return localHeadId.ToString("x8", CultureInfo.InvariantCulture) + ".rbf";
    }

    internal static string GetHeadRelativePath(uint localHeadId) {
        return Path.Combine(HeadsDirectoryName, GetHeadFileName(localHeadId));
    }

    internal static bool TryParseHeadFileName(ReadOnlySpan<char> name, out uint localHeadId) {
        localHeadId = 0;
        if (!TryParseHexFileName(name, out uint value) || value == 0) { return false; }
        localHeadId = value;
        return true;
    }

    internal static bool TryParseTagBucketFileName(ReadOnlySpan<char> name, out uint bucketId) {
        bucketId = 0;
        if (!TryParseHexFileName(name, out uint value) || value >= TagNameCodec.BucketCount) { return false; }
        bucketId = value;
        return true;
    }

    private static bool TryParseHexFileName(ReadOnlySpan<char> name, out uint value) {
        value = 0;
        if (name.Length != 12 || !name[8..].SequenceEqual(".rbf")) { return false; }
        foreach (char digit in name[..8]) {
            uint nibble;
            if (digit is >= '0' and <= '9') { nibble = (uint)(digit - '0'); }
            else if (digit is >= 'a' and <= 'f') { nibble = (uint)(digit - 'a' + 10); }
            else { return false; }
            value = (value << 4) | nibble;
        }
        return true;
    }
}
