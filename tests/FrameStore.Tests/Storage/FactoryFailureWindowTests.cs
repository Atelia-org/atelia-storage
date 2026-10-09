using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Runtime;
using Atelia.FrameStore.Internal.Storage;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Storage;

/// <summary>真实目录和句柄上的受控失败；不声称制造了 OS flush/close 故障或 syscall 中强杀。</summary>
public sealed class FactoryFailureWindowTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrameStore-factory-window-" + Guid.NewGuid().ToString("N"));

    private string GatePath => Path.Combine(_root, DirectoryFrameStoreFiles.GateFileName);
    private string ActivePath => Path.Combine(_root, FrameStorePaths.GetActiveRelativePath(1));
    private string PrivatePath => Path.Combine(_root, DirectoryFrameStoreFiles.CreatingDirectoryName, FrameStorePaths.GetFileName(2));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void CreateRejectsAfterPhysicalGateFlushOrCloseFailureAndLaterOpenUsesSameGate(
        bool failFlush, bool failClose, bool failLockClose) {
        var operations = new WindowOperations(_root) {
            GateFlushError = failFlush ? new IOException("controlled gate flush failure") : null,
            GateCloseError = failClose ? new IOException("controlled gate close failure") : null,
            LockCloseError = failLockClose ? new IOException("controlled owner lock close failure") : null
        };
        FrameStore? owner = null;
        Exception? error = Record.Exception(() => owner = FrameStoreFactory.Create(_root, RotationThreshold.DefaultBytes, operations));

        Assert.Null(owner);
        AssertErrors(error, operations.GateFlushError, operations.GateCloseError, operations.LockCloseError);
        var stream = Assert.IsType<ControlledFileStream>(operations.GateStream);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        // The wrapper drains the managed buffer and reads the same OS handle BEFORE the injected failure.
        byte[] physicalGate = Assert.IsType<byte[]>(stream.PhysicalGateBeforeFailure);
        Assert.Equal(FormatGateCodec.EncodedSize, physicalGate.Length);
        Assert.True(FormatGateCodec.TryRead(physicalGate, out var identity));
        Assert.Equal(physicalGate, File.ReadAllBytes(GatePath));
        Assert.Equal(new[] { "lock-acquire", "gate-create", "gate-physical", "gate-flush", "gate-close", "lock-close" }, operations.Events);
        Assert.True(File.Exists(Path.Combine(_root, DirectoryFrameStoreFiles.OwnerLockFileName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, FrameStorePaths.ActiveDirectoryName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, FrameStorePaths.ArchiveDirectoryName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, DirectoryFrameStoreFiles.CreatingDirectoryName)));

        using (var reopened = FrameStore.Open(_root)) {
            Assert.True(StoreIdentity.TryRead(reopened.StoreId, out var reopenedIdentity));
            Assert.Equal(identity, reopenedIdentity);
            Assert.Empty(reopened.RecoveryReports);
        }
        using var readOnly = FrameStore.OpenReadOnly(_root);
        Assert.True(StoreIdentity.TryRead(readOnly.StoreId, out var readOnlyIdentity));
        Assert.Equal(identity, readOnlyIdentity);
        Assert.Equal(physicalGate, File.ReadAllBytes(GatePath));
        Assert.Throws<InvalidDataException>(() => FrameStore.Create(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateCandidateRequiredCloseFailurePreservesCandidateAndPrecedesActiveRecovery(bool failLockClose) {
        var fixture = CreateRecoverableFixture();
        var before = CaptureFiles();
        var operations = new WindowOperations(_root) {
            PrivateCloseError = new IOException("controlled private close failure"),
            LockCloseError = failLockClose ? new IOException("controlled owner lock close failure") : null
        };
        FrameStore? owner = null;
        Exception? error = Record.Exception(() => owner = FrameStoreFactory.Open(_root,
            RotationThreshold.DefaultBytes, readOnly: false, operations));

        Assert.Null(owner);
        AssertErrors(error, operations.PrivateCloseError, operations.LockCloseError);
        var stream = Assert.IsType<ControlledFileStream>(operations.PrivateStream);
        Assert.Equal(FileHeaderCodec.InitializationBoundary, stream.BytesRead);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        Assert.Equal(new[] { "lock-acquire", "gate-read", "gate-close", "private-read", "private-close", "lock-close" }, operations.Events);
        AssertFilesEqual(before);
        Assert.True(File.Exists(PrivatePath));
        AssertRecoveredByNormalOpen(fixture);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GateReadOrRequiredCloseFailureRejectsBothModesBeforePrivateCleanupAndActiveRecovery(
        bool readOnly, bool failRead) {
        var fixture = CreateRecoverableFixture();
        var before = CaptureFiles();
        var operations = new WindowOperations(_root) {
            GateReadError = failRead ? new IOException("controlled gate read failure") : null,
            GateCloseError = new IOException("controlled gate close failure")
        };
        FrameStore? owner = null;
        Exception? error = Record.Exception(() => owner = FrameStoreFactory.Open(_root,
            RotationThreshold.DefaultBytes, readOnly, operations));

        Assert.Null(owner);
        AssertErrors(error, operations.GateReadError, operations.GateCloseError);
        var stream = Assert.IsType<ControlledFileStream>(operations.GateStream);
        Assert.Equal(failRead ? 0L : FormatGateCodec.EncodedSize, stream.BytesRead);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Null(operations.PrivateStream);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        Assert.Equal(new[] { "lock-acquire", "gate-read", "gate-close", "lock-close" }, operations.Events);
        AssertFilesEqual(before);
        AssertRecoveredByNormalOpen(fixture);
    }

    [Fact]
    public async Task CreateRechecksAfterPreflightWhileAnotherPublicCreateCompletes() {
        Directory.CreateDirectory(_root);
        using var preflightReached = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var operations = new WindowOperations(_root) {
            BeforeLock = _ => {
                preflightReached.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(20))) { throw new TimeoutException("Create preflight handshake timed out."); }
            }
        };
        FrameStore? owner = null;
        Task<Exception?> attempt = Task.Run<Exception?>(() => Record.Exception(() => owner =
            FrameStoreFactory.Create(_root, RotationThreshold.DefaultBytes, operations)));
        byte[] identity;
        FrameAddress address;
        Dictionary<string, byte[]> before;
        Exception? error = null;
        try {
            Assert.True(preflightReached.Wait(TimeSpan.FromSeconds(10)), "Create must reach the explicit pre-lock handshake.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
            using (var winner = FrameStore.Create(_root)) {
                identity = winner.StoreId.ToArray();
                address = winner.Append(17, "winner"u8).Unwrap();
                winner.ConfirmDurable();
            }
            before = CaptureFiles();
        }
        finally {
            resume.Set();
            error = await attempt.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.IsType<InvalidDataException>(error);
        Assert.Null(owner);
        Assert.Null(operations.GateStream);
        Assert.Equal(1, operations.OwnerLock!.DisposeCount);
        Assert.Equal(new[] { "preflight", "lock-acquire", "lock-close" }, operations.Events);
        AssertFilesEqual(before);
        using var reopened = FrameStore.Open(_root);
        Assert.Equal(identity, reopened.StoreId.ToArray());
        using var frame = reopened.ReadFrame(address).Unwrap();
        Assert.Equal("winner"u8.ToArray(), frame.PayloadAndMeta.ToArray());
    }

    private (byte[] Identity, FrameAddress Address, byte[] CompleteActive) CreateRecoverableFixture() {
        byte[] identityBytes;
        FrameAddress address;
        using (var store = FrameStore.Create(_root)) {
            identityBytes = store.StoreId.ToArray();
            address = store.Append(7, "retained"u8).Unwrap();
            store.ConfirmDurable();
        }
        byte[] completeActive = File.ReadAllBytes(ActivePath);
        using (var stream = new FileStream(ActivePath, FileMode.Append, FileAccess.Write)) { stream.WriteByte(1); }
        Assert.True(StoreIdentity.TryRead(identityBytes, out var identity));
        byte[] header = new byte[FileHeaderCodec.PayloadSize];
        Assert.True(FileHeaderCodec.TryWrite(identity, 2, header));
        using (var privateFile = RbfFile.CreateNew(PrivatePath, RbfCacheMode.Off)) {
            privateFile.Append(0, header).Unwrap();
            privateFile.DurableFlush();
        }
        return (identityBytes, address, completeActive);
    }

    private void AssertRecoveredByNormalOpen((byte[] Identity, FrameAddress Address, byte[] CompleteActive) fixture) {
        using (var reopened = FrameStore.Open(_root)) {
            Assert.Equal(fixture.Identity, reopened.StoreId.ToArray());
            Assert.NotEqual(RbfTailRecoveryAction.None, reopened.RecoveryReports[1].Action);
            using var frame = reopened.ReadFrame(fixture.Address).Unwrap();
            Assert.Equal("retained"u8.ToArray(), frame.PayloadAndMeta.ToArray());
            Assert.False(File.Exists(PrivatePath));
        }
        Assert.Equal(fixture.CompleteActive, File.ReadAllBytes(ActivePath));
    }

    private Dictionary<string, byte[]> CaptureFiles() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);

    private void AssertFilesEqual(Dictionary<string, byte[]> expected) {
        var actual = CaptureFiles();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected) { Assert.Equal(bytes, actual[path]); }
    }

    private static void AssertErrors(Exception? observed, params Exception?[] expected) {
        Assert.NotNull(observed);
        var actual = new List<Exception>();
        AddInOrder(observed, actual);
        Assert.Equal(expected.OfType<Exception>(), actual);

        static void AddInOrder(Exception error, List<Exception> destination) {
            if (error is AggregateException aggregate) {
                foreach (Exception inner in aggregate.InnerExceptions) { AddInOrder(inner, destination); }
            }
            else { destination.Add(error); }
        }
    }

    private static void AssertOwnerStillHeld(string root) {
        Exception? error = Record.Exception(() => { using var competitor = FrameStore.Open(root); });
        Assert.IsAssignableFrom<IOException>(error);
    }

    private sealed class WindowOperations(string root) : FrameStoreFactoryOperations {
        internal List<string> Events { get; } = [];
        internal Action<string>? BeforeLock { get; init; }
        internal IOException? GateFlushError { get; init; }
        internal IOException? GateReadError { get; init; }
        internal IOException? GateCloseError { get; init; }
        internal IOException? PrivateCloseError { get; init; }
        internal IOException? LockCloseError { get; init; }
        internal ControlledFileStream? GateStream { get; private set; }
        internal ControlledFileStream? PrivateStream { get; private set; }
        internal TrackingOwnerLock? OwnerLock { get; private set; }

        internal override void BeforeCreateOwnerLock(string admittedRoot) {
            if (BeforeLock is null) { return; }
            Events.Add("preflight");
            BeforeLock(admittedRoot);
        }

        internal override IDisposable AcquireOwnerLock(string admittedRoot, bool create, bool readOnly) {
            IDisposable handle = base.AcquireOwnerLock(admittedRoot, create, readOnly);
            Events.Add("lock-acquire");
            OwnerLock = new TrackingOwnerLock(handle, root, Events, LockCloseError);
            return OwnerLock;
        }

        internal override FileStream CreateGate(string path) {
            Events.Add("gate-create");
            // ReadWrite adds only same-handle byte observation to the production write-only gate path.
            GateStream = new ControlledFileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                root, "gate", Events, GateReadError, GateFlushError, GateCloseError, observePhysicalGate: true);
            return GateStream;
        }

        internal override FileStream ReadGate(string path) {
            Events.Add("gate-read");
            GateStream = new ControlledFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                root, "gate", Events, GateReadError, null, GateCloseError, observePhysicalGate: false);
            return GateStream;
        }

        internal override FileStream ReadPrivateCandidate(string path) {
            Events.Add("private-read");
            PrivateStream = new ControlledFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                root, "private", Events, null, null, PrivateCloseError, observePhysicalGate: false);
            return PrivateStream;
        }
    }

    private sealed class ControlledFileStream(string path, FileMode mode, FileAccess access, FileShare share,
        string root, string name, List<string> events, IOException? readError, IOException? flushError,
        IOException? closeError, bool observePhysicalGate) : FileStream(path, mode, access, share) {
        internal int DisposeCount { get; private set; }
        internal long BytesRead { get; private set; }
        internal byte[]? PhysicalGateBeforeFailure { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer) {
            base.Write(buffer);
            if (!observePhysicalGate) { return; }
            // Drain only the managed buffer, then read file bytes independently of FileStream's buffer/position.
            base.Flush(flushToDisk: false);
            byte[] actual = new byte[checked((int)RandomAccess.GetLength(SafeFileHandle))];
            Assert.Equal(actual.Length, RandomAccess.Read(SafeFileHandle, actual, 0));
            Assert.Equal(FormatGateCodec.EncodedSize, actual.Length);
            Assert.True(FormatGateCodec.TryRead(actual, out _));
            PhysicalGateBeforeFailure = actual;
            events.Add("gate-physical");
        }

        public override int Read(Span<byte> buffer) {
            if (readError is not null) { throw readError; }
            int count = base.Read(buffer);
            BytesRead += count;
            return count;
        }

        public override void Flush(bool flushToDisk) {
            if (flushToDisk) { events.Add(name + "-flush"); }
            // FileStream/SafeFileHandle observation may itself request a nondurable buffer drain.
            if (flushToDisk && flushError is not null) { throw flushError; }
            base.Flush(flushToDisk);
        }

        protected override void Dispose(bool disposing) {
            if (!disposing) { base.Dispose(false); return; }
            DisposeCount++;
            events.Add(name + "-close");
            base.Dispose(true);
            // Probe after the actual data handle closes, so failure cannot be explained by its sharing mode.
            AssertOwnerStillHeld(root);
            if (closeError is not null) { throw closeError; }
        }
    }

    private sealed class TrackingOwnerLock(IDisposable handle, string root, List<string> events, IOException? closeError) : IDisposable {
        internal int DisposeCount { get; private set; }

        public void Dispose() {
            DisposeCount++;
            events.Add("lock-close");
            try { AssertOwnerStillHeld(root); }
            finally { handle.Dispose(); }
            if (closeError is not null) { throw closeError; }
        }
    }

    public void Dispose() {
        if (!Directory.Exists(_root)) { return; }
        // This fixture owns its unique TEMP tree; remove only explicitly enumerated children.
        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(_root);
        while (pending.TryPop(out var directory)) {
            directories.Add(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) { pending.Push(path); }
                else { File.Delete(path); }
            }
        }
        for (int index = directories.Count - 1; index >= 0; index--) { Directory.Delete(directories[index]); }
    }
}
