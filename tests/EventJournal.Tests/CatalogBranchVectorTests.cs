using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class CatalogBranchVectorTests {
    [Theory]
    [InlineData(false, "454a435301004000520000000000000004010000000000001f000084000000000976ce2d0100000000000000010000000000000000000000000000000000000004006d61696e1f00000400000000ebaf6cf4")]
    [InlineData(true, "454a435301004000520000000000000004010000000000001f00008400000000d9955a5d0100000000000000010000000000000000000000000000000000000004006d61696e1f00000400000000e924cd43")]
    public void RealControlCodecsMatchIndependentBranchOnlyVectors(bool hasHead, string expectedHex) {
        string path = Path.Combine(Path.GetTempPath(), "catalog-branch-vector-" + Guid.NewGuid().ToString("N"));
        try {
            // Pure codec witness with a fixed timestamp; public CreateBranch uses the wall clock.
            using var log = RbfFile.CreateNew(path);
            EventAddress? head = hasHead
                ? new EventAddress(SizedPtr.FromPacked(0x0000000004000016), 1, default)
                : null;
            var allocation = new RefOpFrame(RefOpOperation.Create, "main", default, default, 0, null, head, 0, 0);
            var id = new RefId(log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(allocation)).Unwrap().Packed);
            var binding = allocation with { Operation = RefOpOperation.BindName, RefId = id };
            var bindTicket = log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(binding)).Unwrap();
            var boundary = log.GetScanBoundaryAfter(bindTicket).Unwrap();
            using var encoded = new MemoryStream();
            CatalogSnapshotCodec.Write(encoded, new(boundary, new() { ["main"] = id }, new()));
            Assert.Equal(expectedHex, Convert.ToHexStringLower(encoded.ToArray()));
            encoded.Position = 0;
            var decoded = CatalogSnapshotCodec.Read(encoded, "vector.snapshot");
            Assert.Equal(boundary, decoded.Boundary);
            Assert.Equal(id, decoded.Branches["main"]);
            Assert.Empty(decoded.Tags);
        }
        finally { File.Delete(path); }
    }
}
