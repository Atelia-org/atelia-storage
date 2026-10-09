using Atelia.Data;
using Atelia.FrameStore.Internal.Format;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Runtime;

public sealed class FrameStoreCoreTests {
    [Fact]
    public void MixedBuilders_SkipBusyFilesCompleteOutOfOrderAndReuseLowestId() {
        using var fixture = new RuntimeFixture(formalMax: 12);
        fixture.Adopt(7);
        fixture.Adopt(2);
        var first = fixture.Core.BeginAppend(1, 0, out var firstAddress);
        var firstAlias = first;
        var oldWriter = first.PayloadAndMeta;
        var second = fixture.Core.BeginAppend();
        var third = fixture.Core.BeginAppend(0, 0, out var thirdAddress);
        Assert.Equal(2u, firstAddress.FileId);
        Assert.Equal(13u, thirdAddress.FileId); // scalar max also covers archive IDs that are not retained.
        Assert.Equal(3, fixture.Core.OutstandingBuilders);
        Write(second, [7]);
        Assert.Equal(7u, second.EndAppend(20).Unwrap().FileId);
        Write(first, [2]);
        Assert.Equal(firstAddress, first.EndAppend(21).Unwrap());
        firstAlias.Dispose();
        Assert.IsType<FrameStoreStateError>(firstAlias.EndAppend(99).Error);
        Assert.Throws<InvalidOperationException>(() => oldWriter.GetMemory(1));
        Assert.Throws<InvalidOperationException>(() => oldWriter.Advance(0));
        Assert.Throws<InvalidOperationException>(() => { _ = firstAlias.PayloadAndMeta; });
        var replacement = fixture.Core.BeginAppend(1, 0, out var replacementAddress);
        Assert.Equal(2u, replacementAddress.FileId);
        firstAlias.Dispose(); // cannot cancel the replacement.
        Write(replacement, [3]);
        replacement.EndAppend(22).Unwrap();
        third.Dispose();
        third.Dispose();
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        Assert.Equal(13u, fixture.Core.MaxPublishedFileId);
    }

