using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using System.Buffers.Binary;
using System.Text;
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
            PayloadCodecId: EventPayloadCodecId.Brotli,
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
        var header = new EventFrameHeader(EventPayloadCodecId.Identity, 1, 2, 3, default, 4, null);
        Span<byte> buffer = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, buffer);

        buffer[12] ^= 0x01;
        Assert.Equal("EventJournal.HeaderCrcMismatch", EventFrameHeaderCodec.Decode(buffer).Error!.ErrorCode);

        EventFrameHeaderCodec.Encode(in header, buffer);
        buffer[44] = 1;
        uint crc = Atelia.Data.Hashing.RollingCrc.CrcForward(buffer[..60]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[60..64], crc);

        Assert.Equal("EventJournal.HeaderParentInvalid", EventFrameHeaderCodec.Decode(buffer).Error!.ErrorCode);
    }

    [Fact]
    public void HeaderCodec_RejectsReservedNonZero() {
        var header = new EventFrameHeader(EventPayloadCodecId.Identity, 1, 2, 3, default, 4, null);
        Span<byte> buffer = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, buffer);

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[60..64], Atelia.Data.Hashing.RollingCrc.CrcForward(buffer[..60]));

        Assert.Equal("EventJournal.HeaderReservedNonZero", EventFrameHeaderCodec.Decode(buffer).Error!.ErrorCode);

        EventFrameHeaderCodec.Encode(in header, buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..44], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[60..64], Atelia.Data.Hashing.RollingCrc.CrcForward(buffer[..60]));

        Assert.Equal("EventJournal.HeaderReservedNonZero", EventFrameHeaderCodec.Decode(buffer).Error!.ErrorCode);
    }

    [Fact]
    public void AppendAndReadEvent_RoundTripsPayloadAndHeader() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress address = journal.AppendEventFrame(null, new byte[] { 1, 2, 3 }, opaqueEventKind: 12, hint: new AddressHint(0x100)).Unwrap();

        using EventFrame frame = journal.ReadEvent(address).Unwrap();

        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
        Assert.Equal<ulong>(1, frame.Header.SequenceNumber);
        Assert.Equal(EventPayloadCodecId.Identity, frame.Header.PayloadCodecId);
        Assert.Equal<uint>(3, frame.Header.PayloadLength);
        Assert.Equal<uint>(12, frame.Header.OpaqueEventKind);
        Assert.Equal(new AddressHint(0x100), frame.Header.Hint);
        Assert.Null(frame.Header.Parent);
        Assert.True(File.Exists(Path.Combine(path, "events", "buckets", "000000", "00000001.rbf")));
    }

    [Fact]
    public void AppendAndReadEvent_BrotliRoundTripsLogicalPayload() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            PayloadCodecPolicy = EventPayloadCodecPolicy.Brotli with {
                MinimumPayloadLength = 0,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0.01
            }
        };
        using var journal = EventJournal.CreateNew(path, options);
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("简体中文 session journal payload; ", 256)));

        EventAddress address = journal.AppendEventFrame(null, payload, opaqueEventKind: 12).Unwrap();

        EventFrameHeader checkedHeader = journal.ReadEventHeaderChecked(address).Unwrap();
        using EventFrame frame = journal.ReadEvent(address).Unwrap();

        Assert.Equal(EventPayloadCodecId.Brotli, checkedHeader.PayloadCodecId);
        Assert.Equal(EventPayloadCodecId.Brotli, frame.Header.PayloadCodecId);
        Assert.Equal<uint>((uint)payload.Length, frame.Header.PayloadLength);
        Assert.True(address.Ticket.Length < payload.Length + EventFrameHeaderCodec.FixedLength);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void AppendAndReadEvent_ZlibRoundTripsLogicalPayload() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            PayloadCodecPolicy = EventPayloadCodecPolicy.Zlib with {
                MinimumPayloadLength = 0,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0.01
            }
        };
        using var journal = EventJournal.CreateNew(path, options);
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("简体中文 zlib session journal payload; ", 256)));

        EventAddress address = journal.AppendEventFrame(null, payload, opaqueEventKind: 12).Unwrap();

        EventFrameHeader checkedHeader = journal.ReadEventHeaderChecked(address).Unwrap();
        using EventFrame frame = journal.ReadEvent(address).Unwrap();

        Assert.Equal(EventPayloadCodecId.Zlib, checkedHeader.PayloadCodecId);
        Assert.Equal(EventPayloadCodecId.Zlib, frame.Header.PayloadCodecId);
        Assert.Equal<uint>((uint)payload.Length, frame.Header.PayloadLength);
        Assert.True(address.Ticket.Length < payload.Length + EventFrameHeaderCodec.FixedLength);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void AppendEventFrame_BrotliFallsBackForSmallPayload() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            PayloadCodecPolicy = EventPayloadCodecPolicy.Brotli with {
                MinimumPayloadLength = 1024
            }
        };
        using var journal = EventJournal.CreateNew(path, options);

        EventAddress address = journal.AppendEventFrame(null, new byte[] { 1, 2, 3 }).Unwrap();
        EventFrameHeader header = journal.ReadEventHeaderChecked(address).Unwrap();

        Assert.Equal(EventPayloadCodecId.Identity, header.PayloadCodecId);
        Assert.Equal<uint>(3, header.PayloadLength);
    }

    [Fact]
    public void AppendEventFrame_ZlibFallsBackForSmallPayload() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            PayloadCodecPolicy = EventPayloadCodecPolicy.Zlib with {
                MinimumPayloadLength = 1024
            }
        };
        using var journal = EventJournal.CreateNew(path, options);

        EventAddress address = journal.AppendEventFrame(null, new byte[] { 1, 2, 3 }).Unwrap();
        EventFrameHeader header = journal.ReadEventHeaderChecked(address).Unwrap();

        Assert.Equal(EventPayloadCodecId.Identity, header.PayloadCodecId);
        Assert.Equal<uint>(3, header.PayloadLength);
    }

    [Fact]
    public void AppendEventFrame_PerAppendWriteOptionsOverrideJournalDefault() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("单次写入覆盖默认压缩策略。", 256)));

        EventAddress address = journal.AppendEventFrame(
            null,
            payload,
            writeOptions: new EventPayloadWriteOptions(
                EventPayloadCodecPolicy.Brotli with {
                    MinimumPayloadLength = 0,
                    MinimumSavingsBytes = 1,
                    MinimumSavingsRatio = 0.01
                }
            )
        ).Unwrap();

        using EventFrame frame = journal.ReadEvent(address).Unwrap();

        Assert.Equal(EventPayloadCodecId.Brotli, frame.Header.PayloadCodecId);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void ReadEvent_UnsupportedCodecFailsButCheckedHeaderSucceeds() {
        string path = NewJournalPath();
        EventAddress address;
        using (var journal = EventJournal.CreateNew(path)) {
            address = journal.AppendEventFrame(null, new byte[] { 1, 2, 3 }).Unwrap();
        }

        RewriteEventHeaderPayloadCodec(path, address, (EventPayloadCodecId)32768, payloadLength: 3);

        using var reopened = EventJournal.OpenExisting(path);
        EventFrameHeader header = reopened.ReadEventHeaderChecked(address).Unwrap();
        var readResult = reopened.ReadEvent(address);

        Assert.Equal((EventPayloadCodecId)32768, header.PayloadCodecId);
        Assert.True(readResult.IsFailure);
        Assert.Equal("EventJournal.PayloadCodecUnsupported", readResult.Error!.ErrorCode);
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
            EventSegmentStoreOptions = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8 }
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
    public void BuildEphemeralForwardPlan_LinearSameSegment_HasNoRedirects() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress middle = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        EventAddress head = journal.AppendEventFrame(middle, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();

        EphemeralForwardPlan plan = journal.BuildEphemeralForwardPlan(head).Unwrap();
        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(head).Unwrap();

        Assert.Equal(root, plan.RootEvent);
        Assert.Equal(head, plan.TargetHead);
        Assert.Equal<ulong>(3, plan.EventCount);
        Assert.Empty(plan.Redirects);
        Assert.Equal(new[] { root, middle, head }, chronological);
    }

    [Fact]
    public void BuildEphemeralForwardPlan_OrphanBetweenParentAndChild_AddsRedirectAndReplaySkipsIt() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress orphan = journal.AppendEventFrame(root, new byte[] { 99 }, hint: new AddressHint(0x99)).Unwrap();
        EventAddress head = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();

        EphemeralForwardPlan plan = journal.BuildEphemeralForwardPlan(head).Unwrap();
        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();

        RouteRedirect redirect = Assert.Single(plan.Redirects);
        Assert.Equal(root, redirect.FromEvent);
        Assert.Equal(head, redirect.ToChild);
        Assert.Equal<ulong>(2, plan.EventCount);
        Assert.Equal(new[] { root, head }, chronological);
        Assert.DoesNotContain(orphan, chronological);
    }

    [Fact]
    public void BuildEphemeralForwardPlan_CrossSegmentEdge_AddsRedirectAndReplays() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            EventSegmentStoreOptions = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8 }
        };
        using var journal = EventJournal.CreateNew(path, options);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress head = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();

        EphemeralForwardPlan plan = journal.BuildEphemeralForwardPlan(head).Unwrap();
        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(head).Unwrap();

        Assert.NotEqual(root.SegmentNumber, head.SegmentNumber);
        RouteRedirect redirect = Assert.Single(plan.Redirects);
        Assert.Equal(root, redirect.FromEvent);
        Assert.Equal(head, redirect.ToChild);
        Assert.Equal(new[] { root, head }, chronological);
    }

    [Fact]
    public void BuildEphemeralForwardPlan_ReusesExactHeadPlanCache() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress head = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();

        EphemeralForwardPlan first = journal.BuildEphemeralForwardPlan(head).Unwrap();
        EphemeralForwardPlan second = journal.BuildEphemeralForwardPlan(head).Unwrap();

        Assert.Same(first, second);
        Assert.Equal(1, journal.ForwardPlanCacheEntryCount);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.Misses);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.ExactHits);
    }

    [Fact]
    public void BuildEphemeralForwardPlan_ReusesCachedAncestorPrefixForAdvance() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress middle = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        EventAddress orphan = journal.AppendEventFrame(middle, new byte[] { 99 }, hint: new AddressHint(0x99)).Unwrap();
        EventAddress head = journal.AppendEventFrame(middle, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();

        EphemeralForwardPlan prefix = journal.BuildEphemeralForwardPlan(middle).Unwrap();
        EphemeralForwardPlan plan = journal.BuildEphemeralForwardPlan(head).Unwrap();
        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(head).Unwrap();

        Assert.Equal(root, prefix.RootEvent);
        Assert.Equal(root, plan.RootEvent);
        Assert.Equal<ulong>(3, plan.EventCount);
        RouteRedirect redirect = Assert.Single(plan.Redirects);
        Assert.Equal(middle, redirect.FromEvent);
        Assert.Equal(head, redirect.ToChild);
        Assert.Equal(new[] { root, middle, head }, chronological);
        Assert.DoesNotContain(orphan, chronological);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.PrefixHits);
        Assert.Equal(2, journal.ForwardPlanCacheEntryCount);
    }

    [Fact]
    public void BuildEphemeralForwardPlan_CacheHitStillHonorsMaxDepth() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress head = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        journal.BuildEphemeralForwardPlan(head).Unwrap();

        var result = journal.BuildEphemeralForwardPlan(head, maxDepth: 1);

        Assert.True(result.IsFailure);
        Assert.Equal("EventJournal.TraversalDepthExceeded", result.Error!.ErrorCode);
    }

    [Fact]
    public void CompiledForwardPlanCache_ReopenLoadsDiskPlanForExactHead() {
        string path = NewJournalPath();
        EventAddress root;
        EventAddress head;

        using (var journal = EventJournal.CreateNew(path)) {
            root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
            head = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
            _ = journal.ReadChronologicalChain(head).Unwrap();
            Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.DiskWrites);
        }

        using var reopened = EventJournal.OpenExisting(path);
        IReadOnlyList<EventAddress> chronological = reopened.ReadChronologicalChain(head).Unwrap();

        Assert.Equal(new[] { root, head }, chronological);
        Assert.Equal<ulong>(1, reopened.ForwardPlanCacheStats.DiskHits);
        Assert.Equal<ulong>(0, reopened.ForwardPlanCacheStats.Misses);
    }

    [Fact]
    public void CompiledForwardPlanCache_ReadByRefUsesCurrentHeadAndMissesAfterMove() {
        string path = NewJournalPath();
        RefId main;
        EventAddress root;
        EventAddress oldHead;
        EventAddress newHead;

        using (var journal = EventJournal.CreateNew(path)) {
            root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
            oldHead = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
            main = journal.CreateBranch("main", oldHead).Unwrap();
            _ = journal.ReadChronologicalChain(main).Unwrap();
            newHead = journal.AppendEventFrame(oldHead, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();
            Assert.True(journal.AdvanceRef(main, oldHead, newHead).Unwrap());
        }

        using var reopened = EventJournal.OpenExisting(path);
        IReadOnlyList<EventAddress> chronological = reopened.ReadChronologicalChain(main).Unwrap();

        Assert.Equal(new[] { root, oldHead, newHead }, chronological);
        Assert.Equal<ulong>(0, reopened.ForwardPlanCacheStats.DiskHits);
        Assert.Equal<ulong>(1, reopened.ForwardPlanCacheStats.Misses);
        Assert.Equal<ulong>(1, reopened.ForwardPlanCacheStats.DiskWrites);
    }

    [Fact]
    public void CompiledForwardPlanCache_CorruptFileIsDeletedAndRebuilt() {
        string path = NewJournalPath();
        EventAddress head;

        using (var journal = EventJournal.CreateNew(path)) {
            EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
            head = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
            _ = journal.ReadChronologicalChain(head).Unwrap();
        }

        string cacheFile = Directory.GetFiles(Path.Combine(path, "cache", "forward-plans", "v1"), "*.efplan").Single();
        byte[] bytes = File.ReadAllBytes(cacheFile);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(cacheFile, bytes);

        using var reopened = EventJournal.OpenExisting(path);
        _ = reopened.ReadChronologicalChain(head).Unwrap();

        Assert.Equal<ulong>(0, reopened.ForwardPlanCacheStats.DiskHits);
        Assert.Equal<ulong>(1, reopened.ForwardPlanCacheStats.Misses);
        Assert.Equal<ulong>(1, reopened.ForwardPlanCacheStats.DiskWrites);
        Assert.True(File.Exists(cacheFile));
    }

    [Fact]
    public void ForwardPlanTailMerge_ReadByRefAppendsSuffixFromBoundPlan() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress oldHead = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        RefId main = journal.CreateBranch("main", oldHead).Unwrap();

        Assert.Equal(new[] { root, oldHead }, journal.ReadChronologicalChain(main).Unwrap());

        EventAddress newHead = journal.AppendEventFrame(oldHead, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();
        Assert.True(journal.AdvanceRef(main, oldHead, newHead).Unwrap());

        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(main).Unwrap();

        Assert.Equal(new[] { root, oldHead, newHead }, chronological);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.TailMergeHits);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.Misses);
    }

    [Fact]
    public void ForwardPlanTailMerge_ReadByRefRewindsToAncestorByTrimmingOldSuffix() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress middle = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        EventAddress oldHead = journal.AppendEventFrame(middle, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();
        RefId main = journal.CreateBranch("main", oldHead).Unwrap();

        Assert.Equal(new[] { root, middle, oldHead }, journal.ReadChronologicalChain(main).Unwrap());
        Assert.True(journal.MoveRef(main, oldHead, middle).Unwrap());

        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(main).Unwrap();

        Assert.Equal(new[] { root, middle }, chronological);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.TailMergeHits);
    }

    [Fact]
    public void ForwardPlanTailMerge_ReadByRefRewindsThenForksAtCommonAncestor() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress shared = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        EventAddress oldHead = journal.AppendEventFrame(shared, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();
        RefId main = journal.CreateBranch("main", oldHead).Unwrap();

        Assert.Equal(new[] { root, shared, oldHead }, journal.ReadChronologicalChain(main).Unwrap());

        EventAddress newMiddle = journal.AppendEventFrame(shared, new byte[] { 99 }, hint: new AddressHint(0x99)).Unwrap();
        EventAddress newHead = journal.AppendEventFrame(newMiddle, new byte[] { 4 }, hint: new AddressHint(0x40)).Unwrap();
        Assert.True(journal.MoveRef(main, oldHead, newHead).Unwrap());

        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(main, checkedRead: true).Unwrap();

        Assert.Equal(new[] { root, shared, newMiddle, newHead }, chronological);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.TailMergeHits);
    }

    [Fact]
    public void ForwardPlanTailMerge_DoesNotTreatEarlierOrphanSiblingAsCommonPoint() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }, hint: new AddressHint(0x10)).Unwrap();
        EventAddress shared = journal.AppendEventFrame(root, new byte[] { 2 }, hint: new AddressHint(0x20)).Unwrap();
        EventAddress orphanSibling = journal.AppendEventFrame(shared, new byte[] { 99 }, hint: new AddressHint(0x99)).Unwrap();
        EventAddress oldPathMiddle = journal.AppendEventFrame(shared, new byte[] { 3 }, hint: new AddressHint(0x30)).Unwrap();
        EventAddress oldHead = journal.AppendEventFrame(oldPathMiddle, new byte[] { 4 }, hint: new AddressHint(0x40)).Unwrap();
        RefId main = journal.CreateBranch("main", oldHead).Unwrap();

        Assert.Equal(new[] { root, shared, oldPathMiddle, oldHead }, journal.ReadChronologicalChain(main).Unwrap());

        EventAddress newHead = journal.AppendEventFrame(orphanSibling, new byte[] { 5 }, hint: new AddressHint(0x50)).Unwrap();
        Assert.True(journal.MoveRef(main, oldHead, newHead).Unwrap());

        IReadOnlyList<EventAddress> chronological = journal.ReadChronologicalChain(main, checkedRead: true).Unwrap();

        Assert.Equal(new[] { root, shared, orphanSibling, newHead }, chronological);
        Assert.DoesNotContain(oldPathMiddle, chronological);
        Assert.DoesNotContain(oldHead, chronological);
        Assert.Equal<ulong>(1, journal.ForwardPlanCacheStats.TailMergeHits);
    }

    [Fact]
    public void RefMoveStore_RotatesAndReplaysMovesThroughEventJournal() {
        string path = NewJournalPath();
        var options = new EventJournalOptions {
            RefSegmentStoreOptions = new RbfSegmentStoreOptions {
                SegmentSizeThresholdBytes = 8
            }
        };
        RefId refId;

        using (var journal = EventJournal.CreateNew(path, options)) {
            refId = journal.CreateBranch("main", startPoint: null).Unwrap();
            Assert.True(journal.MoveRef(refId, expectedOldHead: null, newHead: null, reasonKind: 7).Unwrap());
        }

        using var reopened = EventJournal.OpenExisting(path, options);
        IReadOnlyList<RefMoveFrame> moves = reopened.ReadReflog(refId).Unwrap();

        Assert.Equal(2, moves.Count);
        Assert.Equal(RefMoveOperation.Init, moves[0].Operation);
        Assert.Equal<ulong>(2, moves[1].MoveSequenceNumber);
        Assert.True(File.Exists(Path.Combine(path, "refs", "objects", refId.ToHexString(), "segments", "00000001.rbf")));
        Assert.True(File.Exists(Path.Combine(path, "refs", "objects", refId.ToHexString(), "segments", "00000002.rbf")));
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

    [Fact]
    public void CreateBranch_OpenBranchAndReopenKeepStableRefIdAndHead() {
        string path = NewJournalPath();
        EventAddress root;
        RefId refId;

        using (var journal = EventJournal.CreateNew(path)) {
            root = journal.AppendEventFrame(null, new byte[] { 1 }).Unwrap();
            refId = journal.CreateBranch("main", root).Unwrap();

            Assert.Equal(refId, journal.OpenBranch("main").Unwrap());
            Assert.Equal(root, journal.GetHead(refId));
        }

        using var reopened = EventJournal.OpenExisting(path);

        Assert.Equal(refId, reopened.OpenBranch("main").Unwrap());
        Assert.Equal(root, reopened.GetHead(refId));
        Assert.Equal(new[] { "main" }, reopened.ListBranches());
    }

    [Fact]
    public void AdvanceRef_CasMismatchDoesNotWriteMove() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        RefId refId = journal.CreateBranch("main", startPoint: null).Unwrap();
        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }).Unwrap();
        Assert.True(journal.AdvanceRef(refId, expectedOldHead: null, root).Unwrap());

        EventAddress child = journal.AppendEventFrame(root, new byte[] { 2 }).Unwrap();
        var failedAdvance = journal.AdvanceRef(refId, expectedOldHead: null, child);

        Assert.True(failedAdvance.IsFailure);
        Assert.Equal("EventJournal.RefCasMismatch", failedAdvance.Error!.ErrorCode);
        Assert.Equal(root, journal.GetHead(refId));
        Assert.Equal(2, journal.ReadReflog(refId).Unwrap().Count);
    }

    [Fact]
    public void ArchiveRef_RecreateSameNameGetsNewRefId() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }).Unwrap();
        RefId first = journal.CreateBranch("main", root).Unwrap();

        Assert.True(journal.MoveRef(first, expectedOldHead: root, newHead: null).Unwrap());
        Assert.True(journal.ArchiveRef(first, expectedOldHead: null).Unwrap());
        RefId second = journal.CreateBranch("main", root).Unwrap();

        Assert.NotEqual(first, second);
        Assert.Equal(second, journal.OpenBranch("main").Unwrap());
        Assert.Equal(root, journal.GetHead(second));
    }

    [Fact]
    public void ArchiveRef_ReopenRemovesBranchBinding() {
        string path = NewJournalPath();
        EventAddress root;
        RefId main;

        using (var journal = EventJournal.CreateNew(path)) {
            root = journal.AppendEventFrame(null, new byte[] { 1 }).Unwrap();
            main = journal.CreateBranch("main", root).Unwrap();
            Assert.True(journal.ArchiveRef(main, root).Unwrap());
        }

        using var reopened = EventJournal.OpenExisting(path);

        Assert.Empty(reopened.ListBranches());
        Assert.True(reopened.OpenBranch("main").IsFailure);
    }

    [Fact]
    public void ForkBranch_CreatesIndependentNamedRefAtSourceHead() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        EventAddress root = journal.AppendEventFrame(null, new byte[] { 1 }).Unwrap();
        RefId main = journal.CreateBranch("main", root).Unwrap();
        RefId feature = journal.ForkBranch("feature", main, root).Unwrap();

        Assert.NotEqual(main, feature);
        Assert.Equal(root, journal.GetHead(feature));
        Assert.Equal(new[] { "feature", "main" }, journal.ListBranches());
    }

    [Fact]
    public void CommitToRef_AppendsEventAndAdvancesBranch() {
        string path = NewJournalPath();
        using var journal = EventJournal.CreateNew(path);

        RefId main = journal.CreateBranch("main", startPoint: null).Unwrap();
        CommitToRefOutcome first = journal.CommitToRef("main", expectedHead: null, new byte[] { 1 }).Unwrap();
        CommitToRefOutcome second = journal.CommitToRef("main", expectedHead: first.EventAddress, new byte[] { 2 }).Unwrap();

        Assert.Equal(main, first.RefId);
        Assert.Equal(main, second.RefId);
        Assert.Equal(second.EventAddress, journal.GetHead(main));
        using EventFrame secondFrame = journal.ReadEvent(second.EventAddress).Unwrap();
        Assert.Equal(new byte[] { 2 }, secondFrame.Payload.ToArray());
        Assert.Equal(first.EventAddress, secondFrame.Header.Parent);
    }

    private string NewJournalPath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-event-journal-" + Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private static void RewriteEventHeaderPayloadCodec(string journalPath, EventAddress address, EventPayloadCodecId codecId, uint payloadLength) {
        string segmentPath = Path.Combine(journalPath, "events", "buckets", "000000", $"{address.SegmentNumber:X8}.rbf".ToLowerInvariant());
        using IRbfFile file = RbfFile.OpenExisting(segmentPath);
        using var frameResult = file.ReadPooledFrame(address.Ticket).ToDisposable();
        RbfPooledFrame frame = frameResult.Unwrap();

        byte[] storedPayload = frame.PayloadAndMeta[..^frame.TailMetaLength].ToArray();
        Span<byte> tailMeta = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        frame.PayloadAndMeta[^frame.TailMetaLength..].CopyTo(tailMeta);
        frame.Dispose();

        BinaryPrimitives.WriteUInt16LittleEndian(tailMeta[8..10], (ushort)codecId);
        BinaryPrimitives.WriteUInt32LittleEndian(tailMeta[36..40], payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(tailMeta[60..64], Atelia.Data.Hashing.RollingCrc.CrcForward(tailMeta[..60]));

        file.Truncate(address.Ticket.Offset);
        file.Append(EventJournal.EventFrameTag, storedPayload, tailMeta).Unwrap();
        file.DurableFlush();
    }
}
