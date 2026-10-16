using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class TagRecordReaderTests {
    private const uint CollisionBucket = 0x106;

    [Fact]
    public void MetadataCrcIsCheckedAfterNonzeroEscapeWithUnalignedEntriesLength() {
        using var fixture = new TagBucketFixture();
        byte[] root = "RBF3!"u8.ToArray(); // Aligned Fence forces a nonzero key; meta starts at phase 1.
        TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare("A", -1, TagTestWire.Identity, 1);
        byte[] metadata = TagMetadataCodec.Encode(in plan);
        SizedPtr ticket;
        using (IRbfFile writer = fixture.Create(0x351)) {
            ticket = TagBucketFixture.Append(writer, "A", root, metadata);
            writer.DurableFlush();
        }
        byte[] physical = File.ReadAllBytes(fixture.Path);
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(physical.AsSpan(checked((int)ticket.Offset + ticket.Length - 4)));
        Assert.NotEqual(0u, key);
        Assert.False(metadata.AsSpan().SequenceEqual(physical.AsSpan(checked((int)ticket.Offset + 4 + root.Length), metadata.Length)));
        using IRbfFile reader = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off);
        CheckedTagBucketHeader header = CheckHeader(reader, 0x351);
        Assert.Equal(-1, TagRecordReader.ReadNamedMetadata(reader, header, "A").Unwrap().RecordedAtUnixMs);
        Assert.Equal("HeadStore.InvalidTagEntries", TagRecordReader.ReadNamed(reader, header, "A").Error!.ErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(87382)]
    public void ReadCopiesCanonicalMembersFromRealColdRbfFile(int length) {
        using var fixture = new TagBucketFixture();
        byte[] expected = TagTestWire.MemberSequence(length);
        using (IRbfFile writer = fixture.Create(CollisionBucket)) {
            TagBucketFixture.Append(writer, "tag-48", expected, TagTestWire.Metadata("tag-48", long.MinValue, (uint)length));
            TagBucketFixture.Append(writer, "tag-65", TagTestWire.Members(0x81));
            writer.DurableFlush();
        }
        HeadSetTag result;
        using (IRbfFile reader = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off)) {
            CheckedTagBucketHeader header = CheckHeader(reader, CollisionBucket);
            Assert.Equal(60, header.RecordsOffset);
            Assert.Equal(TagTestWire.Identity, header.Identity);
            Assert.Equal(CollisionBucket, header.BucketId);
            Assert.Equal(long.MinValue, TagRecordReader.ReadNamedMetadata(reader, header, "tag-48").Unwrap().RecordedAtUnixMs);
            result = TagRecordReader.ReadNamed(reader, header, "tag-48").Unwrap();
            Assert.Equal(TagTestWire.Members(0x81), TagRecordReader.ReadNamed(reader, header, "tag-65").Unwrap().MemberBytes());
        }
        Assert.Equal(length, result.Entries.Count);
        Assert.Equal(expected, result.MemberBytes());
    }

    [Fact]
    public void HeaderOnlyBucketIsHealthyMissingAndBareFenceIsNotAHeader() {
        using var empty = new TagBucketFixture();
        using (IRbfFile file = empty.Create(0x351)) {
            CheckedTagBucketHeader header = CheckHeader(file, 0x351);
            TagNameLookup lookup = TagRecordReader.ScanNamed(file, header, "A").Unwrap();
            Assert.False(lookup.UnknownName);
            Assert.Equal(0, lookup.MatchCount);
            Assert.Equal("HeadStore.TagNotFound", lookup.GetUniqueMetadata().Error!.ErrorCode);
            Assert.Equal("HeadStore.TagNotFound", TagRecordReader.ReadNamed(file, header, "A").Error!.ErrorCode);
        }
        using var bare = new TagBucketFixture();
        using IRbfFile bareFile = RbfFile.CreateNew(bare.Path, RbfCacheMode.Off);
        Assert.Equal("HeadStore.InvalidTagBucketHeader", TagRecordReader.CheckHeader(bareFile, TagTestWire.Identity, 0x351).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UniqueTrustedTargetResolvesWithUnknownNameBeforeOrAfterIt(bool targetFirst) {
        using var fixture = new TagBucketFixture();
        byte[] badMetadata = TagTestWire.Metadata("tag-65");
        badMetadata[0] ^= 1;
        using IRbfFile file = fixture.Create(CollisionBucket);
        if (targetFirst) { TagBucketFixture.Append(file, "tag-48", TagTestWire.Members(0xff)); }
        TagBucketFixture.Append(file, "tag-65", TagTestWire.Members(2), badMetadata);
        if (!targetFirst) { TagBucketFixture.Append(file, "tag-48", TagTestWire.Members(0xff)); }
        CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
        TagNameLookup lookup = TagRecordReader.ScanNamed(file, header, "tag-48").Unwrap();
        Assert.True(lookup.UnknownName);
        Assert.Equal(1, lookup.MatchCount);
        Assert.Equal("tag-48", lookup.GetUniqueMetadata().Unwrap().Name);
        Assert.Equal(TagTestWire.Members(0xff), TagRecordReader.ReadNamed(file, header, "tag-48").Unwrap().MemberBytes());
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("kind")]
    [InlineData("repeatedHeader")]
    [InlineData("route")]
    [InlineData("metaTooShort")]
    [InlineData("metaTooLong")]
    [InlineData("tombstone")]
    public void UntrustedRecordNameMakesZeroTargetUncertain(string corruption) {
        using var fixture = new TagBucketFixture();
        SizedPtr record;
        using (IRbfFile writer = fixture.Create(CollisionBucket)) {
            byte[] metadata = TagTestWire.Metadata("tag-65");
            uint tag = 1;
            byte[] root = [1];
            switch (corruption) {
                case "crc": metadata[^1] ^= 1; break;
                case "kind": tag = 2; break;
                case "repeatedHeader": tag = 0; root = TagTestWire.Header(CollisionBucket); metadata = []; break;
                case "route": metadata = TagTestWire.Metadata("A"); break;
                case "metaTooShort": metadata = new byte[17]; break;
                case "metaTooLong": metadata = new byte[3094]; break;
            }
            record = writer.Append(tag, root, metadata).Unwrap();
            TagBucketFixture.Append(writer, "tag-65", TagTestWire.Members(2)); // Healthy EOF remains independent of the earlier record.
            writer.DurableFlush();
        }
        if (corruption == "tombstone") { fixture.MakeTombstone(record); }
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off);
        CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
        TagNameLookup lookup = TagRecordReader.ScanNamed(file, header, "tag-48").Unwrap();
        Assert.True(lookup.UnknownName);
        Assert.Equal(0, lookup.MatchCount);
        Assert.Equal("HeadStore.TagLookupUncertain", lookup.GetUniqueMetadata().Error!.ErrorCode);
        Assert.Equal("HeadStore.TagLookupUncertain", TagRecordReader.ReadNamedMetadata(file, header, "tag-48").Error!.ErrorCode);
        Assert.Equal("HeadStore.TagLookupUncertain", TagRecordReader.ReadNamed(file, header, "tag-48").Error!.ErrorCode);
    }

    [Fact]
    public void DuplicateTargetUsesDecodedOrdinalNameAndSaturatesAtTwo() {
        using var fixture = new TagBucketFixture();
        using IRbfFile file = fixture.Create(0x351);
        TagBucketFixture.Append(file, "A", TagTestWire.Members(1));
        TagBucketFixture.Append(file, "A", TagTestWire.Members(2), TagTestWire.Metadata("A", nameWire: TagTestWire.Utf16Name("A")));
        TagBucketFixture.Append(file, "A", TagTestWire.Members(3), TagTestWire.Metadata("A", nameWire: TagTestWire.Utf8Name("A", 5)));
        CheckedTagBucketHeader header = CheckHeader(file, 0x351);
        TagNameLookup lookup = TagRecordReader.ScanNamed(file, header, "A").Unwrap();
        Assert.Equal(2, lookup.MatchCount);
        Assert.False(lookup.UnknownName);
        Assert.Equal("HeadStore.DuplicateTagName", lookup.GetUniqueMetadata().Error!.ErrorCode);
        Assert.Equal("HeadStore.DuplicateTagName", TagRecordReader.ReadNamedMetadata(file, header, "A").Error!.ErrorCode);
        Assert.Equal("HeadStore.DuplicateTagName", TagRecordReader.ReadNamed(file, header, "A").Error!.ErrorCode);
    }

    [Fact]
    public void OtherNameDuplicatesInSameBucketDoNotBlockRequestedNameOrProveItsAbsenceUncertain() {
        using var fixture = new TagBucketFixture();
        using IRbfFile file = fixture.Create(CollisionBucket);
        TagBucketFixture.Append(file, "tag-65", TagTestWire.Members(1));
        TagBucketFixture.Append(file, "tag-65", TagTestWire.Members(2));
        CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
        TagNameLookup missing = TagRecordReader.ScanNamed(file, header, "tag-48").Unwrap();
        Assert.False(missing.UnknownName);
        Assert.Equal("HeadStore.TagNotFound", missing.GetUniqueMetadata().Error!.ErrorCode);
        TagBucketFixture.Append(file, "tag-48", TagTestWire.Members(3));
        Assert.Equal(TagTestWire.Members(3), TagRecordReader.ReadNamed(file, header, "tag-48").Unwrap().MemberBytes());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("payloadCrc")]
    [InlineData("zero")]
    [InlineData("oversized")]
    public void TrustedMetadataKeepsNameOccupiedWhenTargetEntriesIsDamaged(string corruption) {
        using var fixture = new TagBucketFixture();
        int rootLength = corruption switch { "zero" => 0, "oversized" => 1048577, _ => 12 };
        SizedPtr target;
        using (IRbfFile writer = fixture.Create(CollisionBucket)) {
            target = TagBucketFixture.Append(writer, "tag-48", new byte[rootLength], TagTestWire.Metadata("tag-48", -99));
            TagBucketFixture.Append(writer, "tag-65", TagTestWire.Members(0x81));
            writer.DurableFlush();
        }
        if (corruption == "root") { fixture.FlipByte(target.Offset + 4 + rootLength / 2); }
        if (corruption == "payloadCrc") { fixture.FlipByte(target.Offset + target.Length - 24); }
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off);
        CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
        TagNameLookup lookup = TagRecordReader.ScanNamed(file, header, "tag-48").Unwrap();
        Assert.False(lookup.UnknownName);
        Assert.Equal(1, lookup.MatchCount); // The V3-B duplicate check must reject this occupied name without reading root.
        Assert.Equal(-99, lookup.GetUniqueMetadata().Unwrap().RecordedAtUnixMs);
        Assert.Equal("tag-48", TagRecordReader.ReadNamedMetadata(file, header, "tag-48").Unwrap().Name);
        var resolution = TagRecordReader.ReadNamed(file, header, "tag-48");
        if (corruption is "zero" or "oversized") { Assert.Equal("HeadStore.InvalidTagEntries", resolution.Error!.ErrorCode); }
        else { Assert.StartsWith("Rbf.", resolution.Error!.ErrorCode); }
        Assert.Equal(TagTestWire.Members(0x81), TagRecordReader.ReadNamed(file, header, "tag-65").Unwrap().MemberBytes());
    }

    [Fact]
    public void TrustedNonTargetWithBadMemberCrcIsNotReadDuringNamedResolution() {
        using var fixture = new TagBucketFixture();
        SizedPtr other;
        using (IRbfFile writer = fixture.Create(CollisionBucket)) {
            other = TagBucketFixture.Append(writer, "tag-65", new byte[256]);
            TagBucketFixture.Append(writer, "tag-48", TagTestWire.Members(0xff));
            writer.DurableFlush();
        }
        fixture.FlipByte(other.Offset + 100);
        string logPath = fixture.NewPath("reads");
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off)) {
            file.SetupReadLog(logPath);
            CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
            Assert.Equal(TagTestWire.Members(0xff), TagRecordReader.ReadNamed(file, header, "tag-48").Unwrap().MemberBytes());
        }
        var reads = TagBucketFixture.Reads(logPath);
        Assert.NotEmpty(reads);
        Assert.DoesNotContain(reads, read => read.Offset < other.Offset + 4 + 256 && read.Offset + read.Length > other.Offset + 4);
    }

    [Theory]
    [InlineData(23, 0, 0)]
    [InlineData(25, 0, 0)]
    [InlineData(24, 1, 0)]
    [InlineData(24, 0, 1)]
    public void InvalidFirstPhysicalHeaderShapeCannotBeReplacedByLaterHeader(int rootLength, int kind, int metadataLength) {
        using var fixture = new TagBucketFixture();
        using IRbfFile file = RbfFile.CreateNew(fixture.Path, RbfCacheMode.Off);
        file.Append((uint)kind, new byte[rootLength], new byte[metadataLength]).Unwrap();
        file.Append(0, TagTestWire.Header(0x351)).Unwrap();
        TagBucketFixture.Append(file, "A", TagTestWire.Members(1));
        Assert.Equal("HeadStore.InvalidTagBucketHeader", TagRecordReader.CheckHeader(file, TagTestWire.Identity, 0x351).Error!.ErrorCode);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("bucket")]
    [InlineData("crc")]
    [InlineData("tombstone")]
    public void NecessaryHeaderRejectsWrongContextOrDamageEvenWithHealthyTarget(string corruption) {
        using var fixture = new TagBucketFixture();
        byte[] payload = TagTestWire.Header(0x351);
        if (corruption == "version") { BinaryPrimitives.WriteUInt32LittleEndian(payload, 1); }
        if (corruption == "identity") { payload[4] ^= 1; }
        if (corruption == "bucket") { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 0x350); }
        SizedPtr headerTicket;
        using (IRbfFile writer = RbfFile.CreateNew(fixture.Path, RbfCacheMode.Off)) {
            headerTicket = writer.Append(0, payload).Unwrap();
            TagBucketFixture.Append(writer, "A", TagTestWire.Members(1));
            writer.DurableFlush();
        }
        if (corruption == "crc") { fixture.FlipByte(headerTicket.Offset + 5); }
        if (corruption == "tombstone") { fixture.MakeTombstone(headerTicket); }
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off);
        var result = TagRecordReader.CheckHeader(file, TagTestWire.Identity, 0x351);
        if (corruption == "crc") { Assert.StartsWith("Rbf.", result.Error!.ErrorCode); }
        else { Assert.Equal("HeadStore.InvalidTagBucketHeader", result.Error!.ErrorCode); }
    }

    [Theory]
    [InlineData("head")]
    [InlineData("trailer")]
    [InlineData("fence")]
    public void StructuralFailureAfterUniqueTargetPropagatesInsteadOfDeliveringTarget(string corruption) {
        using var fixture = new TagBucketFixture();
        SizedPtr middle;
        using (IRbfFile writer = fixture.Create(CollisionBucket)) {
            TagBucketFixture.Append(writer, "tag-48", TagTestWire.Members(1));
            middle = TagBucketFixture.Append(writer, "tag-65", TagTestWire.Members(2));
            TagBucketFixture.Append(writer, "tag-65", TagTestWire.Members(3));
            TagBucketFixture.Append(writer, "tag-65", TagTestWire.Members(4)); // Keep the damaged middle Fence outside factory's final-frame left-Fence check.
            writer.DurableFlush();
        }
        long offset = corruption switch {
            "head" => middle.Offset,
            "trailer" => middle.Offset + middle.Length - 20,
            _ => middle.Offset + middle.Length
        };
        fixture.FlipByte(offset);
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.Path, RbfCacheMode.Off);
        CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
        Assert.StartsWith("Rbf.", TagRecordReader.ScanNamed(file, header, "tag-48").Error!.ErrorCode);
        Assert.StartsWith("Rbf.", TagRecordReader.ReadNamed(file, header, "tag-48").Error!.ErrorCode);
    }

    [Fact]
    public void RecoveredMembersSurvivePooledReuseLaterQueriesAndFileDisposal() {
        using var fixture = new TagBucketFixture();
        HeadSetTag first;
        byte[] expected = TagTestWire.Members(17);
        using (IRbfFile file = fixture.Create(CollisionBucket)) {
            TagBucketFixture.Append(file, "tag-48", expected);
            TagBucketFixture.Append(file, "tag-65", TagTestWire.Members(18));
            CheckedTagBucketHeader header = CheckHeader(file, CollisionBucket);
            first = TagRecordReader.ReadNamed(file, header, "tag-48").Unwrap();
            Array.Clear(expected);
            for (int i = 0; i < 3; i++) {
                byte[] rented = ArrayPool<byte>.Shared.Rent(256);
                try { Array.Fill(rented, (byte)0xa5); }
                finally { ArrayPool<byte>.Shared.Return(rented); }
                Assert.Equal(TagTestWire.Members(18), TagRecordReader.ReadNamed(file, header, "tag-65").Unwrap().MemberBytes());
            }
        }
        Assert.Equal(TagTestWire.Members(17), first.MemberBytes());
    }

    [Fact]
    public void CheckedHeaderCannotBeImportedIntoAnotherHandle() {
        using var firstFixture = new TagBucketFixture();
        using var secondFixture = new TagBucketFixture();
        using IRbfFile first = firstFixture.Create(0x351);
        using IRbfFile second = secondFixture.Create(0x351);
        CheckedTagBucketHeader header = CheckHeader(first, 0x351);
        Assert.Throws<ArgumentException>(() => TagRecordReader.ScanNamed(second, header, "A"));
        Assert.Throws<ArgumentException>(() => TagRecordReader.ReadNamed(second, header, "A"));
    }

    private static CheckedTagBucketHeader CheckHeader(IRbfFile file, uint bucket) =>
        TagRecordReader.CheckHeader(file, TagTestWire.Identity, bucket).Unwrap();
}
