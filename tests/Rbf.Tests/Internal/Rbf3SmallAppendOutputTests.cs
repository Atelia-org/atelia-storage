using System.Buffers.Binary;
using Atelia.Data;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Public Append output boundaries and real partial-write recovery for the 8KiB small buffer.</summary>
public sealed class Rbf3SmallAppendOutputTests : IDisposable {
    private const int PartialTotalLength = 6144;
    private const int PartialFrameLength = PartialTotalLength - sizeof(uint);
    private const int CompleteBodyLength = PartialTotalLength - 2 * sizeof(uint);
    private const uint Tag = 83;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf3-small-output-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        File.Delete(_path);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(128, false)]
    [InlineData(6136, false)]
    [InlineData(6138, false)]
    [InlineData(6142, false)]
    [InlineData(2, true)]
    [InlineData(128, true)]
    [InlineData(6136, true)]
    [InlineData(6138, true)]
    [InlineData(6142, true)]
    public void SingleBufferPartialWrite_FaultsInstanceAndPublicReopenRecoversTheActualPrefix(int prefixLength, bool marker) {
        byte[] payload = CreatePayload(PartialTotalLength, marker);
        byte[] meta = [0x21, 0x43, 0x65];
        byte[] oldPayload = [9, 8, 7, 6];
        byte[] oldMeta = [5, 4];
        AssertLayout(PartialTotalLength, payload, meta);
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        var original = file.Append(41, oldPayload, oldMeta).Unwrap();
        var oldInfo = file.ReadFrameInfo(original).Unwrap();
        SafeFileHandle handle = oldInfo.Reader.File;
        long tail = file.TailOffset;
        int start = checked((int)tail);
        byte[] before = ReadBytes(handle);
        int requests = 0;
        var writes = new List<RbfWriteObservation>();
        byte[]? observedPrefix = null;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => {
                requests++;
                Assert.Equal(1, requests);
                Assert.Equal(Path.GetFullPath(_path), request.Path);
                Assert.Equal(tail, request.Offset);
                Assert.Equal(PartialTotalLength, request.RequestedBytes); // Reject the former multi-write Key0 path.
                return prefixLength;
            },
            AfterWrite = observation => {
                writes.Add(observation);
                Assert.Equal(PartialTotalLength, observation.RequestedBytes);
                Assert.Equal(prefixLength, observation.WrittenBytes);
                Assert.Equal(tail + prefixLength, RandomAccess.GetLength(handle));
                observedPrefix = ReadBytes(handle);
                Assert.Equal(before, observedPrefix[..start]);
            }
        };
        try {
            // The hook performs a real prefix write; instrumentation then throws IOException.
            Assert.Throws<IOException>(() => file.Append(Tag, payload, meta));
        }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(1, requests);
        Assert.Single(writes);
        Assert.NotNull(observedPrefix);
        byte[] partial = observedPrefix!;
        Assert.Equal(start + prefixLength, partial.Length);
        Assert.Equal(tail, file.TailOffset);
        byte[] head = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(head, PartialFrameLength / sizeof(uint));
        Assert.Equal(head[..Math.Min(prefixLength, head.Length)], partial.AsSpan(start, Math.Min(prefixLength, head.Length)).ToArray());

        uint? observedKey = null;
        if (prefixLength >= 2 * sizeof(uint)) {
            // Infer this Append's actual RNG-selected key from its first encoded payload word.
            observedKey = BinaryPrimitives.ReadUInt32LittleEndian(partial.AsSpan(start + sizeof(uint)))
                ^ BinaryPrimitives.ReadUInt32LittleEndian(payload);
            AssertKeyGroup(observedKey.Value, marker);
        }
        // A partial Head contains no key evidence. The marker input itself forbids production Key0.
        Assert.Throws<InvalidOperationException>(() => file.Append(99, new byte[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => file.BeginAppend());
        Assert.Throws<InvalidOperationException>(() => file.DurableFlush());
        Assert.Throws<InvalidOperationException>(() => file.ReadFrameInfo(original));
        Assert.Throws<InvalidOperationException>(() => oldInfo.ReadPooledFrame());
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(partial, ReadBytes(handle));
        file.Dispose();
        Assert.True(handle.IsClosed);
        Assert.Equal(partial, File.ReadAllBytes(_path));

        Assert.Throws<InvalidDataException>(() => {
            using var readOnly = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        });
        Assert.Equal(partial, File.ReadAllBytes(_path));

        bool completeBody = prefixLength >= CompleteBodyLength;
        long expectedTail = completeBody ? tail + PartialTotalLength : tail;
        var pendingTicket = SizedPtr.Create(tail, PartialFrameLength);
        using (var recovered = RbfFile.OpenExisting(_path, out var report, RbfCacheMode.Off)) {
            Assert.Equal(completeBody ? RbfTailRecoveryAction.CompletedTail : RbfTailRecoveryAction.Truncated, report.Action);
            Assert.Equal(tail + prefixLength, report.OriginalLength);
            Assert.Equal(expectedTail, report.FinalLength);
            Assert.Equal(expectedTail, recovered.TailOffset);
            Assert.Equal((long?)tail, report.AffectedFrameOffset);
            Assert.Equal(completeBody ? (SizedPtr?)pendingTicket : null, report.FrameTicket);
            AssertReadableFrame(recovered, original, oldPayload, oldMeta, 41);
            if (completeBody) { AssertReadableFrame(recovered, pendingTicket, payload, meta, Tag); }
        }

        byte[] final = File.ReadAllBytes(_path);
        if (completeBody) {
            Assert.Equal(start + PartialTotalLength, final.Length);
            Assert.Equal(partial, final[..partial.Length]); // Recovery only appends the missing original Key/Fence suffix.
            uint recoveredKey = Rbf3WriterOracle.AssertWire(final.AsSpan(start, PartialTotalLength).ToArray(), payload, meta, Tag);
            Assert.True(observedKey.HasValue);
            Assert.Equal(observedKey.Value, recoveredKey);
            AssertKeyGroup(recoveredKey, marker);
        }
        else { Assert.Equal(before, final); }
        using (var reopened = RbfFile.OpenExisting(_path, out var again, RbfCacheMode.Off)) {
            Assert.Equal(RbfTailRecoveryAction.None, again.Action);
            Assert.Equal(expectedTail, again.OriginalLength);
            Assert.Equal(expectedTail, again.FinalLength);
            Assert.Equal(expectedTail, reopened.TailOffset);
            Assert.Null(again.AffectedFrameOffset);
            Assert.Null(again.FrameTicket);
        }
        Assert.Equal(final, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData(8192, false)]
    [InlineData(8192, true)]
    [InlineData(8196, false)]
    [InlineData(8196, true)]
    public void SmallBufferThreshold_WritesExpectedRequestsAndIndependentlyValidWire(int totalLength, bool marker) {
        byte[] payload = CreatePayload(totalLength, marker);
        byte[] meta = [0x21, 0x43, 0x65];
        AssertLayout(totalLength, payload, meta);
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        var writes = new List<RbfWriteObservation>();
        RbfWriteInstrumentation.Current = new() { AfterWrite = writes.Add };
        SizedPtr ticket;
        try { ticket = file.Append(Tag, payload, meta).Unwrap(); }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(4, ticket.Offset);
        Assert.Equal(totalLength - sizeof(uint), ticket.Length);
        Assert.Equal(4L + totalLength, file.TailOffset);
        int[] expectedRequests = totalLength == 8192 || marker
            ? [totalLength]
            : [sizeof(uint), payload.Length, meta.Length, 28];
        Assert.Equal(expectedRequests, writes.Select(write => write.RequestedBytes).ToArray());
        long offset = 4;
        foreach (var write in writes) {
            Assert.Equal(Path.GetFullPath(_path), write.Path);
            Assert.Equal(offset, write.Offset);
            Assert.Equal(write.RequestedBytes, write.WrittenBytes);
            offset += write.WrittenBytes;
        }
        Assert.Equal(file.TailOffset, offset);
        var info = file.ReadFrameInfo(ticket).Unwrap();
        byte[] bytes = ReadBytes(info.Reader.File);
        Assert.Equal(4 + totalLength, bytes.Length);
        uint key = Rbf3WriterOracle.AssertWire(bytes.AsSpan(4).ToArray(), payload, meta, Tag);
        AssertKeyGroup(key, marker);
        AssertReadableFrame(file, ticket, payload, meta, Tag);
        file.Dispose();
        using var readOnly = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        AssertReadableFrame(readOnly, ticket, payload, meta, Tag);
    }

    private static byte[] CreatePayload(int totalLength, bool marker) {
        Assert.True(RbfFile.TryGetMaxPayloadLengthForAppendBudget(totalLength, 3, out int payloadLength));
        byte[] payload = new byte[payloadLength];
        if (marker) { BinaryPrimitives.WriteUInt32LittleEndian(payload, Rbf3WriterOracle.Fence); }
        return payload;
    }

    private static void AssertLayout(int totalLength, byte[] payload, byte[] meta) {
        Assert.Equal(3, meta.Length);
        Assert.Equal(1, payload.Length & 3); // Payload -> meta crosses XOR phase 1.
        Assert.Equal(totalLength, ((payload.Length + meta.Length + 3) & ~3) + 32);
    }

    private static void AssertKeyGroup(uint key, bool marker) {
        Assert.Equal(marker, key != 0);
        Assert.NotEqual(Rbf3WriterOracle.Fence, key);
    }

    private static byte[] ReadBytes(SafeFileHandle handle) {
        byte[] bytes = new byte[checked((int)RandomAccess.GetLength(handle))];
        Assert.Equal(bytes.Length, RandomAccess.Read(handle, bytes, 0));
        return bytes;
    }

    private static void AssertReadableFrame(IRbfFile file, SizedPtr ticket, byte[] payload, byte[] meta, uint tag) {
        using var frame = file.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal(ticket, frame.Ticket);
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(meta.Length, frame.TailMetaLength);
        Assert.Equal(payload, frame.PayloadAndMeta[..payload.Length].ToArray());
        Assert.Equal(meta, frame.PayloadAndMeta[payload.Length..].ToArray());
        Assert.False(frame.IsTombstone);
    }
}
