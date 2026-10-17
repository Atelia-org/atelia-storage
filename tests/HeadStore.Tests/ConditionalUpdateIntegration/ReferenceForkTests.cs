using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.History;
using Atelia.HeadStore.Tests.ConditionalUpdateFaults;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateIntegration;

public sealed class ReferenceForkTests {
    [Fact]
    public void EmptyForksShareTheExactEntryWithoutAccumulatingAnOriginChain() {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1], [2])[1];
        HeadId[] children;
        using (var store = f.Open()) {
            HeadId a = store.ForkHead(3, initial, out _).Unwrap();
            HeadId b = store.ForkHead(4, initial, out _).Unwrap();
            HeadId c = store.ForkHead(5, store.ReadHead(a).Unwrap().Id, out _).Unwrap();
            children = [a, b, c];
            store.PublishHead(initial.HeadId, [3], out _).Unwrap();
            foreach (HeadId child in children) {
                Assert.Equal(initial, store.ReadHead(child).Unwrap().Id);
                Assert.Equal(new byte[] { 2, 1 }, HistoryFixture.Collect(store, child).Select(entry => entry.Payload[0]));
            }
            foreach (HeadOriginInfo edge in store.ListHeadOrigins(4).Unwrap()) {
                Assert.Equal(initial.HeadId, edge.SourceHeadId);
                Assert.Equal(initial.Ticket, edge.SourceEntryTicket);
            }
        }
        foreach (HeadId child in children) {
            using IRbfFile file = RbfFile.OpenReadOnlyExisting(f.HeadPath(child.GetLocalId(f.Identity)), RbfCacheMode.Off);
            var records = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(records.MoveNext());
            Assert.Equal(0u, records.Current.Tag);
            Assert.False(records.MoveNext());
            Assert.Null(records.TerminationError);
        }
        using var cold = f.Open(readOnly: true);
        Assert.Equal(4, cold.ListHeads().Unwrap().Count);
        Assert.Equal(3, cold.Audit().Unwrap().EntryCount);
        Assert.All(children, child => Assert.Equal(initial, cold.ReadHead(child).Unwrap().Id));
    }

    [Fact]
    public void ParentAndHeaderOnlyChildCanPublishTogetherAndSameValueStillCreatesARealEntry() {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        HeadId child;
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        HeadEntryId same;
        using (var writer = f.Open()) {
            child = writer.ForkHead(2, initial, out _).Unwrap();
            group = writer.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [initial.HeadId] = new byte[] { 2 }, [child] = new byte[] { 3 }
            }, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(group[child], writer.ReadHead(child).Unwrap().Id);
            Assert.Equal(new[] { group[child], initial }, HistoryFixture.Collect(writer, child).Select(entry => entry.Id));
            same = writer.PublishHead(child, [3], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(group[child], same);
        }
        using var cold = f.Open(readOnly: true);
        Assert.Equal(group[initial.HeadId], cold.ReadHead(initial.HeadId).Unwrap().Id);
        Assert.Equal(new[] { same, group[child], initial }, HistoryFixture.Collect(cold, child).Select(entry => entry.Id));
        Assert.Equal(4, cold.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void PartialParentChildGroupLeavesBothAtTheirExactPreviousEntry(uint completed) {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        using (var writer = f.Open()) { writer.ForkHead(2, initial, out _).Unwrap(); }
        ConditionalIntegrationRecords.AppendGroup(f, new Dictionary<uint, byte[]> { [1] = [2], [2] = [3] }, completed);
        using var cold = f.Open(readOnly: true);
        Assert.Equal(initial, cold.ReadHead(1u).Unwrap().Id);
        Assert.Equal(initial, cold.ReadHead(2u).Unwrap().Id);
        Assert.Equal(new[] { initial }, HistoryFixture.Collect(cold, f.Id(2)).Select(entry => entry.Id));
        Assert.Equal(1, cold.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSourceForksAcceptTheirFirstCuOnlyWhenEveryActualMemberExists(bool complete) {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        using (var writer = f.Open()) {
            writer.ForkHead(3, initial, out _).Unwrap();
            writer.ForkHead(4, initial, out _).Unwrap();
        }
        var group = ConditionalIntegrationRecords.AppendGroup(f,
            new Dictionary<uint, byte[]> { [3] = [3], [4] = [4] }, complete ? [3u, 4u] : [3u]);
        using var cold = f.Open(readOnly: true);
        foreach (uint id in new[] { 3u, 4u }) {
            Assert.Equal(complete ? group[id] : initial, cold.ReadHead(id).Unwrap().Id);
        }
        Assert.Equal(complete ? 3 : 1, cold.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(92)]
    [InlineData(768)]
    public void FirstCuHeaderPredecessorDistinguishesReusedTicketsWithoutRevivingFailedGroup(int replacementLength) {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        using (var writer = f.Open()) {
            writer.ForkHead(3, initial, out _).Unwrap();
            writer.ForkHead(4, initial, out _).Unwrap();
        }
        var group = ConditionalIntegrationRecords.AppendGroup(f,
            new Dictionary<uint, byte[]> { [3] = HistoryFixture.Bytes(64), [4] = HistoryFixture.Bytes(64) }, 3);
        SizedPtr replacement;
        using (IRbfFile file = RbfFile.OpenExisting(f.HeadPath(4), out _, RbfCacheMode.Off)) {
            replacement = file.Append(1, HistoryFixture.Bytes(replacementLength, 0x62), ConditionalIntegrationRecords.Metadata(0)).Unwrap();
            file.DurableFlush();
        }
        Assert.Equal(group[4].Ticket.Offset, replacement.Offset);
        Assert.Equal(replacementLength == 92, group[4].Ticket.Length == replacement.Length);
        using var cold = f.Open(readOnly: true);
        Assert.Equal(initial, cold.ReadHead(3u).Unwrap().Id);
        Assert.Equal(HeadEntryId.Create(f.Id(4), replacement), cold.ReadHead(4u).Unwrap().Id);
        Assert.Equal(new[] { initial }, HistoryFixture.Collect(cold, f.Id(3)).Select(entry => entry.Id));
        Assert.Equal(2, cold.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("crc")]
    [InlineData("unknown-kind")]
    [InlineData("past-tail")]
    public void FirstCuNecessaryMemberErrorsCannotTurnIntoOriginFallback(string defect) {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        using (var writer = f.Open()) {
            writer.ForkHead(3, initial, out _).Unwrap();
            writer.ForkHead(4, initial, out _).Unwrap();
        }
        var tickets = new Dictionary<uint, SizedPtr>();
        foreach (uint id in new[] { 3u, 4u }) {
            using IRbfFile file = RbfFile.OpenReadOnlyExisting(f.HeadPath(id), RbfCacheMode.Off);
            tickets[id] = SizedPtr.Create(file.TailOffset + (id == 4 && defect == "past-tail" ? 100 : 0),
                RbfFile.MeasureWriteSize(28 + 128, 8).Unwrap().FrameLength);
        }
        using (IRbfFile file = RbfFile.OpenExisting(f.HeadPath(3), out _, RbfCacheMode.Off)) {
            Assert.Equal(tickets[3], file.Append(2, ConditionalIntegrationRecords.Encode(tickets, HistoryFixture.Bytes()),
                ConditionalIntegrationRecords.Metadata(0)).Unwrap());
            file.DurableFlush();
        }
        if (defect == "missing-file") { File.Delete(f.HeadPath(4)); }
        else if (defect != "past-tail") {
            using (IRbfFile file = RbfFile.OpenExisting(f.HeadPath(4), out _, RbfCacheMode.Off)) {
                Assert.Equal(tickets[4], file.Append(defect == "unknown-kind" ? 19u : 2u,
                    ConditionalIntegrationRecords.Encode(tickets, HistoryFixture.Bytes()), ConditionalIntegrationRecords.Metadata(0)).Unwrap());
                file.DurableFlush();
            }
            if (defect == "crc") { f.FlipPayload(HeadEntryId.Create(f.Id(4), tickets[4])); }
        }
        using var cold = f.Open(readOnly: true);
        Assert.True(cold.ReadHead(3u).IsFailure);
        int deliveries = 0;
        Assert.True(cold.ReadHeadHistory(f.Id(3), HistoryFixture.Unlimited, _ => { deliveries++; return true; }).IsFailure);
        Assert.Equal(0, deliveries);
    }

    [Fact]
    public void HealthyFirstLocalPublicationCanCoverABadOriginForCurrentReadsWithoutCertifyingHistory() {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, HistoryFixture.Bytes())[0];
        HeadId child;
        using (var writer = f.Open()) { child = writer.ForkHead(3, initial, out _).Unwrap(); }
        f.FlipPayload(initial);
        HeadEntryId local;
        using (var writer = f.Open()) {
            Assert.Equal("Rbf.CrcMismatch", writer.ReadHead(child).Error!.ErrorCode);
            local = writer.PublishHead(child, [3], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(local, writer.ReadHead(child).Unwrap().Id);
        }
        using var cold = f.Open(readOnly: true);
        Assert.Equal(local, cold.ReadHead(child).Unwrap().Id);
        var delivered = new List<HeadEntryId>();
        Assert.Equal("Rbf.CrcMismatch", cold.ReadHeadHistory(child, HistoryFixture.Unlimited,
            entry => { delivered.Add(entry.Id); return true; }).Error!.ErrorCode);
        Assert.Equal(new[] { local }, delivered);
        Assert.Equal("Rbf.CrcMismatch", cold.Audit().Error!.ErrorCode);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(768)]
    public void HeaderPredecessorDifferentLengthWitnessDoesNotReadReplacementPayload(int replacementLength) {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        using (var writer = f.Open()) {
            writer.ForkHead(3, initial, out _).Unwrap();
            writer.ForkHead(4, initial, out _).Unwrap();
        }
        ConditionalIntegrationRecords.AppendGroup(f,
            new Dictionary<uint, byte[]> { [3] = HistoryFixture.Bytes(256), [4] = HistoryFixture.Bytes(256) }, 3);
        HeadEntryId replacement;
        using (IRbfFile file = RbfFile.OpenExisting(f.HeadPath(4), out _, RbfCacheMode.Off)) {
            replacement = HeadEntryId.Create(f.Id(4), file.Append(1, HistoryFixture.Bytes(replacementLength), ConditionalIntegrationRecords.Metadata(0)).Unwrap());
            file.Append(1, [7], ConditionalIntegrationRecords.Metadata(0)).Unwrap();
            file.DurableFlush();
        }
        f.FlipPayload(replacement);
        using var cold = f.Open(readOnly: true);
        Assert.Equal(initial, cold.ReadHead(3u).Unwrap().Id);
        Assert.Equal(new[] { initial }, HistoryFixture.Collect(cold, f.Id(3)).Select(entry => entry.Id));
        Assert.Equal("Rbf.CrcMismatch", cold.Audit().Error!.ErrorCode);
    }

    [Fact]
    public void HistoryVisitorReusesSourceHandleWhenReadingAnEmptyChildWhoseSourceIsFirstLocalCu() {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        f.SeedPublic(2, [2]);
        HeadId sourceHead;
        HeadId child;
        HeadEntryId source;
        using (var writer = f.Open()) {
            sourceHead = writer.ForkHead(3, initial, out _).Unwrap();
            source = writer.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [sourceHead] = new byte[] { 3 }, [f.Id(2)] = new byte[] { 4 }
            }, out _).Unwrap()[sourceHead];
            child = writer.ForkHead(4, source, out _).Unwrap();
        }
        using var cold = f.Open(readOnly: true);
        var delivered = new List<HeadEntryId>();
        Assert.Equal(HeadHistoryEnd.Complete, cold.ReadHeadHistory(sourceHead, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            Assert.Equal(source, cold.ReadHead(child).Unwrap().Id);
            return true;
        }).Unwrap());
        Assert.Equal(new[] { source, initial }, delivered);
        Assert.Equal(source, cold.ReadHead(child).Unwrap().Id);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    public void CuSourceDataOrAnyNecessaryFileConfirmationFailurePreventsFormalForkOutput(uint failedDependency) {
        using var f = new ConditionalUpdateFaultFixture();
        HeadEntryId source;
        using (var writer = f.OpenPublic()) { source = writer.PublishHeads(f.Changes((1, [4]), (2, [5])), out _).Unwrap()[f.Id(1)]; }
        var store = f.Open();
        var failure = new IOException("source CU confirmation");
        if (failedDependency == 0) { f.Hooks.ConfirmDataDurable = () => throw failure; }
        else { f.Hooks.BeforeTagDependencyFlush = id => { if (id == failedDependency) { throw failure; } }; }
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => store.ForkHead(4, source, out outcome)));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(f.HeadPath(4)));
        Assert.False(File.Exists(Path.Combine(f.Root, "creating", "pending.rbf")));
    }
}
