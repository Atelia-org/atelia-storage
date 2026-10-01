using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Ordinary files expose a stable frame stream, not arbitrary length mutation.</summary>
public sealed class RbfTruncateTests {
    [Fact]
    public void OrdinaryFileApi_DoesNotExposeTruncate() {
        Assert.Null(typeof(IRbfFile).GetMethod("Truncate"));
        Assert.Null(typeof(RbfFileImpl).GetMethod("Truncate"));
    }
}
