using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Platform;
using Atelia.VersionStore.Internal.Storage;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore.Tests.Storage;

/// <summary>真实句柄上的受控失败窗口，不声称制造了 OS flush/close 故障或 syscall 内强杀。</summary>
public sealed class FactoryFailureWindowTests {
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void CompleteGateSurvivesFailedCreateAndCanBeOpened(bool failFlush, bool failClose, bool failLockClose) {
        using var fixture = new StorageFixture();
        var operations = new WindowOperations {
            FlushError = failFlush ? new IOException("controlled gate flush failure") : null,
            CloseError = failClose ? new IOException("controlled gate close failure") : null,
            LockCloseError = failLockClose ? new IOException("controlled lock close failure") : null
        };
        VersionStore? owner = null;
        Exception? error = Record.Exception(() => owner = VersionStoreFactory.Create(fixture.Root, fixture.Data, 256, operations));
        Assert.Null(owner);
        AssertErrors(error, operations.FlushError, operations.CloseError, operations.LockCloseError);
        Assert.Equal(1, operations.GateStream!.DisposeCount);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        Assert.Equal("lock-close", operations.Events[^1]);
        byte[] gateBytes = File.ReadAllBytes(fixture.GatePath);
        Assert.Equal(44, gateBytes.Length);
        Assert.True(FormatGateCodec.TryRead(gateBytes, out FormatGate gate));
        Assert.True(File.Exists(fixture.LockPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.RefsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.CreatingPath));
        fixture.Data.EnsureUsable();

        using (var reopened = VersionStore.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(gate.VersionStoreId, ReadIdentity(reopened));
            Assert.Equal(256u, reopened.ReservedRefIdUpperBound);
            Assert.Empty(reopened.ListRefs().Unwrap());
        }
        using var readOnly = VersionStore.OpenReadOnly(fixture.Root, fixture.Data);
        Assert.Equal(gate.VersionStoreId, ReadIdentity(readOnly));
        Assert.Equal(gateBytes, File.ReadAllBytes(fixture.GatePath));
        Assert.Throws<InvalidDataException>(() => VersionStore.Create(fixture.Root, fixture.Data));
    }

    [Fact]
    public void PartialGateFailureLeavesLayoutAndIsNeverResumed() {
        using var fixture = new StorageFixture();
        var primary = new IOException("controlled partial gate write");
        var operations = new WindowOperations { WriteError = primary };
        Assert.Same(primary, Record.Exception(() => VersionStoreFactory.Create(fixture.Root, fixture.Data, 0, operations)));
        Assert.Equal(7, new FileInfo(fixture.GatePath).Length);
        Assert.True(Directory.Exists(fixture.RefsPath));
        Assert.True(Directory.Exists(fixture.CreatingPath));
        Assert.Throws<InvalidDataException>(() => VersionStore.Open(fixture.Root, fixture.Data));
        Assert.Throws<InvalidDataException>(() => VersionStore.Create(fixture.Root, fixture.Data));
        Assert.Equal(7, new FileInfo(fixture.GatePath).Length);
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void HandoffFailureLeavesCompleteGateAndReleasesLock() {
        using var fixture = new StorageFixture();
        var primary = new IOException("controlled owner handoff failure");
        var operations = new WindowOperations { HandoffError = primary };
        Assert.Same(primary, Record.Exception(() => VersionStoreFactory.Create(fixture.Root, fixture.Data, 17, operations)));
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        Assert.True(FormatGateCodec.TryRead(File.ReadAllBytes(fixture.GatePath), out FormatGate gate));
        using var reopened = VersionStore.Open(fixture.Root, fixture.Data);
        Assert.Equal(gate.VersionStoreId, ReadIdentity(reopened));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GateReadAndCloseFailurePrecedeFormalDiscoveryAndPendingCancellation(bool readOnly, bool failRead) {
        using var fixture = new StorageFixture();
        fixture.Create();
        fixture.SeedFormal(1);
        byte[] formal = File.ReadAllBytes(fixture.RefPath(1));
        using (var stream = new FileStream(fixture.RefPath(1), FileMode.Append)) { stream.WriteByte(1); }
        byte[] recoverable = File.ReadAllBytes(fixture.RefPath(1));
        File.WriteAllBytes(fixture.PendingPath, [7, 8, 9]);
        var operations = new WindowOperations {
            ReadError = failRead ? new IOException("controlled gate read failure") : null,
            CloseError = new IOException("controlled gate close failure")
        };
        AssertErrors(Record.Exception(() => VersionStoreFactory.Open(fixture.Root, fixture.Data, readOnly, operations)),
            operations.ReadError, operations.CloseError);
        Assert.Equal(1, operations.GateStream!.DisposeCount);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        Assert.DoesNotContain("enumerate:refs", operations.Events);
        Assert.DoesNotContain("enumerate:creating", operations.Events);
        Assert.DoesNotContain("pending-delete", operations.Events);
        Assert.Equal("lock-close", operations.Events[^1]);
        Assert.Equal(recoverable, File.ReadAllBytes(fixture.RefPath(1)));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(fixture.PendingPath));
        fixture.Data.EnsureUsable();

        using (var reopened = VersionStore.Open(fixture.Root, fixture.Data)) {
            Assert.False(File.Exists(fixture.PendingPath));
            // Open is lazy: the malformed tail is still present until the requested ref is opened.
            Assert.Equal(recoverable, File.ReadAllBytes(fixture.RefPath(1)));
            Assert.Equal(new byte[] { 1 }, reopened.ReadRef(1).Unwrap().Payload.ToArray());
        }
        Assert.Equal(formal, File.ReadAllBytes(fixture.RefPath(1)));
    }

    [Fact]
    public void WrongDataBindingClosesGateBeforeAnyDirectoryDiscoveryOrCancellation() {
        using var fixture = new StorageFixture();
        fixture.Create();
        File.WriteAllBytes(fixture.PendingPath, [4]);
        using var otherData = DataStore.Create(Path.Combine(fixture.BasePath, "other-data"));
        var operations = new WindowOperations();
        Assert.Throws<InvalidDataException>(() => VersionStoreFactory.Open(fixture.Root, otherData, false, operations));
        Assert.Equal(1, operations.GateStream!.DisposeCount);
        Assert.DoesNotContain("enumerate:refs", operations.Events);
        Assert.DoesNotContain("enumerate:creating", operations.Events);
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(fixture.PendingPath));
        fixture.Data.EnsureUsable();
        otherData.EnsureUsable();
    }

