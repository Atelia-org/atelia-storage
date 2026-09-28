using Atelia.Data;
using Atelia.RbfSegmentStore;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class RefMoveEndpointTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ref-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly RefId _refId = new(SizedPtr.Create(4, 124).Packed);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndpointsIgnoreCorruptInterveningMoveButExplicitReflogFails(bool rotate) {
        var options = Options(rotate);
        FrameAddress initAddress, middleAddress, lastAddress;
        using (var store = RefMoveStore.CreateNew(_root, _refId, options)) {
            initAddress = store.AppendMove(new(_refId, 1, 0, RefMoveOperation.Init, null, null, null, 0)).Unwrap();
            middleAddress = store.AppendMove(new(_refId, 2, 0, RefMoveOperation.Move, null, null, null, 0)).Unwrap();
            lastAddress = store.AppendMove(new(_refId, 3, 0, RefMoveOperation.Close, null, null, null, 0)).Unwrap();
        }
        string middlePath = SegmentPath(middleAddress.SegmentNumber);
        using (var stream = new FileStream(middlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.Position = middleAddress.Ticket.Offset + 4;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 1));
        }
        byte[] before = File.ReadAllBytes(middlePath);
        using (var store = RefMoveStore.OpenReadOnlyExisting(_root, _refId, options)) {
            var endpoints = store.ReadEndpoints().Unwrap();
            Assert.Equal(initAddress, endpoints.InitAddress);
            Assert.Equal(lastAddress, endpoints.LastAddress);
            Assert.Equal(1ul, endpoints.Init.MoveSequenceNumber);
            Assert.Equal(3ul, endpoints.Last.MoveSequenceNumber);
            Assert.Equal(RefMoveOperation.Close, endpoints.Last.Operation);
            Assert.True(store.ReadAllMoves().IsFailure);
        }
        Assert.Equal(before, File.ReadAllBytes(middlePath));
    }

    [Fact]
    public void PublishedEmptyActiveReadsOnlyTheImmediatePredecessor() {
        var options = Options(rotate: true);
        FrameAddress initAddress;
        using (var store = RefMoveStore.CreateNew(_root, _refId, options)) {
            initAddress = store.AppendMove(new(_refId, 1, 0, RefMoveOperation.Init, null, null, null, 0)).Unwrap();
        }
        using (var segments = RbfSegmentStore.RbfSegmentStore.OpenExisting(RefMoveStore.GetObjectPath(_root, _refId), options)) {
            using var next = segments.OpenActiveWriter();
            Assert.Equal(2u, next.SegmentNumber);
            Assert.Equal(4, next.File.TailOffset);
        }
        using var reopened = RefMoveStore.OpenReadOnlyExisting(_root, _refId, options);
        var endpoints = reopened.ReadEndpoints().Unwrap();
        Assert.Equal(initAddress, endpoints.InitAddress);
        Assert.Equal(initAddress, endpoints.LastAddress);
        Assert.Equal(endpoints.Init, endpoints.Last);
    }

    private static RbfSegmentStoreOptions Options(bool rotate) => new() {
        NewStoreLayout = RbfSegmentStoreLayout.Flat,
        SegmentSizeThresholdBytes = rotate ? 128 : 64 * 1024
    };

    private string SegmentPath(uint segment) => Path.Combine(RefMoveStore.GetObjectPath(_root, _refId), "segments", $"{segment:x8}.rbf");

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
