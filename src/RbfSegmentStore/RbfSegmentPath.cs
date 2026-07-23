using System.Globalization;

namespace Atelia.RbfSegmentStore;

internal static class RbfSegmentPath {
    internal const int SegmentBucketBits = 10;
    internal const uint SegmentBucketMask = (1u << SegmentBucketBits) - 1;
    internal const string SegmentFileExtension = ".rbf";
    internal const long RbfHeaderOnlyLength = 4;
    internal const string BucketedDirectoryName = "buckets";
    internal const string FlatDirectoryName = "segments";

    internal static string LayoutDirectory(string storePath, RbfSegmentStoreLayout layout) {
        return layout switch {
            RbfSegmentStoreLayout.Bucketed => Path.Combine(storePath, BucketedDirectoryName),
            RbfSegmentStoreLayout.Flat => Path.Combine(storePath, FlatDirectoryName),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown RBF segment store layout.")
        };
    }

    internal static string BucketedDirectory(string storePath) {
        return LayoutDirectory(storePath, RbfSegmentStoreLayout.Bucketed);
    }

    internal static string FlatDirectory(string storePath) {
        return LayoutDirectory(storePath, RbfSegmentStoreLayout.Flat);
    }

    internal static string BucketName(uint segmentNumber) {
        return (segmentNumber >> SegmentBucketBits).ToString("x6", CultureInfo.InvariantCulture);
    }

    internal static string FileName(uint segmentNumber) {
        return segmentNumber.ToString("x8", CultureInfo.InvariantCulture) + SegmentFileExtension;
    }

    internal static string GetSegmentPath(string storePath, RbfSegmentStoreLayout layout, uint segmentNumber) {
        if (segmentNumber == 0) { throw new ArgumentOutOfRangeException(nameof(segmentNumber), segmentNumber, "Segment number 0 is reserved."); }
        return layout switch {
            RbfSegmentStoreLayout.Bucketed => Path.Combine(BucketedDirectory(storePath), BucketName(segmentNumber), FileName(segmentNumber)),
            RbfSegmentStoreLayout.Flat => Path.Combine(FlatDirectory(storePath), FileName(segmentNumber)),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown RBF segment store layout.")
        };
    }

    internal static void EnsureSegmentDirectory(string storePath, RbfSegmentStoreLayout layout, uint segmentNumber) {
        Directory.CreateDirectory(Path.GetDirectoryName(GetSegmentPath(storePath, layout, segmentNumber))!);
    }

    internal static bool TryParseBucketName(string name, out uint bucketNumber) {
        bucketNumber = 0;
        return name.Length == 6
            && IsLowerHex(name)
            && uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bucketNumber);
    }

    internal static bool TryParseSegmentFileName(string name, out uint segmentNumber) {
        segmentNumber = 0;
        if (!name.EndsWith(SegmentFileExtension, StringComparison.Ordinal)) { return false; }

        string stem = name[..^SegmentFileExtension.Length];
        return stem.Length == 8
            && IsLowerHex(stem)
            && uint.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out segmentNumber);
    }

    private static bool IsLowerHex(string value) {
        foreach (char c in value) {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { return false; }
        }

        return true;
    }
}
