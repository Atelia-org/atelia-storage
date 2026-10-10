using System.Globalization;

namespace Atelia.VersionStore.Internal.Format;

/// <summary>版本 1 的严格路径词法 codec；成功不证明 ordinary/no-follow、同文件系统或发布资格。</summary>
internal static class VersionStorePaths {
    internal const string FormatFileName = "versionstore.format";
    internal const string LockFileName = "versionstore.lock";
    internal const string RefsDirectoryName = "refs";
    internal const string CreatingDirectoryName = "creating";
    internal const string TagsDirectoryName = "tags";
    internal const string PendingFileName = "pending.rbf";

    internal static string GetRefFileName(uint localRefId) {
        ArgumentOutOfRangeException.ThrowIfZero(localRefId);
        return localRefId.ToString("x8", CultureInfo.InvariantCulture) + ".rbf";
    }

    internal static string GetRefRelativePath(uint localRefId) {
        return Path.Combine(RefsDirectoryName, GetRefFileName(localRefId));
    }

    internal static bool TryParseRefFileName(ReadOnlySpan<char> name, out uint localRefId) {
        localRefId = 0;
        if (name.Length != 12 || !name[8..].SequenceEqual(".rbf")) { return false; }
        uint value = 0;
        foreach (char digit in name[..8]) {
            uint nibble;
            if (digit is >= '0' and <= '9') { nibble = (uint)(digit - '0'); }
            else if (digit is >= 'a' and <= 'f') { nibble = (uint)(digit - 'a' + 10); }
            else { return false; }
            value = (value << 4) | nibble;
        }

        if (value == 0 || !name.SequenceEqual(GetRefFileName(value))) { return false; }
        localRefId = value;
        return true;
    }
}
