using Atelia.Data;
using Atelia.RbfSegmentStore;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class EventJournalTests : IDisposable {
    private readonly List<string> _tempDirectories = new();

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    [Fact]
    public void HeaderCodec_RoundTripsFixedHeader() {
        var parent = new EventAddress(SizedPtr.Create(4, 32), 7, new AddressHint(0xAABBCCDD));
        var header = new EventFrameHeader(
            SequenceNumber: 42,
            UtcUnixTimeMilliseconds: 1_723_456_789,
            OpaqueEventKind: 99,
            Hint: new AddressHint(0x11223344),
            PayloadLength: 123,
            Parent: parent
        );

        Span<byte> buffer = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, buffer);

        EventFrameHeader decoded = EventFrameHeaderCodec.Decode(buffer).Unwrap();

        Assert.Equal(header, decoded);
    }

    [Fact]
    public void HeaderCodec_RejectsCrcMismatchAndHalfParent() {
        var header = new EventFrameHeader(1, 2, 3, default, 4, null);
        Span<byte> buffer = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, buffer);

        buffer[12] ^= 0x01;
        Assert.Equal("EventJournal.HeaderCrcMismatch", EventFrameHeaderCodec.Decode(buffer).Error!.ErrorCode);

        EventFrameHeaderCodec.Encode(in header, buffer);
        buffer[8] = 1;
        buffer[44] = 1;
        uint crc = Atelia.Data.Hashing.RollingCrc.CrcForward(buffer[..60]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer[60..64], crc);

        Assert.Equal("EventJournal.HeaderParentInvalid", EventFrameHeaderCodec.Decode(buffer).Error!.ErrorCode);
    }

    [Fact]
    public void AppendAndReadEvent_RoundTripsPayloadAndHeader() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress address = journal.AppendEventFrame(null, new byte[] { 1, 2, 3 }, opaqueEventKind: 12, hint: new AddressHint(0x100)).Unwrap();

        using EventFrame frame = journal.ReadEvent(address).Unwrap();

        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
        Assert.Equal<ulong>(1, frame.Header.SequenceNumber);
        Assert.Equal<uint>(12, frame.Header.OpaqueEventKind);
        Assert.Equal(new AddressHint(0x100), frame.Header.Hint);
        Assert.Null(frame.Header.Parent);
        Assert.True(File.Exists(Path.Combine(path, "events", "segments", "000000", "00000001.rbf")));
    }

    [Fact]
    public void AppendEventFrame_RejectsMissingParent() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);
        var missingParent = new EventAddress(SizedPtr.Create(4, 32), 99, default);

        var result = journal.AppendEventFrame(missingParent, new byte[] { 1 });

        Assert.True(result.IsFailure);
        Assert.Equal("EventJournal.ParentInvalid", result.Error!.ErrorCode);
    }

    [Fact]
    public void ReadEvent_RejectsHintMismatch() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);
        EventAddress address = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(1)).Unwrap();
        var wrongHint = address with { Hint = new AddressHint(2) };

        var result = journal.ReadEventHeaderPreview(wrongHint);

        Assert.True(result.IsFailure);
        Assert.Equal("EventJournal.HintMismatch", result.Error!.ErrorCode);
    }

    [Fact]
    public void ReadAncestorChain_CrossesSegmentRotation() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            SegmentStoreOptions = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8 }
        };
        using var journal = EventJournal.CreateNew(path, options);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress child = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();

        Assert.Equal<uint>(1, root.SegmentNumber);
        Assert.Equal<uint>(2, child.SegmentNumber);

        IReadOnlyList<EventAddress> reverse = journal.ReadAncestorChain(child).Unwrap();
        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(child).Unwrap();

        Assert.Equal(new[] { child, root }, reverse);
        Assert.Equal(new[] { root, child }, chronological);
        Assert.True(journal.IsAncestor(root, child).Unwrap());
    }

    [Fact]
    public void OpenExisting_ComputesNextSequenceNumberFromStoredFrames() {
        string path = NewJournalPath();
        EventAddress root;

        using (var journal = EventJournal.CreateNew(path)) {
            root = journal.AppendEventFrame(null, new byte[] { 1 }).Unwrap();
        }

        using var reopened = EventJournal.OpenExisting(path);
        EventAddress child = reopened.AppendEventFrame(root, new byte[] { 2 }).Unwrap();

        EventFrameHeader childHeader = reopened.ReadEventHeaderChecked(child).Unwrap();
        Assert.Equal<ulong>(2, childHeader.SequenceNumber);
        Assert.Equal(root, childHeader.Parent);
    }

    private string NewJournalPath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-event-journal-" + Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }
}
