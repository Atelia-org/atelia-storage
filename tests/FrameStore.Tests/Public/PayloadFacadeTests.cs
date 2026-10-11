using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

public sealed class PayloadFacadeTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriterLength_CountsReservationsAndAdvanceOnceAcrossAliases(bool sized) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        using var builder = sized ? store.BeginAppend(3, 2, out _) : store.BeginAppend();
        var writer = builder.PayloadAndMeta;
        var copy = writer;
        Assert.Equal(0, writer.Length);
        Assert.Equal(0, builder.PayloadAndMeta.Length);

        new byte[] { 1, 2 }.AsSpan().CopyTo(writer.ReserveSpan(2, out int token));
        Assert.Equal(2, copy.Length);
        Assert.Equal(2, builder.PayloadAndMeta.Length);
        Assert.True(copy.TryGetReservedSpan(token, out var reserved));
        reserved[0] = 9;
        Assert.Equal(2, writer.Length);

        writer.GetMemory(128).Span[0] = 3;
        Assert.Equal(2, copy.Length); // Capacity and an outstanding borrow do not consume bytes.
        copy.Commit(token); // Committing a reservation during a borrow remains legal.
        Assert.Equal(2, writer.Length);
        Assert.Throws<InvalidOperationException>(() => builder.EndAppend(7, 2));
        copy.Advance(1);
        Assert.Equal(3, writer.Length);

        var repeated = builder.PayloadAndMeta;
        new byte[] { 4, 5 }.AsSpan().CopyTo(repeated.GetSpan(128));
        Assert.Equal(3, copy.Length);
        repeated.Advance(2);
        Assert.Equal(5, writer.Length);
        Assert.False(writer.TryGetReservedSpan(token, out _));
        Assert.Equal(5, repeated.Length);

        var address = builder.EndAppend(7, 2).Unwrap();
        using var read = store.ReadFrame(address).Unwrap();
        Assert.Equal(new byte[] { 9, 2, 3 }, read.Payload.ToArray());
        Assert.Equal(new byte[] { 4, 5 }, read.TailMeta.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriterLength_FirstReadAfterAdvanceUsesTheOriginalSharedOrigin(bool sized) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        using var builder = sized ? store.BeginAppend(2, 1, out _) : store.BeginAppend();
        var builderCopy = builder;
        var writer = builder.PayloadAndMeta;
        var writerCopy = writer;
        writer.GetSpan(2)[..2].Fill(1);
        writer.Advance(2);

        // No Length getter has run yet. A later Builder getter must not move the origin.
        var repeated = builderCopy.PayloadAndMeta;
        Assert.Equal(2, writerCopy.Length);
        Assert.Equal(2, repeated.Length);
        repeated.GetMemory(1).Span[0] = 2;
        repeated.Advance(1);
        Assert.Equal(3, writer.Length);
        Assert.Equal(3, builder.PayloadAndMeta.Length);
        builderCopy.EndAppend(8, 1).Unwrap();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriterLength_RejectionsAndCorrectableEndPreserveCountAndLease(bool sized) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        using var builder = sized ? store.BeginAppend(2, 1, out _) : store.BeginAppend();
        var writer = builder.PayloadAndMeta;
        Assert.Throws<ArgumentOutOfRangeException>(() => { writer.ReserveSpan(-1, out _); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { writer.ReserveSpan(0, out _); });
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(1));
        if (sized) {
            Assert.Throws<ArgumentOutOfRangeException>(() => { writer.ReserveSpan(4, out _); });
        }
        Assert.Equal(0, writer.Length);

        writer.ReserveSpan(1, out int token)[0] = 1;
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token + 1));
        Assert.False(writer.TryGetReservedSpan(token + 1, out _));
        Assert.Equal("Rbf.StateError", builder.EndAppend(9).Error!.ErrorCode);
        Assert.Equal(1, writer.Length);
        writer.Commit(token);
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));
        var rejected = sized ? builder.EndAppend(9) : builder.EndAppend(9, 2);
        Assert.Equal("Rbf.ArgumentError", rejected.Error!.ErrorCode);
        Assert.Equal(1, writer.Length);

        var memory = writer.GetMemory(32);
        memory.Span[0] = 2;
        memory.Span[1] = 3;
        Assert.Throws<InvalidOperationException>(() => writer.GetMemory(1));
        Assert.Throws<InvalidOperationException>(() => { writer.GetSpan(1); });
        Assert.Throws<InvalidOperationException>(() => { writer.ReserveSpan(1, out _); });
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        // In sized mode this exceeds the declaration; otherwise it exceeds the borrowed capacity.
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(sized ? 3 : memory.Length + 1));
        Assert.Equal(1, writer.Length);
        Assert.Throws<InvalidOperationException>(() => builder.EndAppend(9, 1));
        writer.Advance(2);
        Assert.Equal(3, writer.Length);
        Assert.Equal("Rbf.ArgumentError", builder.EndAppend(9, sized ? 0 : 4).Error!.ErrorCode);
        Assert.Equal(3, writer.Length);
        store.EnsureUsable();

        var address = builder.EndAppend(9, 1).Unwrap();
        Assert.Throws<InvalidOperationException>(() => { _ = writer.Length; });
        using var read = store.ReadFrame(address).Unwrap();
        Assert.Equal(new byte[] { 1, 2 }, read.Payload.ToArray());
        Assert.Equal(new byte[] { 3 }, read.TailMeta.ToArray());
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(false, 3, 0)]
    [InlineData(true, 3, 0)]
    [InlineData(false, 0, 2)]
    [InlineData(true, 0, 2)]
    [InlineData(false, 3, 2)]
    [InlineData(true, 3, 2)]
    public void PayloadSlices_ShareBufferForEveryShapeAndRejectAfterDispose(bool sized, int payloadLength, int metaLength) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        byte[] payload = Enumerable.Range(1, payloadLength).Select(i => (byte)i).ToArray();
        byte[] meta = Enumerable.Range(20, metaLength).Select(i => (byte)i).ToArray();
        using var builder = sized ? store.BeginAppend(payloadLength, metaLength, out _) : store.BeginAppend();
        var writer = builder.PayloadAndMeta;
        Assert.Equal(0, writer.Length);
        var memory = writer.GetMemory(payloadLength + metaLength + 128);
        payload.AsSpan().CopyTo(memory.Span);
        meta.AsSpan().CopyTo(memory.Span[payloadLength..]);
        Assert.Equal(0, writer.Length);
        writer.Advance(payloadLength + metaLength); // Includes Advance(0) for an empty frame.
        Assert.Equal(payloadLength + metaLength, writer.Length);
        var address = builder.EndAppend(10, metaLength).Unwrap();
        using var read = store.ReadFrame(address).Unwrap();
        var alias = read;
        store.Dispose();

        var bytes = read.PayloadAndMeta;
        Assert.Equal(payload, read.Payload.ToArray());
        Assert.Equal(meta, read.TailMeta.ToArray());
        if (payloadLength > 0) {
            Assert.True(bytes.Overlaps(read.Payload, out int offset));
            Assert.Equal(0, offset);
        }
        if (metaLength > 0) {
            Assert.True(bytes.Overlaps(read.TailMeta, out int offset));
            Assert.Equal(payloadLength, offset);
        }

        alias.Dispose();
        read.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = read.PayloadAndMeta.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = read.Payload.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = read.TailMeta.Length; });
        Assert.Equal(metaLength, read.TailMetaLength);
        Assert.Equal(address, read.Address);
    }

    [Fact]
    public void WriterLength_CancelledAliasesCannotObserveAReusedFileLease() {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        var cancelled = store.BeginAppend(1, 0, out var cancelledAddress);
        var alias = cancelled;
        var stale = cancelled.PayloadAndMeta;
        PublicStoreFixture.Write(cancelled, [1]);
        Assert.Equal(1, stale.Length);
        alias.Dispose();
        Assert.Throws<InvalidOperationException>(() => { _ = stale.Length; });

        using var replacement = store.BeginAppend(1, 0, out var replacementAddress);
        Assert.Equal(cancelledAddress, replacementAddress);
        Assert.Equal(0, replacement.PayloadAndMeta.Length);
        Assert.Throws<InvalidOperationException>(() => { _ = alias.PayloadAndMeta; });
        Assert.Throws<InvalidOperationException>(() => { _ = stale.Length; });
        alias.Dispose();
        PublicStoreFixture.Write(replacement, [2]);
        replacement.EndAppend(11).Unwrap();
        Assert.Throws<InvalidOperationException>(() => { _ = stale.Length; });
        using var read = store.ReadFrame(replacementAddress).Unwrap();
        Assert.Equal(new byte[] { 2 }, read.Payload.ToArray());
    }
}
