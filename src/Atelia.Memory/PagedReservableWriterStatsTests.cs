using System;
using System.Buffers;
using Xunit;

namespace Atelia.Memory.Tests;

public class PagedReservableWriterStatsTests
{
    private class DummyWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[1024];
        private int _pos;
        public void Advance(int count) => _pos += count;
        public Memory<byte> GetMemory(int sizeHint = 0) {
            if (_pos + sizeHint > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _pos + sizeHint));
            return _buffer.AsMemory(_pos);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public ReadOnlyMemory<byte> Data => _buffer.AsMemory(0, _pos);
    }

    [Fact]
    public void LengthPropertiesReflectWritesAndReservations()
    {
        var inner = new DummyWriter();
        using var writer = new PagedReservableWriter(inner);
        Assert.Equal(0, writer.TotalLogicalLength);
        Assert.Equal(0, writer.EmittedLength);
        Assert.Equal(0, writer.BufferedUnemittedLength);
        Assert.Equal(0, writer.OutstandingReservationCount);
        Assert.True(writer.IsPassthroughMode);

        // Direct write (passthrough)
        var s = writer.GetSpan(5);
        s[0]=1; s[1]=2; s[2]=3; s[3]=4; s[4]=5;
        writer.Advance(5);
        Assert.Equal(5, writer.TotalLogicalLength);
        Assert.Equal(5, writer.EmittedLength);
        Assert.Equal(0, writer.BufferedUnemittedLength);

        // Reservation
        var rspan = writer.ReserveSpan(4, out int token, "tag-X");
        rspan.Clear();
        Assert.Equal(9, writer.TotalLogicalLength); // +4
        Assert.Equal(5, writer.EmittedLength); // 被阻塞
        Assert.Equal(4, writer.BufferedUnemittedLength);
        Assert.Equal(1, writer.OutstandingReservationCount);
        Assert.False(writer.IsPassthroughMode);
        Assert.NotNull(writer.FirstBlockingReservationToken);

        // Fill & commit
        writer.Commit(token);
        Assert.Equal(9, writer.TotalLogicalLength);
        Assert.Equal(9, writer.EmittedLength);
        Assert.Equal(0, writer.BufferedUnemittedLength);
        Assert.Equal(0, writer.OutstandingReservationCount);
        Assert.True(writer.IsPassthroughMode);
        Assert.Null(writer.FirstBlockingReservationToken);
    }

    [Fact]
    public void FirstBlockingReservationTokenChangesWithCommitOrder()
    {
        var inner = new DummyWriter();
        using var writer = new PagedReservableWriter(inner);

        // Two reservations
        writer.ReserveSpan(2, out int t1, "A");
        writer.ReserveSpan(2, out int t2, "B");
        Assert.Equal(2, writer.OutstandingReservationCount);
        var first = writer.FirstBlockingReservationToken;
        Assert.True(first == t1 || first == t2); // Implementation detail: token scramble; ensure token exists

        // Commit first (whichever is blocking) then second
        writer.Commit(first!.Value);
        Assert.Equal(1, writer.OutstandingReservationCount);
        Assert.NotNull(writer.FirstBlockingReservationToken);
        Assert.NotEqual(first, writer.FirstBlockingReservationToken); // 应该变化

        writer.Commit(writer.FirstBlockingReservationToken!.Value);
        Assert.Equal(0, writer.OutstandingReservationCount);
        Assert.Null(writer.FirstBlockingReservationToken);
    }
}
