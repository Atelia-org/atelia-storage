using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfWriteInstrumentationTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-write-hooks-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        File.Delete(_path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialWrite_IsRealAndNeverReturnsSuccess(bool builder) {
        using (var file = RbfFile.CreateNew(_path)) {
            long originalTail = file.TailOffset;
            RbfWriteObservation? observed = null;
            RbfWriteInstrumentation.Current = new() {
                BeforeWrite = request => {
                    Assert.Equal(Path.GetFullPath(_path), request.Path);
                    Assert.Equal(originalTail, request.Offset);
                    return 2;
                },
                AfterWrite = observation => observed = observation
            };
            try {
                Assert.Throws<IOException>(() => Append(file, builder, new byte[9000]));
                Assert.True(observed.HasValue);
                Assert.Equal(2, observed.GetValueOrDefault().WrittenBytes);
                Assert.True(observed.GetValueOrDefault().RequestedBytes > 2);
                Assert.Equal(originalTail, file.TailOffset);
            }
            finally { RbfWriteInstrumentation.Current = null; }
        }
        Assert.Equal(6, new FileInfo(_path).Length); // 4B HeaderFence plus the actual 2B write.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteWrites_AreSequential_AndFlushCallbacksSurroundBarrier(bool builder) {
        using var file = RbfFile.CreateNew(_path);
        var observations = new List<RbfWriteObservation>();
        var phases = new List<string>();
        RbfWriteInstrumentation.Current = new() {
            AfterWrite = observation => observations.Add(observation),
            BeforeFlush = request => {
                Assert.Equal(Path.GetFullPath(_path), request.Path);
                phases.Add("before");
            },
            AfterFlush = request => phases.Add("after")
        };
        try {
            Append(file, builder, new byte[20000]);
            file.DurableFlush();
            Assert.NotEmpty(observations);
            long next = 4;
            foreach (var write in observations) {
                Assert.Equal(next, write.Offset);
                Assert.Equal(write.RequestedBytes, write.WrittenBytes);
                Assert.Equal(Path.GetFullPath(_path), write.Path);
                next += write.WrittenBytes;
            }
            Assert.Equal(file.TailOffset, next);
            Assert.Equal(new[] { "before", "after" }, phases);
        }
        finally { RbfWriteInstrumentation.Current = null; }
        int previousCount = observations.Count;
        Append(file, builder, new byte[3]);
        file.DurableFlush();
        Assert.Equal(previousCount, observations.Count);
    }

    [Fact]
    public async Task Hooks_AreIsolatedAcrossAsyncContexts_AndCanBeReset() {
        var parent = new RbfWriteHooks();
        RbfWriteInstrumentation.Current = parent;
        try {
            await Task.Run(() => {
                Assert.Same(parent, RbfWriteInstrumentation.Current);
                RbfWriteInstrumentation.Current = new();
                Assert.NotSame(parent, RbfWriteInstrumentation.Current);
                RbfWriteInstrumentation.Current = null;
                Assert.Null(RbfWriteInstrumentation.Current);
            });
            Assert.Same(parent, RbfWriteInstrumentation.Current);
        }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Null(RbfWriteInstrumentation.Current);
    }

    [Fact]
    public void InvalidPrefixLength_RejectsBeforeWriting() {
        using var file = RbfFile.CreateNew(_path);
        RbfWriteInstrumentation.Current = new() { BeforeWrite = request => request.RequestedBytes + 1 };
        try {
            Assert.Throws<ArgumentOutOfRangeException>(() => Append(file, false, new byte[3]));
            Assert.Equal(4, file.TailOffset);
        }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlushCallbackFailure_Propagates_WithoutRemovingCompleteBytes(bool afterBarrier) {
        using (var file = RbfFile.CreateNew(_path)) {
            Append(file, false, new byte[3]);
            int beforeCalls = 0;
            int afterCalls = 0;
            RbfWriteInstrumentation.Current = new() {
                BeforeFlush = _ => {
                    beforeCalls++;
                    if (!afterBarrier) { throw new IOException("Before barrier"); }
                },
                AfterFlush = _ => { afterCalls++; throw new IOException("After barrier"); }
            };
            try {
                Assert.Throws<IOException>(() => file.DurableFlush());
                Assert.Equal(1, beforeCalls);
                Assert.Equal(afterBarrier ? 1 : 0, afterCalls);
            }
            finally { RbfWriteInstrumentation.Current = null; }
        }
        using var reopened = RbfFile.OpenReadOnlyExisting(_path);
        var scan = reopened.ScanForward().GetEnumerator();
        Assert.True(scan.MoveNext());
        using var frame = reopened.ReadPooledFrame(scan.Current.Ticket).Unwrap();
        Assert.Equal(3, frame.PayloadAndMeta.Length);
        Assert.False(scan.MoveNext());
        Assert.Null(scan.TerminationError);
    }

    private static void Append(IRbfFile file, bool builder, byte[] payload) {
        if (!builder) {
            file.Append(123, payload).Unwrap();
            return;
        }
        using var frame = file.BeginAppend();
        payload.AsSpan().CopyTo(frame.PayloadAndMeta.GetSpan(payload.Length));
        frame.PayloadAndMeta.Advance(payload.Length);
        frame.EndAppend(123).Unwrap();
    }
}
