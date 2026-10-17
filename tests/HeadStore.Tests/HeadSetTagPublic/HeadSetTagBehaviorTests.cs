using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Tests.ConditionalUpdateFaults;
using Atelia.HeadStore.Tests.Public;
using Atelia.HeadStore.Tests.TagPublic;
using Atelia.HeadStore.Tests.Tags;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.HeadSetTagPublic;

public sealed class HeadSetTagBehaviorTests {
    [Fact]
    public void SelectedSliceOwnsExactEntriesAcrossInputMutationAdvanceAndBothOwnersColdReopen() {
        using var f = new PublicHeadStoreFixture();
        HeadEntryId a;
        HeadEntryId b;
        HeadId child;
        HeadSetTag saved;
        using (var store = HeadStore.Create(f.Root, f.Data, 256)) {
            a = store.CreateHead(1, [0xa1], out _).Unwrap();
            b = store.CreateHead(2, [0xb1], out _).Unwrap();
            var c = store.CreateHead(3, [0xc1], out _).Unwrap();
            child = store.ForkHead(4, a, out _).Unwrap();
            HeadId[] input = [default, b.HeadId, child, a.HeadId, default];
            saved = store.CreateTag("selected", input.AsSpan(1, 3), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Array.Fill(input, c.HeadId);
            store.PublishHead(a.HeadId, [0xa2], out _).Unwrap();
            store.PublishHead(b.HeadId, [0xb2], out _).Unwrap();
            Assert.Equal(a, saved.Entries[a.HeadId]);
            Assert.Equal(b, saved.Entries[b.HeadId]);
            Assert.Equal(a, saved.Entries[child]);
            Assert.DoesNotContain(c.HeadId, saved.Entries.Keys);
        }
        f.Data.Dispose();
        Assert.Equal(3, saved.Entries.Count);
        using (var data = DataStore.Open(f.DataRoot))
        using (var writer = HeadStore.Open(f.Root, data)) {
            HeadSetTag cold = writer.ReadTag("selected").Unwrap();
            Assert.Equal(saved.Entries.OrderBy(p => p.Key.LocalId), cold.Entries.OrderBy(p => p.Key.LocalId));
            Assert.Equal(new byte[] { 0xa1 }, writer.ReadEntry(cold.Entries[a.HeadId]).Unwrap().Payload.ToArray());
            Assert.Equal(new byte[] { 0xb1 }, writer.ReadEntry(cold.Entries[b.HeadId]).Unwrap().Payload.ToArray());
            Assert.Equal(a, cold.Entries[child]);
            var fork = writer.ForkHead(5, cold.Entries[a.HeadId], out _).Unwrap();
            Assert.NotEqual(a.HeadId, fork);
            Assert.Equal(a, writer.ReadHead(fork).Unwrap().Id);
            Assert.Equal(new byte[] { 0xa1 }, writer.ReadHead(fork).Unwrap().Payload.ToArray());
            Assert.Equal(3, writer.ReadTagInfo("selected").Unwrap().EntryCount);
        }
        using var readData = DataStore.OpenReadOnly(f.DataRoot);
        using var reader = HeadStore.OpenReadOnly(f.Root, readData);
        Assert.Equal(1, reader.Audit().Unwrap().TagCount);
        Assert.Equal(saved.RecordedAtUnixMs, reader.ReadTag("selected").Unwrap().RecordedAtUnixMs);
    }

    [Fact]
    public void MultipleHeaderOnlyHeadsRetainIndependentTagKeysForOneSourceAcrossAdvanceAndColdAudit() {
        using var f = new PublicHeadStoreFixture();
        HeadEntryId source;
        HeadId child;
        HeadId sibling;
        HeadId grandchild;
        using (var writer = HeadStore.Create(f.Root, f.Data, 256)) {
            source = writer.CreateHead(1, [0xa1], out _).Unwrap();
            child = writer.ForkHead(2, source, out _).Unwrap();
            sibling = writer.ForkHead(3, source, out _).Unwrap();
            grandchild = writer.ForkHead(4, writer.ReadHead(child).Unwrap().Id, out _).Unwrap();
            var tag = writer.CreateTag("shared-source", [grandchild, sibling, child, source.HeadId], out _).Unwrap();
            Assert.Equal(new[] { source.HeadId, child, sibling, grandchild }, tag.Entries.Keys.OrderBy(id => id.LocalId));
            Assert.All(tag.Entries, pair => Assert.Equal(source, pair.Value));
            writer.PublishHead(source.HeadId, [0xa2], out _).Unwrap();
            writer.PublishHead(child, [0xc1], out _).Unwrap();
        }
        using var reader = HeadStore.OpenReadOnly(f.Root, f.Data);
        var restored = reader.ReadTag("shared-source").Unwrap();
        Assert.Equal(4, restored.Entries.Count);
        Assert.All(restored.Entries, pair => Assert.Equal(source, pair.Value));
        Assert.Equal(1, reader.Audit().Unwrap().TagCount);
        Assert.Equal(source, reader.ReadHead(sibling).Unwrap().Id);
        Assert.Equal(source, reader.ReadHead(grandchild).Unwrap().Id);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("default")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("missing")]
    public void InvalidSelectionCannotPublishAPartialNamedTable(string defect) {
        using var f = new ConditionalUpdateFaultFixture();
        using var other = new ConditionalUpdateFaultFixture();
        using var store = f.OpenPublic();
        HeadId[] selection = defect switch {
            "empty" => [],
            "default" => [f.Id(1), default],
            "duplicate" => [f.Id(1), f.Id(1)],
            "foreign" => [f.Id(1), other.Id(1)],
            _ => [f.Id(1), HeadId.Create(f.Identity, 99)]
        };
        var result = store.CreateTag("rejected", selection, out var outcome);
        Assert.True(result.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal("HeadStore.TagNotFound", store.ReadTag("rejected").Error!.ErrorCode);
        Assert.Equal(f.Initial[1], store.ReadHead(f.Id(1)).Unwrap().Id);
        store.CreateTag("healthy", [f.Id(1)], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void CuSubsetCapturesOnlySelectedHeadAndDoesNotFollowItsPeersLaterGroup() {
        using var f = new ConditionalUpdateFaultFixture();
        IReadOnlyDictionary<HeadId, HeadEntryId> ab;
        using (var writer = f.OpenPublic()) {
            ab = writer.PublishHeads(f.Changes((1, [0xa1]), (2, [0xb1])), out _).Unwrap();
            writer.PublishHeads(f.Changes((2, [0xb2]), (3, [0xc2])), out _).Unwrap();
        }
        File.Delete(f.HeadPath(3)); // A's AB dependency is B's old exact record, never B's later BC group.
        using (var writer = f.Open()) {
            f.Operations.ConfigureOpen = file => {
                if (file.Id == 3) { throw new InvalidOperationException("A later peer group was recursively checked."); }
            };
            var tag = writer.CreateTag("a-only", [f.Id(1)], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(ab[f.Id(1)], Assert.Single(tag.Entries).Value);
            Assert.DoesNotContain(f.Operations.Files, file => file.Id == 3);
        }
        using var reader = f.OpenPublic(readOnly: true);
        var restored = reader.ReadTag("a-only").Unwrap();
        Assert.Equal(new byte[] { 0xa1 }, reader.ReadEntry(restored.Entries[f.Id(1)]).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void CaptureAfterSingleAdvanceRetainsTheRealMixedCurrentCombination() {
        using var f = new ConditionalUpdateFaultFixture();
        using var writer = f.OpenPublic();
        var group = writer.PublishHeads(f.Changes((1, [0xa1]), (2, [0xb1])), out _).Unwrap();
        var currentA = writer.PublishHead(f.Id(1), [0xa2], out _).Unwrap();
        var tag = writer.CreateTag("current", [f.Id(2), f.Id(1)], out _).Unwrap();
        Assert.Equal(currentA, tag.Entries[f.Id(1)]);
        Assert.Equal(group[f.Id(2)], tag.Entries[f.Id(2)]);
    }

    [Fact]
    public void RecoverablePeerTailIsCompletedBeforeSelectingEitherCuMember() {
        using var f = new ConditionalUpdateFaultFixture();
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = f.OpenPublic()) {
            group = writer.PublishHeads(f.Changes((1, [0xa1]), (2, [0xb1])), out _).Unwrap();
        }
        byte[] complete = File.ReadAllBytes(f.HeadPath(2));
        File.WriteAllBytes(f.HeadPath(2), complete[..^8]);
        using (var writer = f.OpenPublic()) {
            var tag = writer.CreateTag("recovered", [f.Id(1), f.Id(2)], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(group[f.Id(1)], tag.Entries[f.Id(1)]);
            Assert.Equal(group[f.Id(2)], tag.Entries[f.Id(2)]);
            var report = Assert.Single(writer.RecoveryReports);
            Assert.Equal(HeadStoreRecoveryFileKind.Head, report.FileKind);
            Assert.Equal(2u, report.LocalId);
            Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Recovery.Action);
        }
        Assert.Equal(complete, File.ReadAllBytes(f.HeadPath(2)));
        using var reader = f.OpenPublic(readOnly: true);
        Assert.Equal(1, reader.Audit().Unwrap().TagCount);
    }

    [Theory]
    [InlineData("missing-head")]
    [InlineData("missing-ticket")]
    [InlineData("uncommitted-cu")]
    public void CanonicalTableCanBeReadWithoutGrantingItsMembersHealthOrAuditQualification(string defect) {
        using var f = new ConditionalUpdateFaultFixture();
        HeadEntryId member = f.Initial[1];
        if (defect == "uncommitted-cu") {
            SizedPtr a = f.Predict(1, 29);
            SizedPtr b = f.Predict(2, 29);
            Assert.Equal(a, f.Append(1, 2, ConditionalUpdateFaultFixture.Envelope([(1, a), (2, b)], [0xaa])));
            member = HeadEntryId.Create(f.Id(1), a);
            using var writer = f.OpenPublic();
            Assert.Equal(f.Initial[1], writer.CreateTag("fallback", [f.Id(1)], out _).Unwrap().Entries[f.Id(1)]);
        }
        else if (defect == "missing-ticket") {
            member = HeadEntryId.Create(f.Id(1), SizedPtr.Create(1_000_000, 40));
        }
        else { File.Delete(f.HeadPath(1)); }
        WriteTag(f, "broken-reference", member);
        using var reader = f.OpenPublic(readOnly: true);
        Assert.Equal(1, reader.ReadTagInfo("broken-reference").Unwrap().EntryCount);
        Assert.Equal(HeadSetTagScanEnd.Complete, reader.ScanTags(_ => true).Unwrap());
        HeadSetTag restored = reader.ReadTag("broken-reference").Unwrap();
        Assert.Equal(member, Assert.Single(restored.Entries).Value);
        Assert.True(reader.ReadEntry(member).IsFailure);
        Assert.True(reader.Audit().IsFailure);
    }

    [Theory]
    [InlineData("wrong-origin")]
    [InlineData("root-cross-owner")]
    [InlineData("missing-selected")]
    public void CanonicalCrossHeadTableIsReadableButAuditChecksSelectedHeadAndItsExactOrigin(string defect) {
        using var f = new ConditionalUpdateFaultFixture();
        HeadId child;
        using (var writer = f.OpenPublic()) { child = writer.ForkHead(4, f.Initial[1], out _).Unwrap(); }
        HeadId selected = defect == "root-cross-owner" ? f.Id(3) : child;
        HeadEntryId member = defect == "wrong-origin" ? f.Initial[2] : f.Initial[1];
        if (defect == "missing-selected") { File.Delete(f.HeadPath(4)); }
        WriteTag(f, "broken-selected-reference", member, selected);

        using var reader = f.OpenPublic(readOnly: true);
        HeadSetTag restored = reader.ReadTag("broken-selected-reference").Unwrap();
        Assert.Equal(member, restored.Entries[selected]);
        Assert.Equal(member, reader.ReadEntry(member).Unwrap().Id);
        Assert.True(reader.Audit().IsFailure);
    }

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 1u)]
    [InlineData(false, 2u)]
    [InlineData(true, 2u)]
    public void OldGateIsRejectedEvenWhenItHasNoTagsOrHeads(bool populated, uint version) {
        using var f = new PublicHeadStoreFixture();
        using (var writer = HeadStore.Create(f.Root, f.Data, 256)) {
            if (populated) { writer.CreateHead(1, [1], out _).Unwrap(); }
        }
        byte[] gate = File.ReadAllBytes(f.GatePath);
        BinaryPrimitives.WriteUInt32LittleEndian(gate, version);
        RollingCrc.SealCodewordForward(gate);
        File.WriteAllBytes(f.GatePath, gate);
        var before = f.CaptureFiles();
        Assert.Throws<InvalidDataException>(() => HeadStore.Open(f.Root, f.Data));
        Assert.Throws<InvalidDataException>(() => HeadStore.OpenReadOnly(f.Root, f.Data));
        f.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 1u)]
    [InlineData(false, 2u)]
    [InlineData(true, 2u)]
    public void VersionThreeGateDoesNotSilentlyAcceptAnOldHeadOrBucketHeader(bool bucket, uint version) {
        using var f = new ConditionalUpdateFaultFixture();
        string path;
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, version);
        f.Identity.TryWrite(header.AsSpan(4, 16));
        if (bucket) {
            uint id = TagPublicFixture.Bucket("old-header");
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), id);
            Directory.CreateDirectory(Path.Combine(f.Root, "tags"));
            path = Path.Combine(f.Root, "tags", $"{id:x8}.rbf");
        }
        else {
            path = f.HeadPath(1);
            File.Delete(path);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
        }
        using (IRbfFile file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            file.Append(0, header).Unwrap();
            if (!bucket) { file.Append(1, [1], new byte[8]).Unwrap(); }
            file.DurableFlush();
        }
        var before = f.CaptureFiles();
        using (var reader = f.OpenPublic(readOnly: true)) {
            Assert.Equal(f.Initial[2], reader.ReadHead(2u).Unwrap().Id); // Open remains lazy.
            if (bucket) { Assert.Equal("HeadStore.InvalidTagBucketHeader", reader.ReadTagInfo("old-header").Error!.ErrorCode); }
            else { Assert.Equal("HeadStore.InvalidHeadHeader", reader.ReadHead(1u).Error!.ErrorCode); }
            Assert.True(reader.Audit().IsFailure);
        }
        f.AssertUnchanged(before);
    }

    private static void WriteTag(ConditionalUpdateFaultFixture f, string name, HeadEntryId member, HeadId? selected = null) {
        uint bucket = TagPublicFixture.Bucket(name);
        Directory.CreateDirectory(Path.Combine(f.Root, "tags"));
        string path = Path.Combine(f.Root, "tags", $"{bucket:x8}.rbf");
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 3);
        f.Identity.TryWrite(header.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), bucket);
        using IRbfFile file = RbfFile.CreateNew(path, RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        file.Append(1, TagTestWire.Members((selected ?? member.HeadId).LocalId, member.Ticket, member.HeadId.LocalId), TagTestWire.Metadata(name)).Unwrap();
        file.DurableFlush();
    }
}
