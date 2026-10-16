using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Tests;

public sealed class RbfFormatTests : IDisposable {
    private readonly string _rbf3Path = Path.Combine(Path.GetTempPath(), $"rbf-format-3-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        File.Delete(_rbf3Path);
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

}
