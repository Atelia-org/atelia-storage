using Atelia.Data;
using Atelia.FrameStore.Internal.Admission;
using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Runtime;
using Atelia.Rbf;

namespace Atelia.FrameStore.Tests.Runtime;

/// <summary>真实 public RBF 的局部协议夹具；不提供 S2 根/no-follow/锁或 private 发布资格。</summary>
internal sealed class RuntimeFixture : IDisposable {
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "FrameStore-runtime-" + Guid.NewGuid().ToString("N"));
    internal readonly List<string> Operations = [];
    internal readonly TestFiles Files;
    internal readonly RecordingLock Lock;
    internal readonly FrameStoreCore Core;

    internal RuntimeFixture(uint formalMax = 0, int maxBuilders = 32, long threshold = RotationThreshold.DefaultBytes,
        bool writable = true) {
        Directory.CreateDirectory(Path.Combine(Root, FrameStorePaths.ActiveDirectoryName));
        Directory.CreateDirectory(Path.Combine(Root, FrameStorePaths.ArchiveDirectoryName));
        Files = new TestFiles(Root, Operations);
        Lock = new RecordingLock(Operations);
        Core = new FrameStoreCore(Files, Lock, formalMax, writable, threshold, maxBuilders);
    }

    internal TrackedRbfFile Adopt(uint fileId) {
        var file = (TrackedRbfFile)Files.CreateActive(fileId);
        Core.AdoptQualifiedActive(fileId, file);
        return file;
    }

    public void Dispose() {
        try { Core.Dispose(); }
        finally {
            // Root is a freshly generated fixture directory under TEMP, never caller-controlled.
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RecordingLock(List<string> operations) : IDisposable {
    internal int DisposeCalls;
    internal Exception? Failure;

    public void Dispose() {
        DisposeCalls++;
        operations.Add("lock:close");
        if (Failure is not null) { throw Failure; }
    }
}

internal sealed class TestFiles : IFrameStoreFiles {
    private readonly string _root;
    private readonly List<string> _operations;
    private readonly StoreIdentity _identity;
    internal readonly Dictionary<uint, TrackedRbfFile> Created = [];
    internal Exception? NextCreateFailure;
    internal Exception? NextAppendFailure;
    internal Exception? NextBeginFailure;
    internal Exception? NextArchiveFailure;
    internal Exception? UnownedReadFailure;
    internal Exception? UnownedCloseFailure;
    internal int UnownedReadCalls;

    internal TestFiles(string root, List<string> operations) {
        _root = root;
        _operations = operations;
        byte[] bytes = Enumerable.Range(1, StoreIdentity.EncodedSize).Select(i => (byte)i).ToArray();
        if (!StoreIdentity.TryRead(bytes, out _identity)) { throw new InvalidOperationException("Invalid fixture identity."); }
    }

    internal string ActivePath(uint fileId) => Path.Combine(_root, FrameStorePaths.GetActiveRelativePath(fileId));
    internal string ArchivePath(uint fileId) => Path.Combine(_root, FrameStorePaths.GetArchiveRelativePath(fileId));

    public IRbfFile CreateActive(uint fileId) {
        _operations.Add($"create:{fileId}");
        if (NextCreateFailure is { } createFailure) {
            NextCreateFailure = null;
            throw createFailure;
        }
        IRbfFile? file = RbfFile.CreateNew(ActivePath(fileId), RbfCacheMode.Off);
        try {
            Span<byte> header = stackalloc byte[FileHeaderCodec.PayloadSize];
            if (!FileHeaderCodec.TryWrite(_identity, fileId, header)) { throw new InvalidOperationException("Invalid fixture header."); }
            file.Append(0, header).Unwrap();
            FrameHeaderReader.Check(file, _identity, fileId).Unwrap();
            var tracked = new TrackedRbfFile(fileId, file, _operations) {
                AppendFailure = NextAppendFailure,
                BeginFailure = NextBeginFailure
            };
            NextAppendFailure = null;
            NextBeginFailure = null;
            Created.Add(fileId, tracked);
            file = null;
            return tracked;
        }
        finally {
            file?.Dispose();
        }
    }

    public void ArchiveClosed(uint fileId) {
        _operations.Add($"archive:{fileId}");
        if (NextArchiveFailure is { } error) {
            NextArchiveFailure = null;
            throw error;
        }
        string destination = ArchivePath(fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(ActivePath(fileId), destination);
    }

    public AteliaResult<FrameRead> ReadUnowned(FrameAddress address, Action markOwnedCleanupFault) {
        UnownedReadCalls++;
        _operations.Add($"read-unowned:{address.FileId}");
        if (UnownedReadFailure is { } readFailure) { throw readFailure; }
        IRbfFile? file;
        try { file = RbfFile.OpenReadOnlyExisting(ArchivePath(address.FileId), RbfCacheMode.Off); }
        catch (FileNotFoundException) { file = RbfFile.OpenReadOnlyExisting(ActivePath(address.FileId), RbfCacheMode.Off); }
        catch (DirectoryNotFoundException) { file = RbfFile.OpenReadOnlyExisting(ActivePath(address.FileId), RbfCacheMode.Off); }

        FrameRead? output = null;
        AteliaError? failedResult = null;
        bool exceptional = false;
        CleanupErrors errors = default;
        try {
            var header = FrameHeaderReader.Check(file, _identity, address.FileId);
            if (header.IsFailure) { failedResult = header.Error; }
            else {
                var result = file.ReadPooledFrame(address.Ticket);
                if (result.IsFailure) { failedResult = result.Error; }
                else {
                    var frame = result.Value!;
                    try { output = new FrameRead(address, frame); }
                    catch { frame.Dispose(); throw; }
                }
            }
        }
        catch (Exception error) {
            exceptional = true;
            errors.Add(error);
        }
        var toClose = file;
        file = null;
        try {
            toClose.Dispose();
            if (UnownedCloseFailure is { } closeFailure) { throw closeFailure; }
        }
        catch (Exception error) {
            exceptional = true;
            markOwnedCleanupFault();
            errors.Add(error);
        }
        if (exceptional) {
            output?.Dispose();
            errors.ThrowIfAny();
        }
        if (failedResult is not null) { return failedResult; }
        return output!;
    }
}

/// <summary>薄装饰器仅记录/注入 public 调用；全部正常存储行为由真实 RBF handle 完成。</summary>
internal sealed class TrackedRbfFile(uint fileId, IRbfFile inner, List<string> operations) : IRbfFile {
    internal readonly IRbfFile Inner = inner;
    internal int AppendCalls;
    internal int BeginCalls;
    internal int FlushCalls;
    internal int DisposeCalls;
    internal int ReadCalls;
    internal Exception? AppendFailure;
    internal Exception? BeginFailure;
    internal Exception? FlushFailure;
    internal Exception? DisposeFailure;
    internal Exception? TailOffsetFailure;
    internal RbfFrameBuilder LastBuilder;

    public RbfFormat Format => Inner.Format;
    public long TailOffset => TailOffsetFailure is null ? Inner.TailOffset : throw TailOffsetFailure;

    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
        AppendCalls++;
        operations.Add($"append:{fileId}");
        if (AppendFailure is { } error) { throw error; }
        return Inner.Append(tag, payload, tailMeta);
    }

    public RbfFrameBuilder BeginAppend() {
        BeginCalls++;
        if (BeginFailure is { } error) { throw error; }
        return LastBuilder = Inner.BeginAppend();
    }

    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) {
        BeginCalls++;
        if (BeginFailure is { } error) { throw error; }
        return LastBuilder = Inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    }

    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) {
        ReadCalls++;
        return Inner.ReadPooledFrame(ptr);
    }

    public void DurableFlush() {
        FlushCalls++;
        operations.Add($"flush:{fileId}");
        if (FlushFailure is { } error) { throw error; }
        Inner.DurableFlush();
    }

    public void Dispose() {
        DisposeCalls++;
        operations.Add($"close:{fileId}");
        Inner.Dispose();
        if (DisposeFailure is { } error) { throw error; }
    }

    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => Inner.ReadFrame(ptr, buffer);
    public RbfReverseSequence ScanReverse(bool showTombstone = false) => Inner.ScanReverse(showTombstone);
    public RbfForwardSequence ScanForward(bool showTombstone = false) => Inner.ScanForward(showTombstone);
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => Inner.GetScanBoundaryAfter(ticket);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => Inner.ScanForward(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => Inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => Inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => Inner.ReadFrameInfo(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => Inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => Inner.ReadPooledTailMeta(ticket);
    public void SetupReadLog(string? logPath) => Inner.SetupReadLog(logPath);
}
