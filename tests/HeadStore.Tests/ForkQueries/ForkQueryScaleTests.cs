using Atelia.Data;
using Xunit;

namespace Atelia.HeadStore.Tests.ForkQueries;

public sealed class ForkQueryScaleTests {
    [Fact]
    public void BoundedDeepHeaderOnlyChainChecksEveryDeclaredEdgeWithReversedNumbers() {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        const uint nodes = 1024;
        var expected = new (HeadId Child, HeadId Source, SizedPtr Ticket)[nodes - 1];
        fixture.WriteMirror(nodes, null);
        for (uint child = nodes - 1; child > 0; child--) {
            HeadEntryId source = fixture.DeclaredPoint(child + 1);
            fixture.WriteMirror(child, source);
            expected[child - 1] = (fixture.Id(child), source.HeadId, source.Ticket);
        }
        using var readOnly = fixture.Open(readOnly: true);
        ForkQueryFixture.AssertEdges(readOnly.ListHeadOrigins((int)nodes).Unwrap(), expected);
        // A bounded real-file correctness vector; iterative linear graph traversal is also a
        // source-review requirement. This does not measure stack/CPU/RSS or certify load capacity.
        Assert.Equal("HeadStore.ForkQueryBudgetExceeded", readOnly.ListHeadOrigins((int)nodes - 1).Error!.ErrorCode);
    }

    [Fact]
    public void ManyBranchesSharingADeepAncestorPathProduceOneEdgePerChildAcrossRepeatedQueries() {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        const uint ancestors = 384;
        const uint branches = 192;
        var expected = new List<(HeadId Child, HeadId Source, SizedPtr Ticket)>();
        fixture.WriteMirror(ancestors, null);
        for (uint child = ancestors - 1; child > 0; child--) {
            HeadEntryId source = fixture.DeclaredPoint(child + 1);
            fixture.WriteMirror(child, source);
            expected.Add((fixture.Id(child), source.HeadId, source.Ticket));
        }
        for (uint branch = 1; branch <= branches; branch++) {
            uint child = ancestors + branch;
            HeadEntryId source = fixture.DeclaredPoint(1);
            fixture.WriteMirror(child, source);
            expected.Add((fixture.Id(child), source.HeadId, source.Ticket));
        }
        using var readOnly = fixture.Open(readOnly: true);
        IReadOnlyList<HeadOriginInfo> first = readOnly.ListHeadOrigins((int)(ancestors + branches)).Unwrap();
        ForkQueryFixture.AssertEdges(first, expected.ToArray());
        ForkQueryFixture.AssertEdges(readOnly.ListHeadOrigins(int.MaxValue).Unwrap(), expected.ToArray());
        readOnly.Dispose();
        ForkQueryFixture.AssertEdges(first, expected.ToArray());
    }
}
