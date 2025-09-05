using System;
using System.Buffers;
using Xunit;

namespace Atelia.Data.Tests;

public class ChunkedReservableWriterStatsTests {
    private class DummyWriter : IBufferWriter<byte> {
        private byte[] _buffer = new byte[1024];
        private int _pos;
        public void Advance(int count) => _pos += count;
        public Memory<byte> GetMemory(int sizeHint = 0) {
            if (_pos + sizeHint > _buffer.Length) {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _pos + sizeHint));
            }

            return _buffer.AsMemory(_pos);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public ReadOnlyMemory<byte> Data => _buffer.AsMemory(0, _pos);
    }

    [Fact]
    public void LengthPropertiesReflectWritesAndReservations() {
        var inner = new DummyWriter();
        using var writer = new ChunkedReservableWriter(inner);
        Assert.Equal(0, writer.WrittenLength);
        Assert.Equal(0, writer.FlushedLength);
        Assert.Equal(0, writer.PendingLength);
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsPassthrough);

        // Direct write (passthrough)
        var s = writer.GetSpan(5);
        s[0] = 1;
        s[1] = 2;
        s[2] = 3;
        s[3] = 4;
        s[4] = 5;
        writer.Advance(5);
        Assert.Equal(5, writer.WrittenLength);
        Assert.Equal(5, writer.FlushedLength);
        Assert.Equal(0, writer.PendingLength);

        // Reservation
        var rspan = writer.ReserveSpan(4, out int token, "tag-X");
        rspan.Clear();
        Assert.Equal(9, writer.WrittenLength); // +4
        Assert.Equal(5, writer.FlushedLength); // 被阻塞
        Assert.Equal(4, writer.PendingLength);
        Assert.Equal(1, writer.PendingReservationCount);
        Assert.False(writer.IsPassthrough);
        Assert.NotNull(writer.BlockingReservationToken);

        // Fill & commit
        writer.Commit(token);
        Assert.Equal(9, writer.WrittenLength);
        Assert.Equal(9, writer.FlushedLength);
        Assert.Equal(0, writer.PendingLength);
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsPassthrough);
        Assert.Null(writer.BlockingReservationToken);
    }

    [Fact]
    public void FirstBlockingReservationTokenChangesWithCommitOrder() {
        var inner = new DummyWriter();
        using var writer = new ChunkedReservableWriter(inner);

        // Two reservations
        writer.ReserveSpan(2, out int t1, "A");
        writer.ReserveSpan(2, out int t2, "B");
        Assert.Equal(2, writer.PendingReservationCount);
        var first = writer.BlockingReservationToken;
        Assert.True(first == t1 || first == t2); // Implementation detail: token scramble; ensure token exists

        // Commit first (whichever is blocking) then second
        writer.Commit(first!.Value);
        Assert.Equal(1, writer.PendingReservationCount);
        Assert.NotNull(writer.BlockingReservationToken);
        Assert.NotEqual(first, writer.BlockingReservationToken); // 应该变化

        writer.Commit(writer.BlockingReservationToken!.Value);
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.Null(writer.BlockingReservationToken);
    }
}