    [Fact]
    public void FreshInputIsRecheckedAfterAnotherCreateCompletesInPrelockWindow() {
        using var fixture = new StorageFixture();
        Directory.CreateDirectory(fixture.Root);
        byte[]? winnerId = null;
        var operations = new WindowOperations {
            BeforeLock = root => { using var winner = VersionStore.Create(root, fixture.Data, 8); winnerId = winner.VersionStoreId.ToArray(); }
        };
        Assert.Throws<InvalidDataException>(() => VersionStoreFactory.Create(fixture.Root, fixture.Data, 9, operations));
        Assert.Null(operations.GateStream);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        using var reopened = VersionStore.Open(fixture.Root, fixture.Data);
        Assert.Equal(winnerId, reopened.VersionStoreId.ToArray());
        Assert.Equal(8u, reopened.ReservedRefIdUpperBound);
    }

    [Fact]
    public void FormalEnumerationDisposeFailurePrecedesPendingCancellationAndLockIsLast() {
        using var fixture = new StorageFixture();
        fixture.Create();
        File.WriteAllBytes(fixture.RefPath(uint.MaxValue), [0]);
        File.WriteAllBytes(fixture.PendingPath, [1]);
        var primary = new IOException("controlled refs enumeration failure");
        var cleanup = new IOException("controlled refs enumerator close failure");
        var operations = new WindowOperations { RefEnumerationError = primary, RefEnumerationCloseError = cleanup };
        AssertErrors(Record.Exception(() => VersionStoreFactory.Open(fixture.Root, fixture.Data, false, operations)), primary, cleanup);
        Assert.Equal(1, operations.RefEnumerator!.DisposeCount);
        Assert.True(File.Exists(fixture.PendingPath));
        Assert.DoesNotContain("enumerate:creating", operations.Events);
        Assert.Equal("lock-close", operations.Events[^1]);
        using var reopened = VersionStore.Open(fixture.Root, fixture.Data);
        Assert.Equal("VersionStore.RefIdExhausted", reopened.CreateRef([2], out var outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
    }

    [Fact]
    public void DeleteFailureRejectsOpenAndPreservesQualifiedPending() {
        using var fixture = new StorageFixture();
        fixture.Create();
        File.WriteAllBytes(fixture.PendingPath, [1, 2, 3]);
        var primary = new IOException("controlled pending deletion failure");
        var operations = new WindowOperations { DeleteError = primary };
        Assert.Same(primary, Record.Exception(() => VersionStoreFactory.Open(fixture.Root, fixture.Data, false, operations)));
        Assert.True(operations.Events.IndexOf("gate-close") < operations.Events.IndexOf("enumerate:refs"));
        Assert.True(operations.Events.IndexOf("enumerate:refs") < operations.Events.IndexOf("enumerate:creating"));
        Assert.True(operations.Events.IndexOf("enumerate:creating") < operations.Events.IndexOf("pending-delete"));
        Assert.Equal("lock-close", operations.Events[^1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.PendingPath));
        using var reopened = VersionStore.OpenReadOnly(fixture.Root, fixture.Data);
    }

    private static StoreIdentity ReadIdentity(VersionStore store) {
        Assert.True(StoreIdentity.TryRead(store.VersionStoreId, out StoreIdentity identity));
        return identity;
    }

    private static void AssertErrors(Exception? observed, params Exception?[] expected) {
        var actual = new List<Exception>();
        Assert.NotNull(observed);
        Flatten(observed!, actual);
        Assert.Equal(expected.OfType<Exception>(), actual);

        static void Flatten(Exception error, List<Exception> target) {
            if (error is AggregateException aggregate) {
                foreach (Exception inner in aggregate.InnerExceptions) { Flatten(inner, target); }
            }
            else { target.Add(error); }
        }
    }

    private sealed class WindowOperations : VersionStoreFactoryOperations {
        internal List<string> Events { get; } = [];
        internal Action<string>? BeforeLock { get; init; }
        internal Exception? FlushError { get; init; }
        internal Exception? CloseError { get; init; }
        internal Exception? ReadError { get; init; }
        internal Exception? WriteError { get; init; }
        internal Exception? LockCloseError { get; init; }
        internal Exception? HandoffError { get; init; }
        internal Exception? RefEnumerationError { get; init; }
        internal Exception? RefEnumerationCloseError { get; init; }
        internal Exception? DeleteError { get; init; }
        internal ControlledGateStream? GateStream { get; private set; }
        internal ControlledLock? OwnerLock { get; private set; }
        internal ControlledEnumerator? RefEnumerator { get; private set; }

        internal override void BeforeCreateOwnerLock(string admittedRoot) => BeforeLock?.Invoke(admittedRoot);

        internal override IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) {
            Events.Add("lock-acquire");
            return OwnerLock = new ControlledLock(VersionStorePlatform.AcquireOwnerLock(root, create, readOnly), Events, LockCloseError);
        }

        internal override FileStream CreateGate(string path) => GateStream =
            new ControlledGateStream(path, write: true, Events, ReadError, WriteError, FlushError, CloseError);

        internal override FileStream ReadGate(string path) => GateStream =
            new ControlledGateStream(path, write: false, Events, ReadError, WriteError, FlushError, CloseError);

        internal override IEnumerator<string> EnumerateDirectory(string directory) {
            string name = Path.GetFileName(directory);
            Events.Add("enumerate:" + name);
            var entries = base.EnumerateDirectory(directory);
            if (name == VersionStorePaths.RefsDirectoryName) {
                return RefEnumerator = new ControlledEnumerator(entries, Events, RefEnumerationError, RefEnumerationCloseError);
            }
            return entries;
        }

        internal override void DeletePending(string path) {
            Events.Add("pending-delete");
            if (DeleteError is not null) { throw DeleteError; }
            base.DeletePending(path);
        }

        internal override void BeforeOwnerHandoff(string admittedRoot) {
            if (HandoffError is not null) { throw HandoffError; }
        }
    }

    private sealed class ControlledGateStream : FileStream {
        private readonly List<string> _events;
        private readonly Exception? _readError;
        private readonly Exception? _writeError;
        private readonly Exception? _flushError;
        private readonly Exception? _closeError;
        internal int DisposeCount { get; private set; }

        internal ControlledGateStream(string path, bool write, List<string> events, Exception? readError,
            Exception? writeError, Exception? flushError, Exception? closeError)
            : base(path, write ? FileMode.CreateNew : FileMode.Open, write ? FileAccess.Write : FileAccess.Read,
                write ? FileShare.None : FileShare.Read) {
            _events = events;
            _readError = readError;
            _writeError = writeError;
            _flushError = flushError;
            _closeError = closeError;
            events.Add(write ? "gate-create" : "gate-read");
        }

        public override int Read(Span<byte> buffer) {
            if (_readError is not null) { throw _readError; }
            return base.Read(buffer);
        }

        public override void Write(ReadOnlySpan<byte> buffer) {
            if (_writeError is not null) { base.Write(buffer[..7]); throw _writeError; }
            base.Write(buffer);
        }

        public override void Flush(bool flushToDisk) {
            base.Flush(flushToDisk);
            _events.Add("gate-flush");
            if (_flushError is not null) { throw _flushError; }
        }

        protected override void Dispose(bool disposing) {
            if (!disposing) { base.Dispose(disposing); return; }
            DisposeCount++;
            base.Dispose(disposing);
            _events.Add("gate-close");
            if (_closeError is not null) { throw _closeError; }
        }
    }

    private sealed class ControlledLock(IDisposable inner, List<string> events, Exception? closeError) : IDisposable {
        internal int DisposeCount { get; private set; }
        public void Dispose() {
            DisposeCount++;
            inner.Dispose();
            events.Add("lock-close");
            if (closeError is not null) { throw closeError; }
        }
    }

    private sealed class ControlledEnumerator(IEnumerator<string> inner, List<string> events,
        Exception? moveError, Exception? closeError) : IEnumerator<string> {
        private int _moves;
        internal int DisposeCount { get; private set; }
        public string Current => inner.Current;
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() {
            if (++_moves == 2 && moveError is not null) { throw moveError; }
            return inner.MoveNext();
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() {
            DisposeCount++;
            inner.Dispose();
            events.Add("refs-enumerator-close");
            if (closeError is not null) { throw closeError; }
        }
    }
}