    [Fact]
    public void QuotaAndInvalidParameters_RejectBeforeDrainWhileBufferAppendCanUseExtraLease() {
        using var fixture = new RuntimeFixture(maxBuilders: 1, threshold: FileHeaderCodec.InitializationBoundary);
        var held = fixture.Core.BeginAppend(0, 0, out _);
        Assert.Equal(2u, fixture.Core.Append(1, [1]).Unwrap().FileId);
        fixture.Operations.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Core.BeginAppend(-1, 0, out _));
        var quotaError = Assert.Throws<InvalidOperationException>(() => fixture.Core.BeginAppend());
        Assert.Contains("(1)", quotaError.Message);
        Assert.Contains("count is 1", quotaError.Message);
        Assert.Empty(fixture.Operations);
        Assert.False(fixture.Core.IsFaulted);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        Assert.Equal(3u, fixture.Core.Append(2, [2]).Unwrap().FileId);
        Assert.Equal(new[] { "flush:2", "close:2", "archive:2", "create:3", "append:3" }, fixture.Operations);
        held.Dispose();
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
    }

    [Fact]
    public void CorrectableEndResults_PreserveLeaseQuotaAndEarlyAddress() {
        using var fixture = new RuntimeFixture(maxBuilders: 1);
        var builder = fixture.Core.BeginAppend(2, 1, out var earlyAddress);
        var writer = builder.PayloadAndMeta;
        writer.ReserveSpan(1, out int token).Fill(1);
        Assert.Equal("Rbf.StateError", builder.EndAppend(7).Error!.ErrorCode);
        writer.Commit(token);
        Assert.Equal("Rbf.ArgumentError", builder.EndAppend(7).Error!.ErrorCode);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        Write(builder, [2, 3]);
        Assert.Equal("Rbf.ArgumentError", builder.EndAppend(7, 0).Error!.ErrorCode);
        Assert.Equal(earlyAddress, builder.EndAppend(7).Unwrap());
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        using var read = fixture.Core.ReadFrame(earlyAddress).Unwrap();
        Assert.Equal(new byte[] { 1, 2, 3 }, read.PayloadAndMeta.ToArray());
        Assert.Equal(1, read.TailMetaLength);
    }

    [Fact]
    public void BorrowGuard_PreservesFailedAdvanceAndReservationOperationsDoNotClearIt() {
        using var fixture = new RuntimeFixture();
        var builder = fixture.Core.BeginAppend(2, 0, out _);
        var writer = builder.PayloadAndMeta;
        writer.ReserveSpan(1, out int token).Fill(1);
        writer.GetMemory(2).Span[0] = 2;
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(2));
        Assert.True(writer.TryGetReservedSpan(token, out var reserved));
        Assert.Equal((byte)1, reserved[0]);
        writer.Commit(token);
        _ = writer.Length;
        Assert.Throws<InvalidOperationException>(() => builder.EndAppend(4));
        Assert.False(fixture.Core.IsFaulted);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        writer.Advance(1);
        builder.EndAppend(4).Unwrap();
        Assert.Equal(0, fixture.Core.OutstandingBuilders);

        var zero = fixture.Core.BeginAppend(0, 0, out _);
        zero.PayloadAndMeta.GetSpan(1);
        Assert.Throws<InvalidOperationException>(() => zero.EndAppend(0));
        zero.PayloadAndMeta.Advance(0);
        zero.EndAppend(0).Unwrap();
    }

    [Fact]
    public void ConfirmDuringBuilding_FlushesCompletedPrefixPreservesBorrowAndReDirtiesOnEnd() {
        using var fixture = new RuntimeFixture();
        var completed = fixture.Core.Append(11, [8]).Unwrap();
        var builder = fixture.Core.BeginAppend(1, 0, out var earlyAddress);
        var writer = builder.PayloadAndMeta;
        writer.GetMemory(1).Span[0] = 9;
        var file = fixture.Files.Created[1];
        fixture.Core.ConfirmDurable();
        Assert.Equal(1, file.FlushCalls);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        using var historical = fixture.Core.ReadFrame(completed).Unwrap();
        Assert.Equal(new byte[] { 8 }, historical.PayloadAndMeta.ToArray());
        Assert.Throws<InvalidOperationException>(() => fixture.Core.ReadFrame(earlyAddress));
        Assert.Throws<InvalidOperationException>(() => builder.EndAppend(12));
        Assert.False(fixture.Core.IsFaulted);
        fixture.Core.ConfirmDurable();
        Assert.Equal(1, file.FlushCalls);
        writer.Advance(1);
        Assert.Equal(earlyAddress, builder.EndAppend(12).Unwrap());
        fixture.Core.ConfirmDurable();
        Assert.Equal(2, file.FlushCalls);
        using var latest = fixture.Core.ReadFrame(earlyAddress).Unwrap();
        Assert.Equal(new byte[] { 9 }, latest.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void AllDirtyFilesIncludingLeased_AreConfirmedInNumericOrderAndReopenedOutputsStartDirty() {
        using var fixture = new RuntimeFixture(formalMax: 9);
        var high = fixture.Adopt(9);
        var low = fixture.Adopt(3);
        var held = fixture.Core.BeginAppend();
        fixture.Operations.Clear();
        fixture.Core.ConfirmDurable();
        Assert.Equal(new[] { "flush:3", "flush:9" }, fixture.Operations);
        Assert.Equal(1, low.FlushCalls);
        Assert.Equal(1, high.FlushCalls);
        held.Dispose();
        fixture.Operations.Clear();
        fixture.Core.ConfirmDurable();
        Assert.Empty(fixture.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessAfterInnerRbf_UsesNoTailGetterOrMaintenance(bool sized) {
        using var fixture = new RuntimeFixture(formalMax: 1);
        var file = fixture.Adopt(1);
        file.TailOffsetFailure = new IOException("A post-success getter must never be called.");
        var first = fixture.Core.Append(11, [1]).Unwrap();
        var builder = sized ? fixture.Core.BeginAppend(1, 0, out _) : fixture.Core.BeginAppend();
        Write(builder, [2]);
        var second = builder.EndAppend(12).Unwrap();
        Assert.Equal(1u, first.FileId);
        Assert.Equal(1u, second.FileId);
        Assert.Equal(0, file.FlushCalls);
        Assert.Equal(0, file.DisposeCalls);
        Assert.False(fixture.Core.IsFaulted);
    }

    [Fact]
    public void ThresholdEqualityAllowsReuse_ExcessCompletesBeforeLaterFlushCloseArchive() {
        long threshold = FileHeaderCodec.InitializationBoundary + RbfFile.MeasureWriteSize(1).Value.AppendLength;
        using var fixture = new RuntimeFixture(threshold: threshold);
        var first = fixture.Core.Append(1, [1]).Unwrap();
        var builder = fixture.Core.BeginAppend(1, 0, out var early);
        Assert.Equal(first.FileId, early.FileId);
        Write(builder, [2]);
        fixture.Operations.Clear();
        Assert.Equal(early, builder.EndAppend(2).Unwrap());
        Assert.Empty(fixture.Operations);
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        fixture.Core.ConfirmDurable();
        Assert.Equal(new[] { "flush:1", "close:1", "archive:1" }, fixture.Operations);
        Assert.True(File.Exists(fixture.Files.ArchivePath(1)));
        Assert.False(File.Exists(fixture.Files.ActivePath(1)));
        using var read = fixture.Core.ReadFrame(early).Unwrap();
        Assert.Equal(early, read.Address);
        Assert.Equal(new byte[] { 2 }, read.PayloadAndMeta.ToArray());
        builder.Dispose();
        Assert.Equal(1, fixture.Files.Created[1].DisposeCalls);
        Assert.Equal(1u, fixture.Core.MaxPublishedFileId);
    }

    [Fact]
    public void RecoveredStoppedFile_IsDrainedAtHandoffAndArchiveKeepsItsIdentity() {
        using var fixture = new RuntimeFixture(formalMax: 6, threshold: FileHeaderCodec.InitializationBoundary);
        var file = (TrackedRbfFile)fixture.Files.CreateActive(6);
        var address = FrameAddress.Create(6, file.Append(44, [6]).Unwrap());
        fixture.Core.AdoptQualifiedActive(6, file);
        fixture.Operations.Clear();
        fixture.Core.CompleteWritableHandoff();
        Assert.Equal(new[] { "flush:6", "close:6", "archive:6" }, fixture.Operations);
        using var read = fixture.Core.ReadFrame(address).Unwrap();
        Assert.Equal(44u, read.Tag);
        Assert.Equal(7u, fixture.Core.BeginAppend(0, 0, out var next).EndAppend(0).Unwrap().FileId);
        Assert.Equal(7u, next.FileId);
    }

    [Fact]
    public void FileIdExhaustion_RejectsBeforeDrainButExistingCandidateAndConfirmRemainLegal() {
        using var fixture = new RuntimeFixture(formalMax: uint.MaxValue, threshold: FileHeaderCodec.InitializationBoundary);
        fixture.Adopt(1);
        Assert.Equal(1u, fixture.Core.Append(1, []).Unwrap().FileId);
        fixture.Operations.Clear();
        Assert.Throws<InvalidOperationException>(() => fixture.Core.BeginAppend());
        Assert.Throws<InvalidOperationException>(() => fixture.Core.Append(2, []));
        Assert.Empty(fixture.Operations);
        Assert.False(fixture.Core.IsFaulted);
        fixture.Core.ConfirmDurable();
        Assert.Equal(new[] { "flush:1", "close:1", "archive:1" }, fixture.Operations);
        Assert.Equal(uint.MaxValue, fixture.Core.MaxPublishedFileId);
    }

    [Fact]
    public void BeginInitializationFailure_IssuesNoQuotaOrAddressAndPreservesPublishedFileId() {
        using var fixture = new RuntimeFixture();
        var failure = new IOException("Injected Begin preparation failure.");
        fixture.Files.NextBeginFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Core.BeginAppend(0, 0, out _)));
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        Assert.Equal(1u, fixture.Core.MaxPublishedFileId);
        Assert.False(fixture.Core.IsFaulted);
        fixture.Files.Created[1].BeginFailure = null;
        var retry = fixture.Core.BeginAppend(0, 0, out var address);
        Assert.Equal(1u, address.FileId);
        retry.Dispose();
    }

    [Fact]
    public void CreateFailure_FaultsAllExistingLeasesWithoutIssuingPhantomQuota() {
        using var fixture = new RuntimeFixture();
        var held = fixture.Core.BeginAppend();
        var writer = held.PayloadAndMeta;
        var failure = new IOException("Injected publication failure.");
        fixture.Files.NextCreateFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Core.BeginAppend()));
        Assert.True(fixture.Core.IsFaulted);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        Assert.Equal(1u, fixture.Core.MaxPublishedFileId);
        Assert.Throws<InvalidOperationException>(() => writer.GetMemory());
        Assert.Throws<InvalidOperationException>(() => held.EndAppend(1));
        Assert.Equal(0, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void AppendException_FaultsOwnerBeforeOtherLeaseAndReadAccess() {
        using var fixture = new RuntimeFixture();
        var completed = fixture.Core.Append(11, [1]).Unwrap();
        var held = fixture.Core.BeginAppend();
        var writer = held.PayloadAndMeta;
        var failure = new IOException("Injected Append exception.");
        fixture.Files.NextAppendFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Core.Append(12, [2])));
        Assert.True(fixture.Core.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => { _ = writer.Length; });
        Assert.Throws<InvalidOperationException>(() => held.EndAppend(13));
        Assert.Throws<InvalidOperationException>(() => fixture.Core.ReadFrame(completed));
        Assert.Throws<InvalidOperationException>(() => fixture.Core.ReadFrame(default));
        Assert.Equal(0, fixture.Files.Created[1].ReadCalls);
        fixture.Core.Dispose();
        Assert.Equal(1, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void UnknownEndException_TerminatesCurrentLeaseAndFaultsOtherLeasesBeforeAccess() {
        using var fixture = new RuntimeFixture();
        var completed = fixture.Core.Append(10, [1]).Unwrap();
        var first = fixture.Core.BeginAppend();
        var alias = first;
        var firstWriter = first.PayloadAndMeta;
        var second = fixture.Core.BeginAppend();
        var secondWriter = second.PayloadAndMeta;
        var firstFile = fixture.Files.Created[1];

        // Fault injection only: access the test decorator's raw RBF Builder to create an inner borrow
        // the outer lease did not observe. This is not a legal FrameStore consumer trajectory and does
        // not simulate post-output pool-return failure. It makes a real delegated End throw.
        firstFile.LastBuilder.PayloadAndMeta.GetMemory(1);
        var failure = Assert.Throws<InvalidOperationException>(() => first.EndAppend(11));
        Assert.Contains("Advance", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Core.IsFaulted);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        alias.Dispose();
        first.Dispose();
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        Assert.Throws<InvalidOperationException>(() => alias.EndAppend(12));
        Assert.Throws<InvalidOperationException>(() => firstWriter.Advance(0));
        Assert.Throws<InvalidOperationException>(() => secondWriter.GetMemory());
        Assert.Throws<InvalidOperationException>(() => second.EndAppend(13));
        Assert.Throws<InvalidOperationException>(() => fixture.Core.ReadFrame(completed));
        Assert.Equal(0, firstFile.ReadCalls);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
        fixture.Operations.Clear();
        fixture.Core.Dispose();
        alias.Dispose();
        second.Dispose();
        fixture.Core.Dispose();
        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, fixture.Operations);
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        Assert.Equal(1, firstFile.DisposeCalls);
        Assert.Equal(1, fixture.Files.Created[2].DisposeCalls);
    }

    [Fact]
    public void FlushFailure_AfterEarlierFlushStopsOwnerAndPreservesPrimaryException() {
        using var fixture = new RuntimeFixture();
        fixture.Core.Append(1, [1]).Unwrap();
        var held = fixture.Core.BeginAppend();
        var writer = held.PayloadAndMeta;
        fixture.Core.Append(2, [2]).Unwrap();
        var failure = new IOException("Injected flush failure.");
        fixture.Files.Created[2].FlushFailure = failure;
        fixture.Operations.Clear();
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Core.ConfirmDurable()));
        Assert.Equal(new[] { "flush:1", "flush:2" }, fixture.Operations);
        Assert.Throws<InvalidOperationException>(() => writer.Advance(0));
        Assert.Equal(0, fixture.Lock.DisposeCalls);
        fixture.Core.Dispose(); // does not rethrow the old flush error.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveCloseOrMoveFailure_FaultsOwnerAndNeverRetriesClose(bool closeFailure) {
        using var fixture = new RuntimeFixture(threshold: FileHeaderCodec.InitializationBoundary);
        fixture.Core.Append(1, [1]).Unwrap();
        var file = fixture.Files.Created[1];
        var failure = new IOException("Injected archive maintenance failure.");
        if (closeFailure) { file.DisposeFailure = failure; }
        else { fixture.Files.NextArchiveFailure = failure; }
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Core.ConfirmDurable()));
        Assert.True(fixture.Core.IsFaulted);
        Assert.Equal(1, file.DisposeCalls);
        fixture.Core.Dispose();
        fixture.Core.Dispose();
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void OwnedRead_OutlivesOwnerAndItsAliasesShareIndependentDispose() {
        using var fixture = new RuntimeFixture();
        var address = fixture.Core.Append(9, [1, 2], [3]).Unwrap();
        var read = fixture.Core.ReadFrame(address).Unwrap();
        var alias = read;
        fixture.Core.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, read.PayloadAndMeta.ToArray());
        Assert.Equal(address, read.Address);
        Assert.Equal(9u, read.Tag);
        Assert.Equal(1, read.TailMetaLength);
        alias.Dispose();
        read.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = read.PayloadAndMeta.Length; });
        Assert.Equal(9u, read.Tag);
    }

    [Fact]
    public void UnownedRead_PureReadExceptionStaysHealthyButCloseFailureFaultsOwner() {
        using var fixture = new RuntimeFixture(threshold: FileHeaderCodec.InitializationBoundary);
        var address = fixture.Core.Append(1, [1]).Unwrap();
        fixture.Core.ConfirmDurable();
        var readFailure = new IOException("Injected pure read failure.");
        fixture.Files.UnownedReadFailure = readFailure;
        Assert.Same(readFailure, Assert.Throws<IOException>(() => fixture.Core.ReadFrame(address)));
        Assert.False(fixture.Core.IsFaulted);
        fixture.Files.UnownedReadFailure = null;
        var closeFailure = new IOException("Injected temporary reader close failure.");
        fixture.Files.UnownedCloseFailure = closeFailure;
        Assert.Same(closeFailure, Assert.Throws<IOException>(() => fixture.Core.ReadFrame(address)));
        Assert.True(fixture.Core.IsFaulted);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void Dispose_InvalidatesAllAliasesThenTriesEveryFileAndLockOnceWithOrderedErrors() {
        using var fixture = new RuntimeFixture(formalMax: 5);
        var high = fixture.Adopt(5);
        var low = fixture.Adopt(2);
        var first = fixture.Core.BeginAppend();
        var stale = first.PayloadAndMeta;
        var second = fixture.Core.BeginAppend();
        var lowError = new IOException("low close");
        var highError = new InvalidOperationException("high close");
        var lockError = new ApplicationException("lock close");
        low.DisposeFailure = lowError;
        high.DisposeFailure = highError;
        fixture.Lock.Failure = lockError;
        fixture.Operations.Clear();
        var aggregate = Assert.Throws<AggregateException>(() => fixture.Core.Dispose());
        Assert.Equal(new Exception[] { lowError, highError, lockError }, aggregate.InnerExceptions);
        Assert.Equal(new[] { "close:2", "close:5", "lock:close" }, fixture.Operations);
        fixture.Core.Dispose();
        first.Dispose();
        second.Dispose();
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        Assert.Equal(1, low.DisposeCalls);
        Assert.Equal(1, high.DisposeCalls);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => stale.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => first.EndAppend(1));
        Assert.Throws<ObjectDisposedException>(() => fixture.Core.ReadFrame(default));
    }

    [Fact]
    public void SingleDisposeFailure_PreservesExceptionIdentityAndIsNotRetried() {
        using var fixture = new RuntimeFixture();
        fixture.Core.Append(1, []).Unwrap();
        var file = fixture.Files.Created[1];
        var failure = new IOException("single close failure");
        file.DisposeFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Core.Dispose()));
        Assert.Contains(nameof(TrackedRbfFile.Dispose), failure.StackTrace);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
        fixture.Core.Dispose();
        Assert.Equal(1, file.DisposeCalls);
    }

    [Fact]
    public void DefaultFacadesAndReadOnlyMode_RejectWithoutIssuingOutput() {
        FrameBuilder builder = default;
        FramePayloadWriter writer = default;
        Assert.IsType<FrameStoreStateError>(builder.EndAppend(0).Error);
        Assert.Throws<InvalidOperationException>(() => { _ = builder.PayloadAndMeta; });
        Assert.Throws<InvalidOperationException>(() => writer.GetMemory());
        builder.Dispose();
        using var fixture = new RuntimeFixture(writable: false, maxBuilders: 0, threshold: -1);
        Assert.Throws<InvalidOperationException>(() => fixture.Core.Append(0, []));
        Assert.Throws<InvalidOperationException>(() => fixture.Core.BeginAppend());
        Assert.Throws<InvalidOperationException>(() => fixture.Core.ConfirmDurable());
        Assert.IsType<FrameStoreStateError>(fixture.Core.ReadFrame(default).Error);
        Assert.Empty(fixture.Operations);
    }

    private static void Write(FrameBuilder builder, ReadOnlySpan<byte> bytes) {
        var writer = builder.PayloadAndMeta;
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }
}
