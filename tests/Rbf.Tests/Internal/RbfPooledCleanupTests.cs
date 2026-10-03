using System.Buffers;
using Atelia.Data;
using Atelia.Rbf.ReadCache;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfPooledCleanupTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PooledFrame_CrcFailure_ReturnsOnlyOnceWhenReturnThrows(bool useFrameInfo) {
        using var fixture = new Fixture();
        fixture.CorruptPayload();
        int rents = 0;
        fixture.Reader.BufferRentObserver = _ => rents++;
        var probe = new ReturnAfterAcceptThenThrowProbe();
        RbfFrameInfo info = fixture.Info;

        Exception? thrown = useFrameInfo
            ? Record.Exception(() => RbfReadImpl.ReadPooledFrame(fixture.Reader, info, probe.Invoke))
            : Record.Exception(() => RbfReadImpl.ReadPooledFrame(fixture.Reader, fixture.Ticket, probe.Invoke));

        Assert.Equal(1, rents);
        Assert.Equal(1, probe.Attempts);
        Assert.Same(probe.FirstFailure, thrown);
    }

    [Fact]
    public void PooledTailMeta_ShortRead_ReturnsOnlyOnceWhenReturnThrows() {
        using var fixture = new Fixture();
        long tailMetaOffset = fixture.Ticket.Offset + FrameLayout.PayloadOffset + fixture.Payload.Length;
        fixture.Reader.ShortReadOffset = tailMetaOffset;
        var rents = new List<int>();
        var reads = new List<(long Offset, int Length, bool Raw)>();
        fixture.Reader.BufferRentObserver = rents.Add;
        fixture.Reader.ReadObserver = (offset, length, raw) => reads.Add((offset, length, raw));
        var probe = new ReturnAfterAcceptThenThrowProbe();

        Exception? thrown = Record.Exception(() => fixture.Info.ReadPooledTailMeta(probe.Invoke));

        Assert.Equal(new[] { fixture.TailMeta.Length }, rents);
        Assert.Contains(reads, read => read.Offset == tailMetaOffset && read.Length == fixture.TailMeta.Length && !read.Raw);
        Assert.Equal(1, probe.Attempts);
        Assert.Same(probe.FirstFailure, thrown);
    }

    [Fact]
    public void PooledRead_ReadExceptionsReturnOnceAndPreserveReadException() {
        using var fixture = new Fixture();
        var frameReturn = new ReturnOnceProbe();
        var frameReadFailure = new IOException("Injected frame read failure.");
        fixture.Reader.ReadObserver = (_, _, raw) => { if (!raw) { throw frameReadFailure; } };

        Exception? frameThrown = Record.Exception(() => RbfReadImpl.ReadPooledFrame(
            fixture.Reader, fixture.Ticket, frameReturn.Invoke));

        Assert.Same(frameReadFailure, frameThrown);
        Assert.Equal(1, frameReturn.Attempts);

        var metaReturn = new ReturnOnceProbe();
        var metaReadFailure = new IOException("Injected TailMeta read failure.");
        fixture.Reader.ReadObserver = (_, _, raw) => { if (!raw) { throw metaReadFailure; } };

        Exception? metaThrown = Record.Exception(() => fixture.Info.ReadPooledTailMeta(
            metaReturn.Invoke));

        Assert.Same(metaReadFailure, metaThrown);
        Assert.Equal(1, metaReturn.Attempts);
    }

    [Fact]
    public void PooledSuccess_TransfersOwnershipWithoutCallingFailureReturn() {
        using var fixture = new Fixture();
        int failureReturns = 0;
        Action<byte[]> failIfCalled = _ => {
            failureReturns++;
            throw new InvalidOperationException("Successful ownership transfer used the failure return callback.");
        };

        using (var frame = RbfReadImpl.ReadPooledFrame(fixture.Reader, fixture.Ticket, failIfCalled).Unwrap()) {
            Assert.True(frame.PayloadAndMeta[..fixture.Payload.Length].SequenceEqual(fixture.Payload));
            Assert.True(frame.PayloadAndMeta[fixture.Payload.Length..].SequenceEqual(fixture.TailMeta));
        }
        using (var tailMeta = fixture.Info.ReadPooledTailMeta(failIfCalled).Unwrap()) {
            Assert.True(tailMeta.TailMeta.SequenceEqual(fixture.TailMeta));
        }

        Assert.Equal(0, failureReturns);
    }

    private sealed class Fixture : IDisposable {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-pooled-cleanup-{Guid.NewGuid():N}.rbf");

        internal readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7];
        internal readonly byte[] TailMeta = [21, 22, 23];
        internal readonly SizedPtr Ticket;
        internal readonly Microsoft.Win32.SafeHandles.SafeFileHandle Handle;
        internal readonly ControlledReader Reader;
        internal readonly RbfFrameInfo Info;

        internal Fixture() {
            using (IRbfFile file = RbfFile.CreateNew(_path)) {
                Ticket = file.Append(0x12345678, Payload, TailMeta).Unwrap();
            }

            Handle = File.OpenHandle(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Reader = new ControlledReader(Handle);
            Info = RbfReadImpl.ReadFrameInfo(Reader, Ticket).Unwrap();
        }

        internal void CorruptPayload() {
            Span<byte> value = stackalloc byte[1];
            long offset = Ticket.Offset + FrameLayout.PayloadOffset;
            Assert.Equal(1, RandomAccess.Read(Handle, value, offset));
            value[0] ^= 0x80;
            RandomAccess.Write(Handle, value, offset);
        }

        public void Dispose() {
            Reader.Dispose();
            Handle.Dispose();
            File.Delete(_path);
        }
    }

    private sealed class ControlledReader(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        : RandomAccessReader(handle, profile: RbfProfile.Rbf3) {
        internal long? ShortReadOffset { get; set; }

        protected override int ReadWithCache(long offset, Span<byte> buffer) {
            int read = base.ReadWithCache(offset, buffer);
            return offset == ShortReadOffset && read > 0 ? read - 1 : read;
        }
    }

    private sealed class ReturnAfterAcceptThenThrowProbe {
        internal readonly InvalidOperationException FirstFailure = new("Injected failure after Shared.Return accepted the buffer.");
        internal int Attempts { get; private set; }

        internal void Invoke(byte[] buffer) {
            Attempts++;
            if (Attempts == 1) {
                ArrayPool<byte>.Shared.Return(buffer);
                throw FirstFailure;
            }

            throw new DuplicateCleanupAttemptException();
        }
    }

    private sealed class ReturnOnceProbe {
        internal int Attempts { get; private set; }

        internal void Invoke(byte[] buffer) {
            Attempts++;
            if (Attempts == 1) { ArrayPool<byte>.Shared.Return(buffer); }
            else { throw new DuplicateCleanupAttemptException(); }
        }
    }

    private sealed class DuplicateCleanupAttemptException : Exception { }
}
