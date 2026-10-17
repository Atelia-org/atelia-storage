using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.AuditFaults;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.AuditPublic;

public sealed class TagChainMembershipTests {
    [Fact]
    public void AuditRejectsATagTicketInsideAnotherFrameDespiteItsValidExactRead() {
        using var fixture = new AuditPublicFixture();
        SizedPtr outer = fixture.WriteHead(7, records: [new AuditHeadRecord(new byte[256])])[0];
        byte[] embeddedPayload = [0x31, 0x32, 0x33];
        // This deliberately illegal offline rewrite puts aligned Fence words inside the encoded body.
        // A normal RBF3 writer's marker-free output cannot produce this image. Audit must still
        // reject its nonmember reference; this does not strengthen the ordinary ReadEntry contract.
        SizedPtr embedded = fixture.EmbedReadableFrame(fixture.HeadPath(7), outer, embeddedPayload);
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(7), RbfCacheMode.Off)) {
            using var frame = file.ReadPooledFrame(embedded).Unwrap();
            Assert.Equal(embeddedPayload, frame.PayloadAndMeta[..^frame.TailMetaLength].ToArray());
            Assert.True(file.GetScanBoundaryAfter(embedded).IsSuccess);
            var forward = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(forward.MoveNext()); // Actual header.
            Assert.True(forward.MoveNext());
            Assert.Equal(outer, forward.Current.Ticket);
            Assert.False(forward.MoveNext());
            Assert.Null(forward.TerminationError);
        }
        fixture.Storage.WriteMirror("A", new TagPublicRecord("A", TagPublicFixture.Members(7, embedded)));
        var before = fixture.CaptureFiles();
        using (var reader = fixture.OpenReadOnly()) {
            var member = Assert.Single(reader.ReadTag("A").Unwrap().Entries);
            Assert.Equal(7u, member.Key.LocalId);
            Assert.Equal(member.Key, member.Value.HeadId);
            Assert.Equal(embedded, member.Value.Ticket);
            Assert.True(reader.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("start", false)]
    [InlineData("read", false)]
    [InlineData("start", true)]
    [InlineData("read", true)]
    public void MembershipScanCancellationCleansTheExistingMemberSlotAndPreservesPrimaryError(
        string stage, bool failClose) {
        using var f = new AuditFaultFixture();
        AuditHeadSeed seed = f.SeedHead(7, null, new byte[8192], [2]);
        f.SeedTagBucket("A", new TagPublicRecord("A", TagPublicFixture.Members(7, seed.Tickets[0])));
        using var cancellation = new CancellationTokenSource();
        var memberClose = new IOException("membership member close");
        var bucketClose = new IOException("membership bucket close");
        var cursorClose = new IOException("membership cursor close");
        var lockClose = new IOException("membership lock close");
        bool tagPhase = false;
        bool cancelledAtMembership = false;
        int exactReadsAtMembership = -1;
        AuditFile? memberReader = null;
        if (failClose) {
            f.Lock.DisposeFailure = lockClose;
            f.Operations.ConfigureCursor = (tag, cursor) => { if (tag) { cursor.DisposeFailure = cursorClose; } };
        }
        f.Files.ConfigureOpen = file => {
            if (file.IsTag) {
                tagPhase = true;
                if (failClose) { file.DisposeFailure = bucketClose; }
                return;
            }
            if (!tagPhase) { return; }
            memberReader = file;
            int forwardCalls = 0;
            bool membershipStarted = false;
            void Cancel() {
                cancelledAtMembership = true;
                cancellation.Cancel();
                if (failClose) { file.DisposeFailure = memberClose; }
            }
            file.AfterAccess = access => {
                if (access != "forward" || ++forwardCalls != 2) { return; }
                // The first scan checks this member handle's header. Exact/CU reading
                // succeeds before the second scan begins its physical membership proof.
                membershipStarted = true;
                exactReadsAtMembership = CompleteReadRequests(file, seed.Tickets[0]);
                if (stage == "start") { Cancel(); }
            };
            file.BeforeRead = (_, _, raw) => {
                if (stage == "read" && membershipStarted && !raw && !cancelledAtMembership) { Cancel(); }
            };
        };

        Exception observed = Assert.IsAssignableFrom<Exception>(Record.Exception(() => f.Store.Audit(cancellation.Token)));

        Exception[] errors = AuditFaultFixture.Flatten(observed);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(errors[0]).CancellationToken);
        Assert.True(cancelledAtMembership);
        Assert.NotNull(memberReader);
        Assert.True(exactReadsAtMembership > 0);
        Assert.Equal(exactReadsAtMembership, CompleteReadRequests(memberReader, seed.Tickets[0]));
        Assert.Equal(0, memberReader.CompleteReads(seed.Tickets[1]));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(0, f.Files.ActiveFiles);
        if (failClose) {
            Assert.Equal(new Exception[] { errors[0], memberClose, bucketClose, cursorClose, lockClose }, errors);
            f.AssertFaultedAndCleaned();
        }
        else {
            Assert.Single(errors);
            Assert.Equal(0, f.Lock.DisposeCalls);
            f.Files.ConfigureOpen = null;
            Assert.Equal(1, f.Store.Audit().Unwrap().TagCount);
        }
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MembershipScanReadExceptionStaysPrimaryAndClosesTheExistingSlotsOnce(bool failClose) {
        using var f = new AuditFaultFixture();
        AuditHeadSeed seed = f.SeedHead(7, null, new byte[8192]);
        f.SeedTagBucket("A", new TagPublicRecord("A", TagPublicFixture.Members(7, seed.Tickets[0])));
        var primary = new IOException("membership structural read");
        var memberClose = new IOException("membership member close");
        var bucketClose = new IOException("membership bucket close");
        bool tagPhase = false;
        bool failedAtMembership = false;
        int exactReadsAtMembership = -1;
        AuditFile? memberReader = null;
        f.Files.ConfigureOpen = file => {
            if (file.IsTag) {
                tagPhase = true;
                if (failClose) { file.DisposeFailure = bucketClose; }
                return;
            }
            if (!tagPhase) { return; }
            memberReader = file;
            int forwardCalls = 0;
            bool membershipStarted = false;
            file.AfterAccess = access => {
                if (access != "forward" || ++forwardCalls != 2) { return; }
                membershipStarted = true;
                exactReadsAtMembership = CompleteReadRequests(file, seed.Tickets[0]);
            };
            file.BeforeRead = (_, _, raw) => {
                if (!membershipStarted || raw) { return; }
                failedAtMembership = true;
                if (failClose) { file.DisposeFailure = memberClose; }
                throw primary;
            };
        };

        Exception observed = Assert.IsAssignableFrom<Exception>(Record.Exception(() => f.Store.Audit()));

        Exception[] errors = AuditFaultFixture.Flatten(observed);
        Assert.Same(primary, errors[0]);
        Assert.True(failedAtMembership);
        Assert.NotNull(memberReader);
        Assert.True(exactReadsAtMembership > 0);
        Assert.Equal(exactReadsAtMembership, CompleteReadRequests(memberReader, seed.Tickets[0]));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(0, f.Files.ActiveFiles);
        if (failClose) {
            Assert.Equal(new Exception[] { primary, memberClose, bucketClose }, errors);
            f.AssertFaultedAndCleaned();
        }
        else {
            Assert.Single(errors);
            Assert.Equal(0, f.Lock.DisposeCalls);
            f.Files.ConfigureOpen = null;
            Assert.Equal(1, f.Store.Audit().Unwrap().TagCount);
        }
        f.AssertNoWrites();
    }

    private static int CompleteReadRequests(AuditFile file, SizedPtr ticket) => file.Reads.Count(read =>
        !read.Raw && read.Offset == ticket.Offset && read.Length == ticket.Length);
}
