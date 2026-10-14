using System.Reflection;
using System.Text;
using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.ReadCache.Tests;

public sealed class ReadLoggerCleanupTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rbf-log-cleanup-{Guid.NewGuid():N}");

    public ReadLoggerCleanupTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Dispose_FlushFailure_ClosesOwnedStreamAndDoesNotRetry() {
        var logger = new ReadLogger();
        using var log = new FailingLog(logger, Path.Combine(_directory, "original.csv"));

        var error = Assert.Throws<IOException>(() => logger.Dispose());

        Assert.Same(log.Stream.Failure, error);
        Assert.Equal(1, log.Stream.FlushCalls);
        Assert.Equal(1, log.Stream.DisposeCalls);
        Assert.False(logger.NeedCacheSegments);
        logger.Dispose();
        Assert.Equal(1, log.Stream.FlushCalls);
        Assert.Equal(1, log.Stream.DisposeCalls);
    }

    [Fact]
    public void RbfFileDispose_LogFlushFailure_StillClosesDataHandleAndDoesNotRetry() {
        string dataPath = Path.Combine(_directory, "data.rbf");
        using var file = RbfFile.CreateNew(dataPath);
        SizedPtr ticket = file.Append(7, new byte[] { 1, 2, 3 }).Unwrap();
        var implementation = Assert.IsType<RbfFileImpl>(file);
        var reader = PrivateField<RandomAccessReader>(typeof(RbfFileImpl), implementation, "_reader");
        var logger = PrivateField<ReadLogger>(typeof(RandomAccessReader), reader, "_logger");
        using var log = new FailingLog(logger, Path.Combine(_directory, "original.csv"));

        var error = Assert.Throws<IOException>(() => file.Dispose());

        Assert.Same(log.Stream.Failure, error);
        Assert.True(reader.File.IsClosed);
        Assert.Equal(1, log.Stream.DisposeCalls);
        file.Dispose();
        Assert.Equal(1, log.Stream.FlushCalls);
        Assert.Equal(1, log.Stream.DisposeCalls);
        using var reopened = RbfFile.OpenExisting(dataPath, out var recovery);
        Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
        Assert.Equal(ticket, reopened.ReadFrameInfo(ticket).Unwrap().Ticket);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDisableOrSwitch_CanSetUpPreviousPathWithoutRetryingFailedStream(bool switchPath) {
        string previousPath = Path.Combine(_directory, "previous.csv");
        var logger = new ReadLogger();
        using var log = new FailingLog(logger, previousPath);
        logger.Setup(new ReadLogger.Params(switchPath ? Path.Combine(_directory, "other.csv") : null));

        var error = Assert.Throws<IOException>(() => logger.OnReadBegin(4, 8));

        Assert.Same(log.Stream.Failure, error);
        Assert.Equal(1, log.Stream.DisposeCalls);
        Assert.False(logger.NeedCacheSegments);

        // A failed close ends ownership of the previous writer; setting its path again must reopen it.
        logger.Setup(new ReadLogger.Params(previousPath, Append: false, FlushEvery: 1));
        try {
            logger.OnReadBegin(12, 16);
            logger.OnReadFinish(16);
            Assert.True(logger.NeedCacheSegments);
            logger.Dispose();
            Assert.True(File.Exists(previousPath));
            string[] lines = File.ReadAllLines(previousPath);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("#v1 tickFreq=", lines[0]);
            Assert.Equal("seq,offset,requested,bytesRead,rawCount,rawBytes,ioTicks,cacheTicks,hitmap", lines[1]);
            Assert.Equal(new[] { "12", "16", "16" }, lines[2].Split(',').Skip(1).Take(3).ToArray());
            Assert.Equal(1, log.Stream.FlushCalls);
            Assert.Equal(1, log.Stream.DisposeCalls);
        }
        finally { logger.Dispose(); }
    }

    [Fact]
    public void PublicReadLog_SwitchAndDisable_FlushCsvAndPreserveReads() {
        string dataPath = Path.Combine(_directory, "healthy.rbf");
        string firstPath = Path.Combine(_directory, "first.csv");
        string secondPath = Path.Combine(_directory, "second.csv");
        using var file = RbfFile.CreateNew(dataPath);
        SizedPtr ticket = file.Append(9, new byte[] { 4, 5, 6 }).Unwrap();

        file.SetupReadLog(firstPath);
        Assert.Equal(ticket, file.ReadFrameInfo(ticket).Unwrap().Ticket);
        file.SetupReadLog(secondPath);
        Assert.Equal(ticket, file.ReadFrameInfo(ticket).Unwrap().Ticket);
        file.SetupReadLog(null);
        Assert.Equal(ticket, file.ReadFrameInfo(ticket).Unwrap().Ticket);
        file.Dispose();

        string[] first = File.ReadAllLines(firstPath);
        string[] second = File.ReadAllLines(secondPath);
        Assert.Equal(3, first.Length);
        Assert.Equal(3, second.Length);
        Assert.StartsWith("#v1 tickFreq=", first[0]);
        Assert.StartsWith("#v1 tickFreq=", second[0]);
        Assert.Equal(first[1], second[1]);
        Assert.Equal("0", first[2].Split(',')[0]);
        Assert.Equal("1", second[2].Split(',')[0]);
        Assert.Contains("H", second[2].Split(',')[8]);
        using var reopened = RbfFile.OpenReadOnlyExisting(dataPath);
        Assert.Equal(ticket, reopened.ReadFrameInfo(ticket).Unwrap().Ticket);
    }

    private static T PrivateField<T>(Type declaringType, object instance, string name) =>
        Assert.IsAssignableFrom<T>(declaringType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));

    private sealed class FailingLog : IDisposable {
        internal FlushFailureStream Stream { get; } = new();
        private readonly StreamWriter _writer;

        internal FailingLog(ReadLogger logger, string path) {
            _writer = new StreamWriter(Stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: false);
            _writer.Write("buffered diagnostic data");
            // Isolated assembly-internal fault injection; production keeps its normal FileStream construction.
            typeof(ReadLogger).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(logger, _writer);
            typeof(ReadLogger).GetField("_logPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(logger, path);
        }

        public void Dispose() {
            // Keep an intentionally failing old implementation from leaking fixture-owned resources on a red run.
            Stream.FailFlush = false;
            _writer.Dispose();
        }
    }

    private sealed class FlushFailureStream : MemoryStream {
        internal IOException Failure { get; } = new("Injected log stream flush failure.");
        internal bool FailFlush { get; set; } = true;
        internal int FlushCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public override void Flush() {
            FlushCalls++;
            if (FailFlush) { throw Failure; }
            base.Flush();
        }

        protected override void Dispose(bool disposing) {
            if (disposing) { DisposeCalls++; }
            base.Dispose(disposing);
        }
    }

    public void Dispose() {
        // The test exclusively owns this freshly generated directory under TEMP.
        Directory.Delete(_directory, recursive: true);
    }
}
