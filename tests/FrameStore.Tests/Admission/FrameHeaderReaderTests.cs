using Atelia.FrameStore.Internal.Admission;
using Atelia.FrameStore.Internal.Format;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Admission;

public sealed class FrameHeaderReaderTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"framestore-header-{Guid.NewGuid():N}.rbf");
    private static StoreIdentity Identity {
        get {
            Assert.True(StoreIdentity.TryRead(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), out var identity));
            return identity;
        }
    }

    public void Dispose() => File.Delete(_path);

    private static byte[] Header(uint id = 1) {
        byte[] payload = new byte[FileHeaderCodec.PayloadSize];
        Assert.True(FileHeaderCodec.TryWrite(Identity, id, payload));
        return payload;
    }

    [Fact]
    public void CheckedFirstFrameAllowsHeaderOnlyAndLaterUserTagZero() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        var ticket = file.Append(0, Header()).Unwrap();
        Assert.Equal(ticket, FrameHeaderReader.Check(file, Identity, 1).Unwrap());
        Assert.Equal(FileHeaderCodec.InitializationBoundary, file.TailOffset);
        var user = file.Append(0, new byte[] { 7 }).Unwrap();
        Assert.Equal(FileHeaderCodec.InitializationBoundary, user.Offset);
        Assert.Equal(ticket, FrameHeaderReader.Check(file, Identity, 1).Unwrap());
    }

    [Fact]
    public void BareFenceIsNotAnInitializedFile() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        Assert.Equal("FrameStore.InvalidHeader", FrameHeaderReader.Check(file, Identity, 1).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(23, 0, 0)]
    [InlineData(25, 0, 0)]
    [InlineData(24, 1, 0)]
    [InlineData(24, 0, 1)]
    [InlineData(4096, 0, 0)]
    public void InvalidFirstShapeCannotBeReplacedByLaterValidHeader(int payloadLength, int tag, int metaLength) {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        Assert.True(file.Append((uint)tag, new byte[payloadLength], new byte[metaLength]).IsSuccess);
        Assert.True(file.Append(0, Header()).IsSuccess);
        Assert.Equal("FrameStore.InvalidHeader", FrameHeaderReader.Check(file, Identity, 1).Error!.ErrorCode);
    }

    [Fact]
    public void ValidCrcDoesNotAuthorizeAdoptingHeaderIdentityOrFileId() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        Assert.True(file.Append(0, Header(2)).IsSuccess);
        Assert.Equal("FrameStore.InvalidHeader", FrameHeaderReader.Check(file, Identity, 1).Error!.ErrorCode);
        Assert.True(StoreIdentity.TryRead(Convert.FromHexString("1102030405060708090A0B0C0D0E0F10"), out var other));
        Assert.Equal("FrameStore.InvalidHeader", FrameHeaderReader.Check(file, other, 2).Error!.ErrorCode);
    }

    [Fact]
    public void FullPayloadCrcIsRequiredEvenWhenStructureScans() {
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            Assert.True(file.Append(0, Header()).IsSuccess);
            file.DurableFlush();
        }
        byte[] bytes = File.ReadAllBytes(_path);
        // Independent RBF3 fixture offset: inside the first frame's encoded payload.
        bytes[16] ^= 1;
        File.WriteAllBytes(_path, bytes);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        var scan = reader.ScanForward(showTombstone: true).GetEnumerator();
        Assert.True(scan.MoveNext());
        var checkedHeader = FrameHeaderReader.Check(reader, Identity, 1);
        Assert.True(checkedHeader.IsFailure);
        Assert.StartsWith("Rbf.", checkedHeader.Error.ErrorCode);
    }
}
