using Atelia.Data;
using Atelia.Rbf.Internal.Tests;
using Xunit;

namespace Atelia.Rbf.Tests;

public sealed class RbfFormatTests : IDisposable {
    private readonly string _rbf3Path = Path.Combine(Path.GetTempPath(), $"rbf-format-3-{Guid.NewGuid():N}.rbf");
    private readonly string _rbf1EmptyPath = Path.Combine(Path.GetTempPath(), $"rbf-format-1-empty-{Guid.NewGuid():N}.rbf");
    private readonly string _rbf1Path = Path.Combine(Path.GetTempPath(), $"rbf-format-1-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        File.Delete(_rbf3Path);
        File.Delete(_rbf1EmptyPath);
        File.Delete(_rbf1Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format_ReportsRbf3ForEmptyAndNonemptyFiles(bool populated) {
        SizedPtr ticket;
        using (IRbfFile file = RbfFile.CreateNew(_rbf3Path)) {
            Assert.Equal(RbfFormat.Rbf3, file.Format);
            ticket = populated ? file.Append(11, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }).Unwrap() : default;
            Assert.Equal(RbfFormat.Rbf3, file.Format);
        }

        using IRbfFile reopened = RbfFile.OpenReadOnlyExisting(_rbf3Path);
        Assert.Equal(RbfFormat.Rbf3, reopened.Format);
        if (populated) {
            using var frame = reopened.ReadPooledFrame(ticket).Unwrap();
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, frame.PayloadAndMeta.ToArray());
        }
    }

    [Fact]
    public void Format_ReportsRbf1AndRetainsLegacyReadQualification() {
        using (RawRbfTestFile.CreateLegacy(_rbf1EmptyPath)) { }
        SizedPtr ticket;
        using (var legacy = RawRbfTestFile.CreateLegacy(_rbf1Path)) {
            ticket = legacy.Append(12, new byte[] { 6, 7, 8 }, new byte[] { 9, 10 }).Unwrap();
        }

        using (IRbfFile empty = RbfFile.OpenReadOnlyExisting(_rbf1EmptyPath)) {
            Assert.Equal(RbfFormat.Rbf1, empty.Format);
            Assert.Equal(4, empty.TailOffset);
        }

        using (IRbfFile reopened = RbfFile.OpenReadOnlyExisting(_rbf1Path)) {
            Assert.Equal(RbfFormat.Rbf1, reopened.Format);
            var info = reopened.ReadFrameInfo(ticket).Unwrap();
            Assert.Equal(ticket, info.Ticket);
            using var frame = reopened.ReadPooledFrame(ticket).Unwrap();
            Assert.Equal(new byte[] { 6, 7, 8, 9, 10 }, frame.PayloadAndMeta.ToArray());
        }
    }
}
