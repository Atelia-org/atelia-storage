using Atelia.VersionStore.Internal.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.Format;

public class VersionStorePathsTests {
    [Theory]
    [InlineData(1u, "00000001.rbf")]
    [InlineData(7u, "00000007.rbf")]
    [InlineData(0x80000000u, "80000000.rbf")]
    [InlineData(0x89ABCDEFu, "89abcdef.rbf")]
    [InlineData(uint.MaxValue, "ffffffff.rbf")]
    public void RefName_IndependentTextGoldensAreBigEndianHexOfLocalValue(uint localId, string expected) {
        Assert.Equal(expected, VersionStorePaths.GetRefFileName(localId));
        Assert.Equal(Path.Combine("refs", expected), VersionStorePaths.GetRefRelativePath(localId));
        Assert.True(VersionStorePaths.TryParseRefFileName(expected, out uint parsed));
        Assert.Equal(localId, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000.rbf")]
    [InlineData("1.rbf")]
    [InlineData("000000001.rbf")]
    [InlineData("100000000.rbf")]
    [InlineData("89ABCDEF.rbf")]
    [InlineData("89abcdef.RBF")]
    [InlineData("89abcdef.Rbf")]
    [InlineData("0x89abcdef.rbf")]
    [InlineData("+0000001.rbf")]
    [InlineData("-0000001.rbf")]
    [InlineData(" 0000001.rbf")]
    [InlineData("00000001.rbf ")]
    [InlineData("00000001.rbf.")]
    [InlineData("00000001.rbf\n")]
    [InlineData("00000001.rbf\0")]
    [InlineData("00000001rbf")]
    [InlineData("00000001.rbf.bak")]
    [InlineData("refs/00000001.rbf")]
    [InlineData("refs\\00000001.rbf")]
    [InlineData("../00000001.rbf")]
    [InlineData("0000000１.rbf")]
    [InlineData("0000000١.rbf")]
    [InlineData("89abcdeｆ.rbf")]
    [InlineData("89abcdeg.rbf")]
    public void RefName_RejectsNoncanonicalActualWholeNamesWithoutNormalizing(string name) {
        uint localId = 7;
        Assert.False(VersionStorePaths.TryParseRefFileName(name, out localId));
        Assert.Equal(0u, localId);
    }

    [Fact]
    public void ZeroCannotBeFormattedAndManagedNamesRemainExactLowercase() {
        Assert.Throws<ArgumentOutOfRangeException>(() => VersionStorePaths.GetRefFileName(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VersionStorePaths.GetRefRelativePath(0));
        Assert.Equal("versionstore.format", VersionStorePaths.FormatFileName);
        Assert.Equal("versionstore.lock", VersionStorePaths.LockFileName);
        Assert.Equal("refs", VersionStorePaths.RefsDirectoryName);
        Assert.Equal("creating", VersionStorePaths.CreatingDirectoryName);
        Assert.Equal("tags", VersionStorePaths.TagsDirectoryName);
        Assert.Equal("pending.rbf", VersionStorePaths.PendingFileName);
    }
}
