using Atelia.HeadStore.Tests.Tags;
using System.Buffers.Binary;
using Atelia.Rbf;
using Xunit;

namespace Atelia.HeadStore.Tests.TagPublic;

public sealed class PublicTagBucketAdmissionTests {
    public static IEnumerable<object[]> ShortBucketLengths() => Enumerable.Range(0, TagPublicFixture.InitializedLength)
        .Select(length => new object[] { length });

    [Theory]
    [MemberData(nameof(ShortBucketLengths))]
    public void EveryPrefixBeforeInitializedHeaderRejectsRepeatedWritersWithoutRepairOrAdoption(int length) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        fixture.WriteMirror("A");
        byte[] image = File.ReadAllBytes(fixture.BucketPath("A"));
        Assert.Equal(TagPublicFixture.InitializedLength, image.Length);
        File.WriteAllBytes(fixture.BucketPath("A"), image[..length]);
        var before = fixture.CaptureFiles();
        // This includes the complete header body missing its original Key and/or Fence.
        // Two cold owners prove no CompletedTail repair can turn the next attempt into adoption.
        for (int attempt = 0; attempt < 2; attempt++) {
            using (var writer = fixture.Open()) {
                Assert.Throws<InvalidDataException>(() => { writer.ReadTag("A"); });
                var outcome = PublicationOutcome.Confirmed;
                Assert.Throws<InvalidDataException>(() => { writer.CreateTag("A", [head], out outcome); });
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                Assert.Equal(new[] { head }, writer.ListHeads().Unwrap());
            }
            fixture.AssertUnchanged(before);
            fixture.AssertBucketReleased("A");
        }
        using (var readOnly = fixture.Open(readOnly: true)) {
            // Bare Fence can open successfully in RBF and fail as a missing VS header;
            // a physically short RBF image is rejected by its public factory.
            Exception? error = Record.Exception(() => { Assert.True(readOnly.ReadTag("A").IsFailure); });
            if (error is not null) { Assert.IsType<InvalidDataException>(error); }
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void HeaderOnlyFormalBucketIsHealthyMissingThenSupportsCreationInThatSameBucket() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        fixture.WriteMirror("A");
        byte[] headerImage = File.ReadAllBytes(fixture.BucketPath("A"));
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.TagNotFound", readOnly.ReadTag("A").Error!.ErrorCode);
        }
        Assert.Equal(headerImage, File.ReadAllBytes(fixture.BucketPath("A")));
        using (var writer = fixture.Open()) {
            Assert.True(writer.CreateTag("A", [head], out var outcome).IsSuccess);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(writer.ReadHead(head).Unwrap().Id, Assert.Single(writer.ReadTag("A").Unwrap().Entries).Value);
        }
        Assert.Equal(headerImage, File.ReadAllBytes(fixture.BucketPath("A"))[..headerImage.Length]);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("bucket")]
    [InlineData("trailing")]
    public void FullSizedButWrongHeaderNeverGrantsBucketIdentity(string kind) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        byte[] header = fixture.Header("A");
        switch (kind) {
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(header, 1); break;
            case "identity": header.AsSpan(4, 16).Clear(); break;
            case "bucket": BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), TagPublicFixture.Bucket("Release")); break;
            case "trailing": header = [.. header, 0]; break;
        }
        fixture.WriteMirrorHeader("A", header, new TagPublicRecord("A", TagTestWire.Members(1)));
        var before = fixture.CaptureFiles();
        foreach (bool readOnly in new[] { false, true }) {
            using (var store = fixture.Open(readOnly)) {
                Assert.Equal("HeadStore.InvalidTagBucketHeader", store.ReadTag("A").Error!.ErrorCode);
                if (!readOnly) {
                    Assert.True(store.CreateTag("A", [head], out var outcome).IsFailure);
                    Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                }
            }
            fixture.AssertUnchanged(before);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyTailAfterTheCompleteHeaderCanRecoverAndReadOnlyNeverChangesIt(bool completeRecordTail) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        if (completeRecordTail) { fixture.WriteMirror("A", new TagPublicRecord("A", TagTestWire.Members(0xff))); }
        else { fixture.WriteMirror("A"); }
        string path = fixture.BucketPath("A");
        byte[] complete = File.ReadAllBytes(path);
        // Recovery images, deliberately not Dispose or a process-crash simulation.
        File.WriteAllBytes(path, completeRecordTail ? complete[..^8] : [.. complete, 1]);
        Assert.True(new FileInfo(path).Length >= TagPublicFixture.InitializedLength);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadTag("A"); });
        }
        fixture.AssertUnchanged(before);
        using (var writer = fixture.Open()) {
            Assert.Equal(before[Path.GetRelativePath(fixture.Root, path)], File.ReadAllBytes(path)); // Still lazy.
            if (completeRecordTail) {
                Assert.Equal(TagTestWire.Members(0xff), writer.ReadTag("A").Unwrap().MemberBytes());
                Assert.True(writer.CreateTag("A", [head], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            else { Assert.Equal("HeadStore.TagNotFound", writer.ReadTag("A").Error!.ErrorCode); }
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
        using var cold = fixture.Open(readOnly: true);
        if (completeRecordTail) { Assert.Equal(TagTestWire.Members(0xff), cold.ReadTag("A").Unwrap().MemberBytes()); }
        else { Assert.Equal("HeadStore.TagNotFound", cold.ReadTag("A").Error!.ErrorCode); }
    }

    [Theory]
    [InlineData("unknown.txt", false)]
    [InlineData("00000106.RBF", false)]
    [InlineData("00000400.rbf", false)]
    [InlineData("00000abc.rbf", false)]
    [InlineData("0000010A.rbf", false)]
    [InlineData("00000000.rbf", true)]
    [InlineData("subdir", true)]
    public void AllDirectItemNamesAndTypesAreAdmittedBeforeNamedContentAccess(string name, bool directory) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        Directory.CreateDirectory(fixture.TagsPath);
        string path = Path.Combine(fixture.TagsPath, name);
        if (directory) { Directory.CreateDirectory(path); }
        else { File.WriteAllBytes(path, [0x81]); }
        var before = fixture.CaptureFiles();
        foreach (bool readOnly in new[] { false, true }) {
            using (var store = fixture.Open(readOnly)) {
                Assert.Throws<InvalidDataException>(() => { store.ReadTag("A"); });
                if (!readOnly) {
                    var outcome = PublicationOutcome.Confirmed;
                    Assert.Throws<InvalidDataException>(() => { store.CreateTag("A", [head], out outcome); });
                    Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                }
            }
            fixture.AssertUnchanged(before);
        }
    }

    [Fact]
    public void TagDirectoryAliasAndFileAreRejectedDuringRootAdmission() {
        foreach (bool alias in new[] { false, true }) {
            using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
            if (alias) { Directory.CreateDirectory(Path.Combine(fixture.Root, "Tags")); }
            else { File.WriteAllBytes(fixture.TagsPath, [1]); }
            var before = fixture.CaptureFiles();
            Assert.Throws<InvalidDataException>(() => { using var unexpected = fixture.Open(); });
            Assert.Throws<InvalidDataException>(() => { using var unexpected = fixture.Open(readOnly: true); });
            fixture.AssertUnchanged(before);
        }
    }

    [Fact]
    public void CanonicalZeroBucketNameIsAllowedWithoutReadingItsUnrelatedContents() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        Directory.CreateDirectory(fixture.TagsPath);
        File.WriteAllBytes(Path.Combine(fixture.TagsPath, "00000000.rbf"), [0xff]);
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.TagNotFound", store.ReadTag("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void PendingTagAttemptIsPreservedByReadOnlyAndCanceledWithoutAdoptionByWriter() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        File.WriteAllBytes(fixture.PendingPath, [0xff, 0, 0xfe]);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.TagNotFound", readOnly.ReadTag("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
        using (var writer = fixture.Open()) {
            Assert.False(File.Exists(fixture.PendingPath));
            Assert.False(Directory.Exists(fixture.TagsPath));
            Assert.Equal("HeadStore.TagNotFound", writer.ReadTag("A").Error!.ErrorCode);
            Assert.True(writer.CreateTag("A", [head], out var outcome).IsSuccess);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(cold.ReadHead(head).Unwrap().Id, Assert.Single(cold.ReadTag("A").Unwrap().Entries).Value);
    }
}
