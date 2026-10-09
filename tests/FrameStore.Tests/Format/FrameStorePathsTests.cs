using Atelia.FrameStore.Internal.Format;
using Xunit;

namespace Atelia.FrameStore.Tests.Format;

public class FrameStorePathsTests {
    [Theory]
    [InlineData(0x00000001u, "00000001.rbf", "000000")]
    [InlineData(0x000003ffu, "000003ff.rbf", "000000")]
    [InlineData(0x00000400u, "00000400.rbf", "000001")]
    [InlineData(0x89abcdefu, "89abcdef.rbf", "226af3")]
    [InlineData(0xffffffffu, "ffffffff.rbf", "3fffff")]
    public void NormativeVectors_GenerateAndParseUniqueComponents(uint fileId, string fileName, string bucketName) {
        Assert.Equal(fileName, FrameStorePaths.GetFileName(fileId));
        Assert.Equal(bucketName, FrameStorePaths.GetBucketName(fileId));
        Assert.Equal(Path.Combine("active", fileName), FrameStorePaths.GetActiveRelativePath(fileId));
        Assert.Equal(Path.Combine("archive", bucketName, fileName), FrameStorePaths.GetArchiveRelativePath(fileId));
        Assert.True(FrameStorePaths.TryParseFileName(fileName, out uint parsedFile));
        Assert.Equal(fileId, parsedFile);
        Assert.True(FrameStorePaths.TryParseArchiveFileName(bucketName, fileName, out uint parsedArchiveFile));
        Assert.Equal(fileId, parsedArchiveFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000.rbf")]
    [InlineData("1.rbf")]
    [InlineData("0000001.rbf")]
    [InlineData("000000001.rbf")]
    [InlineData("89ABCDEF.rbf")]
    [InlineData("00000001.RBF")]
    [InlineData("00000001.rbf ")]
    [InlineData("00000001.rbf.")]
    [InlineData("00000001.rbf.bak")]
    [InlineData(" 00000001.rbf")]
    [InlineData("+0000001.rbf")]
    [InlineData("0x000001.rbf")]
    [InlineData("０００００００１.rbf")]
    [InlineData("00000001/rbf")]
    [InlineData("active/00000001.rbf")]
    [InlineData("nested\\00000001.rbf")]
    public void FileName_RejectsAliasesAndExtraLayers(string name) {
        Assert.False(FrameStorePaths.TryParseFileName(name, out uint fileId));
        Assert.Equal(0u, fileId);
    }

    [Theory]
    [InlineData("000000", 0u)]
    [InlineData("000001", 1u)]
    [InlineData("3fffff", 0x3fffffu)]
    public void Bucket_ValidatesEvenEmptyDirectoryComponents(string name, uint expected) {
        Assert.True(FrameStorePaths.TryParseBucketName(name, out uint bucket));
        Assert.Equal(expected, bucket);
    }

    [Theory]
    [InlineData("400000")]
    [InlineData("ffffff")]
    [InlineData("3FFFFF")]
    [InlineData("00000")]
    [InlineData("0000000")]
    [InlineData("000000 ")]
    [InlineData("000000.")]
    [InlineData("00000g")]
    [InlineData("00000１")]
    [InlineData("000000/000001")]
    public void Bucket_RejectsNoncanonicalAndOutOfRangeComponents(string name) {
        Assert.False(FrameStorePaths.TryParseBucketName(name, out uint bucket));
        Assert.Equal(0u, bucket);
    }

    [Theory]
    [InlineData("000000", "00000400.rbf")]
    [InlineData("000001", "000003ff.rbf")]
    [InlineData("226af2", "89abcdef.rbf")]
    [InlineData("400000", "ffffffff.rbf")]
    [InlineData("000000", "00000000.rbf")]
    public void Archive_RejectsWrongBucketWithoutIssuingFileId(string bucket, string file) {
        Assert.False(FrameStorePaths.TryParseArchiveFileName(bucket, file, out uint fileId));
        Assert.Equal(0u, fileId);
    }

    [Fact]
    public void Generators_RejectZeroFileId() {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStorePaths.GetFileName(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStorePaths.GetBucketName(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStorePaths.GetActiveRelativePath(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStorePaths.GetArchiveRelativePath(0));
    }
}
