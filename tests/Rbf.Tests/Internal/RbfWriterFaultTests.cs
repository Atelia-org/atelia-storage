using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfWriterFaultTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-writer-fault-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        File.Delete(_path);
    }

    [Theory]
    [InlineData(false, RbfCacheMode.Off)]
    [InlineData(false, RbfCacheMode.Slots16)]
    [InlineData(true, RbfCacheMode.Off)]
    [InlineData(true, RbfCacheMode.Slots16)]
    public void FailedAppend_StopsOldAndNewReaders_AndAbortDoesNotClearFault(bool builder, RbfCacheMode cacheMode) {
        using var file = RbfFile.CreateNew(_path, cacheMode);
        var emptyMetaTicket = file.Append(11, new byte[] { 1, 2 }).Unwrap();
        file.Append(10, new byte[6000]).Unwrap(); // Keep the first cached page away from the append invalidation boundary.
        var metaTicket = file.Append(12, new byte[] { 3 }, new byte[] { 4 }).Unwrap();
        var emptyMetaInfo = file.ReadFrameInfo(emptyMetaTicket).Unwrap();
        var metaInfo = file.ReadFrameInfo(metaTicket).Unwrap();
        using var materialized = metaInfo.ReadPooledFrame().Unwrap();
        byte[] frameBuffer = new byte[emptyMetaTicket.Length];
        var materializedSpan = emptyMetaInfo.ReadFrame(frameBuffer).Unwrap();
        var forward = file.ScanForward().GetEnumerator();
        while (forward.MoveNext()) { }
        var reverse = file.ScanReverse().GetEnumerator();
        while (reverse.MoveNext()) { }

        long originalTail = file.TailOffset;
        RbfWriteInstrumentation.Current = new() { BeforeWrite = _ => 2 };
        if (builder) {
            var append = file.BeginAppend();
            var writer = append.PayloadAndMeta;
            writer.GetSpan(9000)[..9000].Fill(5);
            writer.Advance(9000);
            Assert.Throws<IOException>(() => { append.EndAppend(99); });
            RbfWriteInstrumentation.Current = null;
            Assert.Throws<InvalidOperationException>(() => { writer.GetSpan(); });
            Assert.Throws<InvalidOperationException>(() => { append.EndAppend(99); });
            append.Dispose();
        }
        else {
            Assert.Throws<IOException>(() => { file.Append(99, new byte[9000]); });
            RbfWriteInstrumentation.Current = null;
        }

        Assert.Equal(originalTail, file.TailOffset);
        Assert.Throws<InvalidOperationException>(() => { file.Append(13, new byte[1]); });
        Assert.Throws<InvalidOperationException>(() => { file.BeginAppend(); });
        Assert.Throws<InvalidOperationException>(() => file.DurableFlush());
        Assert.Throws<InvalidOperationException>(() => { file.ReadFrameInfo(metaTicket); });
        Assert.Throws<InvalidOperationException>(() => { file.ReadPooledFrame(metaTicket); });
        Assert.Throws<InvalidOperationException>(() => { file.ReadFrame(emptyMetaTicket, frameBuffer); });
        Assert.Throws<InvalidOperationException>(() => { emptyMetaInfo.ReadTailMeta(Span<byte>.Empty); });
        Assert.Throws<InvalidOperationException>(() => { emptyMetaInfo.ReadPooledTailMeta(); });
        Assert.Throws<InvalidOperationException>(() => { metaInfo.ReadTailMeta(new byte[1]); });
        Assert.Throws<InvalidOperationException>(() => { metaInfo.ReadPooledFrame(); });
        Assert.Throws<InvalidOperationException>(() => { emptyMetaInfo.ReadFrame(frameBuffer); });
        Assert.Throws<InvalidOperationException>(() => { file.ReadFrameInfoImmediatelyAfter(metaTicket); });
        Assert.Throws<InvalidOperationException>(() => { file.ScanForward(RbfScanBoundary.Empty); });

        bool forwardRefused = false;
        try { forward.MoveNext(); }
        catch (InvalidOperationException) { forwardRefused = true; }
        Assert.True(forwardRefused);
        bool reverseRefused = false;
        try { reverse.MoveNext(); }
        catch (InvalidOperationException) { reverseRefused = true; }
        Assert.True(reverseRefused);

        // Already materialized data and value properties retain their ordinary ownership.
        Assert.Equal(new byte[] { 3, 4 }, materialized.PayloadAndMeta.ToArray());
        Assert.Equal(new byte[] { 1, 2 }, materializedSpan.PayloadAndMeta.ToArray());
        Assert.Equal(11u, emptyMetaInfo.Tag);
        file.Dispose();
        using var raw = File.OpenHandle(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFlush_StopsExistingBuilderAndCachedFrameHandles(bool afterBarrier) {
        using var file = RbfFile.CreateNew(_path);
        var ticket = file.Append(7, new byte[] { 8 }).Unwrap();
        var info = file.ReadFrameInfo(ticket).Unwrap();
        using var append = file.BeginAppend();
        var writer = append.PayloadAndMeta;
        writer.GetSpan()[0] = 9;
        writer.Advance(1);
        RbfWriteInstrumentation.Current = new() {
            BeforeFlush = _ => { if (!afterBarrier) { throw new IOException("Before flush"); } },
            AfterFlush = _ => throw new IOException("After flush")
        };
        Assert.Throws<IOException>(() => file.DurableFlush());
        RbfWriteInstrumentation.Current = null;
        Assert.Throws<InvalidOperationException>(() => { writer.GetSpan(); });
        Assert.Throws<InvalidOperationException>(() => { append.EndAppend(8); });
        Assert.Throws<InvalidOperationException>(() => { info.ReadPooledTailMeta(); });
        append.Dispose();
        Assert.Throws<InvalidOperationException>(() => { file.Append(9, new byte[1]); });
        file.Dispose();
        using var reopened = RbfFile.OpenReadOnlyExisting(_path);
        using var frame = reopened.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal(new byte[] { 8 }, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void PreIoArgumentAndStateFailures_DoNotFaultHealthyWriter() {
        using var file = RbfFile.CreateNew(_path);
        Assert.True(file.Append(1, default, new byte[RbfFile.MaxTailMetaLength + 1]).IsFailure);
        using (var append = file.BeginAppend()) {
            Assert.Throws<InvalidOperationException>(() => { file.Append(2, default); });
            Assert.True(append.EndAppend(2, tailMetaLength: -1).IsFailure);
            append.EndAppend(2).Unwrap();
        }
        var ticket = file.Append(3, new byte[] { 10 }).Unwrap();
        file.DurableFlush();
        using var frame = file.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal(new byte[] { 10 }, frame.PayloadAndMeta.ToArray());
    }
}
