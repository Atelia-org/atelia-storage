using System.Diagnostics.Tracing;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.TagScanLifecycle;

public sealed class TagScanCancellationAndPoolTests {
    [Fact]
    public void NullVisitorAndPreCancelledRequestDoNotEnumerateOrOpenAnyBucket() {
        using var f = new TagScanFaultFixture();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(f.Store.ScanTags(null!).IsFailure);
        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.ScanTags(_ => true, cancellation.Token)).CancellationToken);

        Assert.Empty(f.Operations.Enumerators);
        Assert.Empty(f.Tags.Opened);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
    }

    [Theory]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("eof")]
    [InlineData("before-close")]
    [InlineData("after-close")]
    public void CancellationAtDirectoryBoundariesPreventsSuccessAndLeavesWriterHealthy(string stage) {
        using var f = new TagScanFaultFixture();
        using var cancellation = new CancellationTokenSource();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        f.Operations.ConfigureEnumerator = cursor => {
            if (stage == "move") { cursor.AfterMoveNext = (_, present) => { if (present) { cancellation.Cancel(); } }; }
            if (stage == "current") { cursor.AfterCurrent = cancellation.Cancel; }
            if (stage == "eof") { cursor.AfterMoveNext = (_, present) => { if (!present) { cancellation.Cancel(); } }; }
            if (stage == "before-close") { cursor.BeforeDispose = cancellation.Cancel; }
            if (stage == "after-close") { cursor.AfterDispose = cancellation.Cancel; }
        };

        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.ScanTags(_ => true, cancellation.Token)).CancellationToken);

        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.All(f.Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        if (stage is "move" or "current") { Assert.Empty(f.Tags.Opened); }
        if (stage == "move") { Assert.Equal(0, Assert.Single(f.Operations.Enumerators).CurrentCalls); }
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Operations.ConfigureEnumerator = null;
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
    }

    [Theory]
    [InlineData("meta-read")]
    [InlineData("meta-rent")]
    public void CancellationAtActualMetadataWorkPreventsVisitorAndDoesNotReadLaterRecords(string stage) {
        using var f = new TagScanFaultFixture();
        using var cancellation = new CancellationTokenSource();
        var seed = f.SeedBucket(TagScanFaultFixture.Name,
            new TagPublicRecord(TagScanFaultFixture.Name, new byte[8192]),
            new TagPublicRecord(TagScanFaultFixture.SameBucketName, [2]));
        f.Order(TagScanFaultFixture.Name);
        f.Tags.ConfigureOpen = file => {
            file.BeforeRent = _ => { if (stage == "meta-rent") { cancellation.Cancel(); } };
            file.BeforeRead = (offset, length, raw) => {
                if (stage == "meta-read" && !raw && offset == seed.Tickets[0].Offset + 4 + 8192
                    && length == seed.Records[0].Metadata.Length) { cancellation.Cancel(); }
            };
        };
        int visitors = 0;

        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() =>
            f.Store.ScanTags(_ => { visitors++; return true; }, cancellation.Token)).CancellationToken);

        Assert.Equal(0, visitors);
        var file = Assert.Single(f.Tags.Opened);
        Assert.DoesNotContain(file.LoggedReads(), read => read.Offset >= seed.Tickets[1].Offset);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Tags.ConfigureOpen = null;
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
    }

    [Fact]
    public void ActualSmallMetadataBufferIsReturnedExactlyOnceBeforeVisitorRuns() {
        using var f = new TagScanFaultFixture();
        var seed = f.SeedBucket(TagScanFaultFixture.Name,
            new TagPublicRecord(TagScanFaultFixture.Name, new byte[8192]),
            new TagPublicRecord(TagScanFaultFixture.SameBucketName, [2]));
        f.Order(TagScanFaultFixture.Name);
        using var pool = new MetadataPoolListener();
        f.Tags.ConfigureOpen = file => file.BeforeRent = length => {
            Assert.InRange(length, 15, 3104);
            pool.BeginMetadata();
        };
        var retained = new List<TagInfo>();

        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(info => {
            // BCL pool events describe the actual Shared buffer, including reads via RbfFrameInfo.
            // This does not count proxy ReadPooledTailMeta calls that the scanner never uses.
            pool.AssertReturnedBeforeVisitor();
            retained.Add(info);
            return true;
        }).Unwrap());

        Assert.Equal(2, retained.Count);
        Assert.Equal(seed.Records.Select(record => record.Metadata.Length), Assert.Single(f.Tags.Opened).Rents);
        f.Store.Dispose();
        Assert.Equal(new[] { TagScanFaultFixture.Name, TagScanFaultFixture.SameBucketName }, retained.Select(info => info.Name));
    }

    [Fact]
    public void CancellationStaysPrimaryWhenBucketAndCursorCloseAlsoFail() {
        using var f = new TagScanFaultFixture();
        using var cancellation = new CancellationTokenSource();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        var bucketClose = new IOException("cancel bucket close");
        var cursorClose = new IOException("cancel cursor close");
        f.Tags.ConfigureOpen = file => file.DisposeFailure = bucketClose;
        f.Operations.ConfigureEnumerator = cursor => cursor.DisposeFailure = cursorClose;

        var observed = Assert.Throws<AggregateException>(() => f.Store.ScanTags(_ => {
            cancellation.Cancel();
            return false;
        }, cancellation.Token));

        Exception[] errors = observed.Flatten().InnerExceptions.ToArray();
        Assert.Equal(3, errors.Length);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(errors[0]).CancellationToken);
        Assert.Same(bucketClose, errors[1]);
        Assert.Same(cursorClose, errors[2]);
        f.AssertFaultedAndCleaned();
    }
}

/// <summary>只捕获本同步线程、单次 metadata rent 到 visitor 之间的实际 BCL Shared pool 事件。</summary>
internal sealed class MetadataPoolListener : EventListener {
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly List<(string Kind, int BufferId)> _events = [];
    private bool _capture;

    protected override void OnEventSourceCreated(EventSource source) {
        if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Verbose); }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data) {
        if (!_capture || Environment.CurrentManagedThreadId != _threadId
            || data.EventName is not ("BufferRented" or "BufferReturned")) { return; }
        _events.Add((data.EventName, Convert.ToInt32(data.Payload![0])));
    }

    internal void BeginMetadata() {
        _capture = false;
        _events.Clear();
        _capture = true;
    }

    internal void AssertReturnedBeforeVisitor() {
        _capture = false;
        var rented = Assert.Single(_events, entry => entry.Kind == "BufferRented");
        var returned = Assert.Single(_events, entry => entry.Kind == "BufferReturned");
        Assert.Equal(rented.BufferId, returned.BufferId);
        Assert.Equal(new[] { "BufferRented", "BufferReturned" }, _events.Select(entry => entry.Kind));
    }
}
