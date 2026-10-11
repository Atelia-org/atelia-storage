using System.Buffers.Binary;
using Atelia.Rbf;
using Xunit;

namespace Atelia.VersionStore.Tests.TagPublic;

public sealed class PublicTagBucketAdmissionTests {
    public static IEnumerable<object[]> ShortBucketLengths() => Enumerable.Range(0, TagPublicFixture.InitializedLength)
        .Select(length => new object[] { length });

    [Theory]
    [MemberData(nameof(ShortBucketLengths))]
    public void EveryPrefixBeforeInitializedHeaderRejectsRepeatedWritersWithoutRepairOrAdoption(int length) {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A");
        byte[] image = File.ReadAllBytes(fixture.BucketPath("A"));
        Assert.Equal(TagPublicFixture.InitializedLength, image.Length);
        File.WriteAllBytes(fixture.BucketPath("A"), image[..length]);
        var before = fixture.Snapshot();
        // This includes the complete header body missing its original Key and/or Fence.
        // Two cold owners prove no CompletedTail repair can turn the next attempt into adoption.
        for (int attempt = 0; attempt < 2; attempt++) {
            using (var writer = fixture.Open()) {
                Assert.Throws<InvalidDataException>(() => { writer.ResolveTag("A"); });
                var outcome = PublicationOutcome.Confirmed;
                Assert.Throws<InvalidDataException>(() => { writer.CreateTag("A", [1], out outcome); });
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                Assert.Empty(writer.ListRefs().Unwrap());
            }
            fixture.AssertUnchanged(before);
            fixture.AssertBucketReleased("A");
        }
        using (var readOnly = fixture.Open(readOnly: true)) {
            // Bare Fence can open successfully in RBF and fail as a missing VS header;
            // a physically short RBF image is rejected by its public factory.
            Exception? error = Record.Exception(() => { Assert.True(readOnly.ResolveTag("A").IsFailure); });
            if (error is not null) { Assert.IsType<InvalidDataException>(error); }
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void HeaderOnlyFormalBucketIsHealthyMissingThenSupportsCreationInThatSameBucket() {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A");
        byte[] headerImage = File.ReadAllBytes(fixture.BucketPath("A"));
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.TagNotFound", readOnly.ResolveTag("A").Error!.ErrorCode);
        }
        Assert.Equal(headerImage, File.ReadAllBytes(fixture.BucketPath("A")));
        using (var writer = fixture.Open()) {
            Assert.True(writer.CreateTag("A", [0xff], out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new byte[] { 0xff }, writer.ResolveTag("A").Unwrap().Bytes.ToArray());
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
        byte[] header = fixture.Header("A");
        switch (kind) {
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(header, 2); break;
            case "identity": header.AsSpan(4, 16).Clear(); break;
            case "bucket": BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), TagPublicFixture.Bucket("Release")); break;
            case "trailing": header = [.. header, 0]; break;
        }
        fixture.WriteMirrorHeader("A", header, new TagPublicRecord("A", [1]));
        var before = fixture.Snapshot();
        foreach (bool readOnly in new[] { false, true }) {
            using (var store = fixture.Open(readOnly)) {
                Assert.Equal("VersionStore.InvalidTagBucketHeader", store.ResolveTag("A").Error!.ErrorCode);
                if (!readOnly) {
                    Assert.True(store.CreateTag("A", [2], out var outcome).IsFailure);
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
        if (completeRecordTail) { fixture.WriteMirror("A", new TagPublicRecord("A", [0xff, 0, 0x81])); }
        else { fixture.WriteMirror("A"); }
        string path = fixture.BucketPath("A");
        byte[] complete = File.ReadAllBytes(path);
        // Recovery images, deliberately not Dispose or a process-crash simulation.
        File.WriteAllBytes(path, completeRecordTail ? complete[..^8] : [.. complete, 1]);
        Assert.True(new FileInfo(path).Length >= TagPublicFixture.InitializedLength);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ResolveTag("A"); });
        }
        fixture.AssertUnchanged(before);
        using (var writer = fixture.Open()) {
            Assert.Equal(before[Path.GetRelativePath(fixture.Root, path)], File.ReadAllBytes(path)); // Still lazy.
            if (completeRecordTail) {
                Assert.Equal(new byte[] { 0xff, 0, 0x81 }, writer.ResolveTag("A").Unwrap().Bytes.ToArray());
                Assert.True(writer.CreateTag("A", [2], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            else { Assert.Equal("VersionStore.TagNotFound", writer.ResolveTag("A").Error!.ErrorCode); }
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
        using var cold = fixture.Open(readOnly: true);
        if (completeRecordTail) { Assert.Equal(new byte[] { 0xff, 0, 0x81 }, cold.ResolveTag("A").Unwrap().Bytes.ToArray()); }
        else { Assert.Equal("VersionStore.TagNotFound", cold.ResolveTag("A").Error!.ErrorCode); }
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
        Directory.CreateDirectory(fixture.TagsPath);
        string path = Path.Combine(fixture.TagsPath, name);
        if (directory) { Directory.CreateDirectory(path); }
        else { File.WriteAllBytes(path, [0x81]); }
        var before = fixture.Snapshot();
        foreach (bool readOnly in new[] { false, true }) {
            using (var store = fixture.Open(readOnly)) {
                Assert.Throws<InvalidDataException>(() => { store.ResolveTag("A"); });
                if (!readOnly) {
                    var outcome = PublicationOutcome.Confirmed;
                    Assert.Throws<InvalidDataException>(() => { store.CreateTag("A", [1], out outcome); });
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
            if (alias) { Directory.CreateDirectory(Path.Combine(fixture.Root, "Tags")); }
            else { File.WriteAllBytes(fixture.TagsPath, [1]); }
            var before = fixture.Snapshot();
            Assert.Throws<InvalidDataException>(() => { using var unexpected = fixture.Open(); });
            Assert.Throws<InvalidDataException>(() => { using var unexpected = fixture.Open(readOnly: true); });
            fixture.AssertUnchanged(before);
        }
    }

    [Fact]
    public void CanonicalZeroBucketNameIsAllowedWithoutReadingItsUnrelatedContents() {
        using var fixture = new TagPublicFixture();
        Directory.CreateDirectory(fixture.TagsPath);
        File.WriteAllBytes(Path.Combine(fixture.TagsPath, "00000000.rbf"), [0xff]);
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.TagNotFound", store.ResolveTag("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void PendingTagAttemptIsPreservedByReadOnlyAndCanceledWithoutAdoptionByWriter() {
        using var fixture = new TagPublicFixture();
        File.WriteAllBytes(fixture.PendingPath, [0xff, 0, 0xfe]);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.TagNotFound", readOnly.ResolveTag("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
        using (var writer = fixture.Open()) {
            Assert.False(File.Exists(fixture.PendingPath));
            Assert.False(Directory.Exists(fixture.TagsPath));
            Assert.Equal("VersionStore.TagNotFound", writer.ResolveTag("A").Error!.ErrorCode);
            Assert.True(writer.CreateTag("A", [1], out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(new byte[] { 1 }, cold.ResolveTag("A").Unwrap().Bytes.ToArray());
    }
}
