using System.Reflection;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfSizedAppendFailureTests {
    [Theory]
    [InlineData(0, RbfTailRecoveryAction.None)]
    [InlineData(2, RbfTailRecoveryAction.Truncated)]
    [InlineData(7, RbfTailRecoveryAction.Truncated)]
    [InlineData(27, RbfTailRecoveryAction.Truncated)]
    [InlineData(28, RbfTailRecoveryAction.CompletedTail)]
    [InlineData(31, RbfTailRecoveryAction.CompletedTail)]
    [InlineData(32, RbfTailRecoveryAction.CompletedTail)]
    [InlineData(35, RbfTailRecoveryAction.CompletedTail)]
    [InlineData(36, RbfTailRecoveryAction.None)]
    public void OutputFailure_DoesNotCompleteEarlyTicket_AndReopenUsesActualPrefix(int prefixLength, RbfTailRecoveryAction action) {
        using var fixture = new Rbf3WriterFixture();
        var original = fixture.File.Append(1, new byte[] { 9 }, ReadOnlySpan<byte>.Empty).Unwrap();
        var originalInfo = fixture.File.ReadFrameInfo(original).Unwrap();
        long tail = fixture.File.TailOffset;
        var builder = fixture.File.BeginAppend(3, 0, out var earlyTicket);
        Rbf3WriterOracle.WriteBuilder(builder, [5, 6, 7]);
        int writes = 0;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => {
                writes++;
                Assert.Equal(tail, request.Offset);
                Assert.Equal(36, request.RequestedBytes); // Independent RBF3 wire vector.
                return prefixLength;
            },
            AfterWrite = _ => {
                if (prefixLength == 36) { throw new IOException("Injected failure after complete output."); }
            }
        };
        try {
            Assert.Throws<IOException>(() => builder.EndAppend(2));
            Assert.Equal(tail, fixture.File.TailOffset);
            Assert.Equal(tail + prefixLength, RandomAccess.GetLength(fixture.Handle));
            Assert.Throws<InvalidOperationException>(() => fixture.File.ReadPooledFrame(earlyTicket));
            Assert.Throws<InvalidOperationException>(() => originalInfo.ReadPooledFrame());
            Assert.Throws<InvalidOperationException>(() => fixture.File.BeginAppend(0, 0, out _));
            builder.Dispose();
            Assert.Equal(1, writes); // Cancel/Dispose never retries publication.
        }
        finally { RbfWriteInstrumentation.Current = null; }

        fixture.File.Dispose();
        using var reopened = RbfFile.OpenExisting(fixture.Path, out var report, RbfCacheMode.Off);
        Assert.Equal(action, report.Action);
        Assert.Equal(tail + prefixLength, report.OriginalLength);
        Assert.Equal(prefixLength >= 28 ? tail + 36 : tail, reopened.TailOffset);
        using var oldFrame = reopened.ReadPooledFrame(original).Unwrap();
        Assert.Equal(new byte[] { 9 }, oldFrame.PayloadAndMeta.ToArray());
        if (prefixLength >= 28) {
            using var recovered = reopened.ReadPooledFrame(earlyTicket).Unwrap();
            Assert.Equal(new byte[] { 5, 6, 7 }, recovered.PayloadAndMeta.ToArray());
            Assert.Equal(2u, recovered.Tag);
            if (action == RbfTailRecoveryAction.CompletedTail) { Assert.Equal(earlyTicket, report.FrameTicket); }
        }
        else { Assert.IsType<RbfArgumentError>(reopened.ReadPooledFrame(earlyTicket).Error); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeAlignedStart_IsRejectedBeforeBuilderPublication(bool sized) {
        using var fixture = new Rbf3WriterFixture();
        var field = typeof(RbfFileImpl).GetField("_tailOffset", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(fixture.File, -4L); // Synthetic invalid owner state, no file holes or I/O.
        Assert.Throws<InvalidOperationException>(() => {
            if (sized) { fixture.File.BeginAppend(0, 0, out _); }
            else { fixture.File.BeginAppend(); }
        });
        Assert.Equal("RBF3"u8.ToArray(), fixture.ReadBytes());
        field.SetValue(fixture.File, 4L);
        using var next = fixture.File.BeginAppend(0, 0, out var ticket);
        Assert.Equal(ticket, next.EndAppend(3).Unwrap());
    }
}
