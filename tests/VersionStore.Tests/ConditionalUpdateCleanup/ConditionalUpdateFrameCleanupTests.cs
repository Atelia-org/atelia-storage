using System.Globalization;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Storage;
using Atelia.VersionStore.Tests.ConditionalUpdateFaults;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdateCleanup;

public sealed class ConditionalUpdateFrameCleanupTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulLocalPublicationReadReturnFailureFaultsAndClosesTheOwner(bool exact) {
        using var fixture = new ConditionalUpdateFaultFixture();
        var operations = new FrameCleanupOperations { BadReturnId = 1 };
        var store = Open(fixture, operations, readOnly: true);
        Assert.Throws<ArgumentException>(() => {
            if (exact) { store.ReadRevision(fixture.Initial[1]); }
            else { store.ReadRef(fixture.Id(1)); }
        });
        AssertLeaseWasTakenAndFaultCleanup(fixture, operations);
        using var reopened = fixture.OpenPublic(readOnly: true);
        Assert.Equal(fixture.Initial[1], reopened.ReadRef(fixture.Id(1)).Unwrap().Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalCurrentPreflightReturnFailureFaultsBeforeTheDataBarrier(bool batch) {
        using var fixture = new ConditionalUpdateFaultFixture();
        var operations = new FrameCleanupOperations { BadReturnId = 1 };
        var store = Open(fixture, operations, readOnly: false);
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ArgumentException>(() => {
            if (batch) { store.PublishRefs(fixture.Changes((1, [0x31]), (2, [0x32])), out outcome); }
            else { store.PublishRef(fixture.Id(1), [0x31], out outcome); }
        });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.DoesNotContain("data:confirm", fixture.Events);
        Assert.All(operations.Files, file => Assert.Equal(0, file.BeginCalls));
        AssertLeaseWasTakenAndFaultCleanup(fixture, operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulDesignatedPeerReadReturnFailureClosesPeerBeforeOwnedFilesAndLock(bool exact) {
        using var fixture = new ConditionalUpdateFaultFixture();
        IReadOnlyDictionary<RefId, RefRevision> group = PublishGroup(fixture);
        var operations = new FrameCleanupOperations { BadReturnId = 2, BadReturnTicket = group[fixture.Id(2)].Ticket };
        var store = Open(fixture, operations, readOnly: true);
        Assert.Throws<ArgumentException>(() => {
            if (exact) { store.ReadRevision(group[fixture.Id(1)]); }
            else { store.ReadRef(fixture.Id(1)); }
        });
        AssertLeaseWasTakenAndFaultCleanup(fixture, operations);
        Assert.Equal(new[] { "close:2", "close:1", "lock:close" },
            operations.Events.Where(item => item.StartsWith("close:", StringComparison.Ordinal) || item == "lock:close"));
        using var reopened = fixture.OpenPublic(readOnly: true);
        Assert.Equal(group[fixture.Id(1)], reopened.ReadRef(fixture.Id(1)).Unwrap().Revision);
        Assert.Equal(group[fixture.Id(2)], reopened.ReadRef(fixture.Id(2)).Unwrap().Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BodyPrimarySurvivesLocalReturnAndOwnedFileCleanupFailuresInOrder(bool exact) {
        using var fixture = new ConditionalUpdateFaultFixture();
        IReadOnlyDictionary<RefId, RefRevision> group = PublishGroup(fixture);
        var primary = new IOException("peer body read failed");
        var fileCleanup = new IOException("owned file close failed");
        var operations = new FrameCleanupOperations {
            BadReturnId = 1, ReadFailureId = 2, ReadFailure = primary,
            DisposeFailureId = 1, DisposeFailure = fileCleanup
        };
        var store = Open(fixture, operations, readOnly: true);
        AggregateException observed = Assert.Throws<AggregateException>(() => {
            if (exact) { store.ReadRevision(group[fixture.Id(1)]); }
            else { store.ReadRef(fixture.Id(1)); }
        });
        Assert.Same(primary, observed.InnerExceptions[0]);
        Assert.IsType<ArgumentException>(observed.InnerExceptions[1]);
        Assert.Same(fileCleanup, observed.InnerExceptions[2]);
        AssertLeaseWasTakenAndFaultCleanup(fixture, operations);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void AuditCUFrameReturnFailureClosesItsLocalResourcesBeforeTheRootLock(uint badId) {
        using var fixture = new ConditionalUpdateFaultFixture();
        IReadOnlyDictionary<RefId, RefRevision> group = PublishGroup(fixture);
        var operations = new FrameCleanupOperations { BadReturnId = badId, BadReturnTicket = group[fixture.Id(badId)].Ticket };
        var store = Open(fixture, operations, readOnly: true);
        Assert.Throws<ArgumentException>(() => { store.Audit(); });
        AssertLeaseWasTakenAndFaultCleanup(fixture, operations);
        int lockIndex = operations.Events.IndexOf("lock:close");
        Assert.Equal(operations.Events.Count - 1, lockIndex);
        Assert.Contains(operations.Files, file => file.Id == 1);
        Assert.Contains(operations.Files, file => file.Id == 2);
        Assert.All(operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        using var reopened = fixture.OpenPublic(readOnly: true);
        Assert.Equal(5L, reopened.Audit().Unwrap().SnapshotCount);
    }

    [Fact]
    public void PeerBodyReadExceptionWithHealthyFrameReturnKeepsOwnerUsable() {
        using var fixture = new ConditionalUpdateFaultFixture();
        IReadOnlyDictionary<RefId, RefRevision> group = PublishGroup(fixture);
        var primary = new IOException("ordinary peer read failed");
        var operations = new FrameCleanupOperations { ReadFailureId = 2, ReadFailure = primary };
        var store = Open(fixture, operations, readOnly: true);
        Assert.Same(primary, Assert.Throws<IOException>(() => { store.ReadRef(fixture.Id(1)); }));
        Assert.Empty(operations.BadFrames);
        Assert.Equal(0, operations.Locks.Single().DisposeCalls);
        Assert.Equal(0, operations.Files.Single(file => file.Id == 1).DisposeCalls);
        Assert.Equal(1, operations.Files.Single(file => file.Id == 2).DisposeCalls);
        Assert.Equal(3, store.ListRefs().Unwrap().Count);
        operations.ReadFailure = null;
        Assert.Equal(group[fixture.Id(1)], store.ReadRef(fixture.Id(1)).Unwrap().Revision);
        fixture.Data.EnsureUsable();
        fixture.Close();
        Assert.All(operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, operations.Locks.Single().DisposeCalls);
        Assert.Equal("lock:close", operations.Events[^1]);
    }

    private static VersionStore Open(ConditionalUpdateFaultFixture fixture, FrameCleanupOperations operations, bool readOnly) {
        fixture.Close();
        fixture.Owner = VersionStoreFactory.Open(fixture.Root, fixture.Data, readOnly, operations, fixture.Hooks);
        return fixture.Owner;
    }

    private static IReadOnlyDictionary<RefId, RefRevision> PublishGroup(ConditionalUpdateFaultFixture fixture) {
        using var writer = fixture.OpenPublic();
        var group = writer.PublishRefs(fixture.Changes((1, ConditionalUpdateFaultFixture.Bytes(0x41)),
            (2, ConditionalUpdateFaultFixture.Bytes(0x51))), out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return group;
    }

    private static void AssertLeaseWasTakenAndFaultCleanup(ConditionalUpdateFaultFixture fixture,
        FrameCleanupOperations operations) {
        RbfPooledFrame bad = Assert.Single(operations.BadFrames);
        // Real Dispose exchanges its buffer to null before Shared.Return rejects its nonbucket size.
        Assert.Throws<ObjectDisposedException>(() => { _ = bad.PayloadAndMeta.Length; });
        bad.Dispose();
        Assert.Throws<InvalidOperationException>(() => { fixture.Owner!.ListRefs(); });
        Assert.All(operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, operations.Locks.Single().DisposeCalls);
        Assert.Equal("lock:close", operations.Events[^1]);
        fixture.Owner!.Dispose();
        fixture.Owner.Dispose();
        Assert.All(operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, operations.Locks.Single().DisposeCalls);
        fixture.Data.EnsureUsable();
    }
}

/// <summary>只替换成功完整读取后的 lease backing；实际 framing/CRC 始终由 public RBF 完成。</summary>
internal sealed class FrameCleanupOperations : VersionStoreFactoryOperations {
    internal readonly List<string> Events = [];
    internal readonly List<FrameCleanupFile> Files = [];
    internal readonly List<ConditionalUpdateLock> Locks = [];
    internal readonly List<RbfPooledFrame> BadFrames = [];
    internal uint? BadReturnId;
    internal SizedPtr? BadReturnTicket;
    internal uint? ReadFailureId;
    internal Exception? ReadFailure;
    internal uint? DisposeFailureId;
    internal Exception? DisposeFailure;

    internal override IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) {
        var ownerLock = new ConditionalUpdateLock(base.AcquireOwnerLock(root, create, readOnly), Events);
        Locks.Add(ownerLock);
        return ownerLock;
    }

    internal override IRbfFile OpenRef(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        uint id = uint.Parse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var file = new FrameCleanupFile(id, base.OpenRef(path, readOnly, out recovery), this);
        Files.Add(file);
        return file;
    }

    internal RbfPooledFrame ReplaceWithBadReturnFrame(RbfPooledFrame original) {
        // Nonbucket arrays fail a real Shared.Return. No CRC or dispose result is mocked.
        byte[] content = original.PayloadAndMeta.ToArray();
        SizedPtr ticket = original.Ticket;
        uint tag = original.Tag;
        int tailMetaLength = original.TailMetaLength;
        bool tombstone = original.IsTombstone;
        original.Dispose();
        int length = Math.Max(17, content.Length + 1);
        if ((length & (length - 1)) == 0) { length++; }
        byte[] nonBucket = new byte[length];
        content.CopyTo(nonBucket, 0);
        var replacement = new RbfPooledFrame(nonBucket, ticket, tag, 0, content.Length, tailMetaLength, tombstone);
        BadFrames.Add(replacement);
        return replacement;
    }
}

internal sealed class FrameCleanupFile(uint id, IRbfFile inner, FrameCleanupOperations operations) : IRbfFile {
    internal uint Id => id;
    internal int DisposeCalls;
    internal int BeginCalls;
    public RbfFormat Format => inner.Format;
    public long TailOffset => inner.TailOffset;

    public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) {
        operations.Events.Add($"read:{id}");
        if (operations.ReadFailureId == id && operations.ReadFailure is { } failure) { throw failure; }
        var result = inner.ReadPooledFrame(ticket);
        if (result.IsFailure) { return result.Error!; }
        if (operations.BadReturnId == id && (operations.BadReturnTicket is null || operations.BadReturnTicket == ticket)) {
            return operations.ReplaceWithBadReturnFrame(result.Value!);
        }
        return result;
    }

    public void Dispose() {
        DisposeCalls++;
        operations.Events.Add($"close:{id}");
        inner.Dispose();
        if (operations.DisposeFailureId == id && operations.DisposeFailure is { } failure) { throw failure; }
    }

    public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) {
        BeginCalls++;
        return inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
    }
    public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
    public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);
    public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
    public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
    public RbfForwardSequence ScanForward(bool showTombstone = false) => inner.ScanForward(showTombstone);
    public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
    public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
    public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanReverse(boundary, showTombstone);
    public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
    public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
    public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => inner.GetScanBoundaryAfter(ticket);
    public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
    public void DurableFlush() => inner.DurableFlush();
    public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
}
