using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateFaults;

public sealed class ConditionalUpdateReuseAndMemberTests {
    [Theory]
    [InlineData("ordinary", -1)]
    [InlineData("ordinary", 0)]
    [InlineData("ordinary", 1)]
    [InlineData("ab", -1)]
    [InlineData("ab", 0)]
    [InlineData("ab", 1)]
    [InlineData("bc", -1)]
    [InlineData("bc", 0)]
    [InlineData("bc", 1)]
    public void FreshSameOrDifferentLengthReplacementNeverRevivesTheEarlierFailedGroup(string replacement, int sizeRelation) {
        using var f = new ConditionalUpdateFaultFixture();
        byte[] failedRoot = ConditionalUpdateFaultFixture.Bytes(0xa0);
        SizedPtr oldA = default;
        SizedPtr wantedB = f.Predict(2, 28 + failedRoot.Length);
        var failed = f.Open();
        f.Hooks.AfterHeadRecordAppended = ticket => oldA = ticket;
        f.Hooks.BeforeConditionalUpdateBegin = id => { if (id == 2) { throw new IOException("second Begin"); } };
        var outcome = PublicationOutcome.NotAttempted;
        Assert.Throws<IOException>(() => failed.PublishHeads(f.Changes((1, failedRoot), (2, failedRoot)), out outcome));
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.NotEqual(default, oldA);
        f.AssertColdOld(); // Observe genuine incompleteness before performing any fresh operation.

        int rootLength = sizeRelation switch { -1 => 1, 1 => 256, _ => replacement == "ordinary" ? 28 + failedRoot.Length : failedRoot.Length };
        byte[] freshB = ConditionalUpdateFaultFixture.Bytes(0xb0, rootLength);
        byte[] freshOther = ConditionalUpdateFaultFixture.Bytes(0xc0, rootLength);
        SizedPtr actualB;
        using (var writer = f.OpenPublic()) {
            if (replacement == "ordinary") { actualB = writer.PublishHead(f.Id(2), freshB, out outcome).Unwrap().Ticket; }
            else {
                uint other = replacement == "ab" ? 1u : 3u;
                var entryIds = writer.PublishHeads(f.Changes((other, freshOther), (2, freshB)), out outcome).Unwrap();
                actualB = entryIds[f.Id(2)].Ticket;
                if (other == 1) { Assert.NotEqual(oldA, entryIds[f.Id(other)].Ticket); }
            }
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        Assert.Equal(wantedB.Offset, actualB.Offset);
        Assert.Equal(sizeRelation == 0, wantedB.Length == actualB.Length);

        using var cold = f.OpenPublic(readOnly: true);
        Assert.Equal(replacement == "ab" ? freshOther : ConditionalUpdateFaultFixture.Old(1), cold.ReadHead(1u).Unwrap().Payload.ToArray());
        Assert.Equal(freshB, cold.ReadHead(2u).Unwrap().Payload.ToArray());
        Assert.All(ConditionalUpdateFaultFixture.History(cold, f.Id(1)), entry => {
            Assert.NotEqual(oldA, entry.Id.Ticket);
            Assert.False(entry.Payload.SequenceEqual(failedRoot));
        });
        Assert.DoesNotContain(ConditionalUpdateFaultFixture.History(cold, f.Id(2)), entry => entry.Payload.SequenceEqual(failedRoot));
        Assert.Equal(replacement == "ordinary" ? 4 : 5, cold.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("self")]
    [InlineData("duplicate")]
    [InlineData("short-meta")]
    [InlineData("unknown-kind")]
    [InlineData("later-header")]
    [InlineData("missing-ref")]
    [InlineData("wrong-ref-header")]
    [InlineData("wrong-store-header")]
    [InlineData("past-tail")]
    public void NecessaryMemberErrorsAreHardFailuresForCurrentAndHistoryAndNeverReturnTheOldValue(string defect) {
        using var f = new ConditionalUpdateFaultFixture();
        byte[] root = ConditionalUpdateFaultFixture.Bytes(0x80, 128);
        SizedPtr a = f.Predict(1, 28 + root.Length);
        SizedPtr b = f.Predict(2, 28 + root.Length);
        if (defect == "past-tail") { b = SizedPtr.Create(b.Offset + 100, b.Length); }
        var members = new[] { (1u, a), (2u, b) };
        Assert.Equal(a, f.Append(1, 2, ConditionalUpdateFaultFixture.Envelope(members, root)));
        if (defect == "missing-ref") { File.Delete(f.HeadPath(2)); }
        else if (defect is "wrong-ref-header" or "wrong-store-header") { RewritePeerHeader(f, members, root, defect); }
        else if (defect != "past-tail") {
            byte[] peer = ConditionalUpdateFaultFixture.Envelope(members, root);
            if (defect == "self") { BinaryPrimitives.WriteUInt64LittleEndian(peer.AsSpan(20), SizedPtr.Create(b.Offset + 4, b.Length).Packed); }
            if (defect == "duplicate") { BinaryPrimitives.WriteUInt32LittleEndian(peer.AsSpan(16), 1); }
            uint tag = defect == "unknown-kind" ? 19u : defect == "later-header" ? 0u : 2u;
            SizedPtr actual = f.Append(2, tag, peer, ConditionalUpdateFaultFixture.Metadata(defect == "short-meta" ? 7 : 8));
            Assert.Equal(b, actual);
            if (defect == "crc") { f.Flip(2, actual.Offset + actual.Length / 2); }
        }

        using var cold = f.OpenPublic(readOnly: true);
        var current = cold.ReadHead(f.Id(1));
        Assert.True(current.IsFailure);
        if (defect == "crc") { Assert.Equal("Rbf.CrcMismatch", current.Error!.ErrorCode); }
        var delivered = new List<HeadEntry>();
        var history = cold.ReadHeadHistory(f.Id(1), new(10, 1000), value => { delivered.Add(value); return true; });
        Assert.True(history.IsFailure);
        Assert.Empty(delivered);
    }

    [Fact]
    public void SameTicketValidDifferentMemberTableIsAFalseWitnessWithoutRecursingIntoTheReplacementGroup() {
        using var f = new ConditionalUpdateFaultFixture();
        byte[] root = ConditionalUpdateFaultFixture.Bytes(0x80);
        SizedPtr a = f.Predict(1, 28 + root.Length);
        SizedPtr b = f.Predict(2, 28 + root.Length);
        SizedPtr c = f.Predict(3, 28 + root.Length);
        f.Append(1, 2, ConditionalUpdateFaultFixture.Envelope([(1, a), (2, b)], root));
        f.Append(2, 2, ConditionalUpdateFaultFixture.Envelope([(2, b), (3, c)], root));
        var store = f.Open(readOnly: true);
        f.Operations.ConfigureOpen = file => { if (file.Id == 3) { throw new InvalidOperationException("Replacement group was recursively checked."); } };

        Assert.Equal(f.Initial[1], store.ReadHead(1u).Unwrap().Id);
        Assert.Equal(new[] { f.Initial[1] }, ConditionalUpdateFaultFixture.History(store, f.Id(1)).Select(value => value.Id));
        Assert.DoesNotContain(f.Operations.Files, file => file.Id == 3);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(768)]
    public void DifferentLengthReplacementWithBadUnreadPayloadCanDisproveOldTicketButDirectReadStillReportsCrc(int actualLength) {
        using var f = new ConditionalUpdateFaultFixture();
        CreatePartialMirror(f, 256);
        SizedPtr actual = f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xb0, actualLength));
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xc0, 128));
        f.Flip(2, actual.Offset + actual.Length / 2);

        using (var cold = f.OpenPublic(readOnly: true)) {
            Assert.Equal(f.Initial[1], cold.ReadHead(1u).Unwrap().Id);
            Assert.Single(ConditionalUpdateFaultFixture.History(cold, f.Id(1)));
            Assert.True(cold.Audit().IsFailure); // A scoped false witness did not certify the replacement payload.
        }
        using var rbf = RbfFile.OpenReadOnlyExisting(f.HeadPath(2), RbfCacheMode.Off);
        var read = rbf.ReadPooledFrame(actual);
        Assert.True(read.IsFailure);
        Assert.Equal("Rbf.CrcMismatch", read.Error!.ErrorCode);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(768)]
    public void DifferentLengthHistoricalReplacementWithBadHeadLengthCannotBeSwallowedByReverseTrailerScan(int actualLength) {
        using var f = new ConditionalUpdateFaultFixture();
        CreatePartialMirror(f, 256);
        SizedPtr actual = f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xb0, actualLength));
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xc0, 128));
        f.Flip(2, actual.Offset); // Mutate the publicly emitted HeadLen; no custom trailer or CRC construction.

        using var cold = f.OpenPublic(readOnly: true);
        Assert.True(cold.ReadHead(1u).IsFailure);
        var delivered = new List<HeadEntry>();
        Assert.True(cold.ReadHeadHistory(f.Id(1), new(10, 1000), entry => { delivered.Add(entry); return true; }).IsFailure);
        Assert.Empty(delivered);
    }

    internal static (SizedPtr A, SizedPtr B) CreatePartialMirror(ConditionalUpdateFaultFixture f, int rootLength = 64) {
        byte[] root = ConditionalUpdateFaultFixture.Bytes(0x80, rootLength);
        SizedPtr a = f.Predict(1, 28 + root.Length);
        SizedPtr b = f.Predict(2, 28 + root.Length);
        Assert.Equal(a, f.Append(1, 2, ConditionalUpdateFaultFixture.Envelope([(1, a), (2, b)], root)));
        return (a, b);
    }

    internal static (SizedPtr A, SizedPtr B) CreateCompleteMirror(ConditionalUpdateFaultFixture f) {
        byte[] root = ConditionalUpdateFaultFixture.Bytes(0x80);
        SizedPtr a = f.Predict(1, 28 + root.Length);
        SizedPtr b = f.Predict(2, 28 + root.Length);
        var members = new[] { (1u, a), (2u, b) };
        f.Append(1, 2, ConditionalUpdateFaultFixture.Envelope(members, root));
        f.Append(2, 2, ConditionalUpdateFaultFixture.Envelope(members, root));
        return (a, b);
    }

    private static void RewritePeerHeader(ConditionalUpdateFaultFixture f, (uint, SizedPtr)[] members, byte[] root, string defect) {
        File.Delete(f.HeadPath(2));
        StoreIdentity identity = f.Identity;
        if (defect == "wrong-store-header") { StoreIdentity.TryRead(Enumerable.Repeat((byte)0xee, 16).ToArray(), out identity); }
        HeadId headerId = HeadId.Create(identity, defect == "wrong-ref-header" ? 3u : 2u);
        byte[] header = new byte[HeadHeaderCodec.NoOriginPayloadSize];
        Assert.True(HeadHeaderCodec.TryWrite(identity, headerId, null, header));
        using var file = RbfFile.CreateNew(f.HeadPath(2), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        file.Append(1, ConditionalUpdateFaultFixture.Old(2), ConditionalUpdateFaultFixture.Metadata()).Unwrap();
        Assert.Equal(members[1].Item2, file.Append(2, ConditionalUpdateFaultFixture.Envelope(members, root), ConditionalUpdateFaultFixture.Metadata()).Unwrap());
        file.DurableFlush();
    }
}
