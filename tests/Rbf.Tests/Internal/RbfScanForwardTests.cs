using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfScanForwardTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } }
            catch { }
        }
    }

    [Fact]
    public void ScanForward_MultipleFrames_ReturnsInWriteOrder() {
        var path = GetTempFilePath();
        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, [0x01, 0x02]).IsSuccess);
            Assert.True(rbf.Append(0x22222222, [0xAA, 0xBB, 0xCC]).IsSuccess);
            Assert.True(rbf.Append(0x33333333, [0x10, 0x20, 0x30, 0x40]).IsSuccess);
        }

        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, int payloadLen)>();

        foreach (var info in rbfRead.ScanForward()) {
            frames.Add((info.Tag, info.PayloadLength));
        }

        Assert.Equal(
            [
            (0x11111111u, 2),
            (0x22222222u, 3),
            (0x33333333u, 4)
        ], frames
        );
    }

    [Fact]
    public void ScanForward_EmptyFile_ReturnsEmptySequence() {
        var path = GetTempFilePath();
        using (RbfFile.CreateNew(path)) { }

        using var rbfRead = RbfFile.OpenExisting(path);
        var enumerator = rbfRead.ScanForward().GetEnumerator();

        Assert.False(enumerator.MoveNext());
        Assert.Null(enumerator.TerminationError);
    }

    [Fact]
    public void ScanForward_TicketUsableForReadFrame() {
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0xAABBCCDD, payload).IsSuccess);
        }

        using var rbfRead = RbfFile.OpenExisting(path);
        var enumerator = rbfRead.ScanForward().GetEnumerator();

        Assert.True(enumerator.MoveNext());
        var result = enumerator.Current.ReadPooledFrame();
        Assert.True(result.IsSuccess);
        using var frame = result.Value!;
        Assert.Equal(0xAABBCCDDu, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void ReadFrameInfoImmediatelyAfter_ReturnsDirectPhysicalSuccessorAndEof() {
        var path = GetTempFilePath();
        SizedPtr first;
        SizedPtr second;

        using (var rbf = RbfFile.CreateNew(path)) {
            first = rbf.Append(0x11111111, [0x01]).Unwrap();
            second = rbf.Append(0x22222222, [0x02]).Unwrap();
        }

        using var rbfRead = RbfFile.OpenExisting(path);
        OptionalRbfFrameInfo afterFirst = rbfRead.ReadFrameInfoImmediatelyAfter(first).Unwrap();
        OptionalRbfFrameInfo afterSecond = rbfRead.ReadFrameInfoImmediatelyAfter(second).Unwrap();

        Assert.True(afterFirst.HasValue);
        Assert.Equal(second, afterFirst.Value.Ticket);
        Assert.Equal(0x22222222u, afterFirst.Value.Tag);
        Assert.False(afterSecond.HasValue);
    }

    [Fact]
    public void ScanForward_ShowTombstoneFalse_SkipsTombstones() {
        var path = CreateTestFileWithMixedFrames(
            (0x11111111, [0x01], false),
            (0x22222222, [0x02], true),
            (0x33333333, [0x03], false)
        );

        using var rbfRead = RbfFile.OpenExisting(path);
        var tags = new List<uint>();

        foreach (var info in rbfRead.ScanForward(showTombstone: false)) {
            tags.Add(info.Tag);
        }

        Assert.Equal([0x11111111u, 0x33333333u], tags);
    }

    [Fact]
    public void ScanForward_ShowTombstoneTrue_IncludesTombstones() {
        var path = CreateTestFileWithMixedFrames(
            (0x11111111, [0x01], false),
            (0x22222222, [0x02], true),
            (0x33333333, [0x03], false)
        );

        using var rbfRead = RbfFile.OpenExisting(path);
        var frames = new List<(uint tag, bool isTombstone)>();

        foreach (var info in rbfRead.ScanForward(showTombstone: true)) {
            frames.Add((info.Tag, info.IsTombstone));
        }

        Assert.Equal(
            [
            (0x11111111u, false),
            (0x22222222u, true),
            (0x33333333u, false)
        ], frames
        );
    }

    [Fact]
    public void ScanForward_CorruptedPayload_StillSucceeds() {
        var path = GetTempFilePath();
        SizedPtr ptr2;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(1, [0x01, 0x02, 0x03, 0x04]).IsSuccess);
            var result2 = rbf.Append(2, [0x11, 0x12, 0x13, 0x14]);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;
            Assert.True(rbf.Append(3, [0x21, 0x22, 0x23, 0x24]).IsSuccess);
        }

        CorruptPayload(path, ptr2);

        using var rbfRead = RbfFile.OpenExisting(path);
        var tags = new List<uint>();
        var enumerator = rbfRead.ScanForward().GetEnumerator();
        while (enumerator.MoveNext()) {
            tags.Add(enumerator.Current.Tag);
        }

        Assert.Equal([1u, 2u, 3u], tags);
        Assert.Null(enumerator.TerminationError);
    }

    [Fact]
    public void ScanForward_CorruptedTrailer_HardStopsWithError() {
        var path = GetTempFilePath();
        SizedPtr ptr2;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(1, [0x01]).IsSuccess);
            var result2 = rbf.Append(2, [0x02]);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;
            Assert.True(rbf.Append(3, [0x03]).IsSuccess);
        }

        CorruptTrailerCrc(path, ptr2);

        using var rbfRead = RbfFile.OpenExisting(path);
        var tags = new List<uint>();
        var enumerator = rbfRead.ScanForward().GetEnumerator();
        while (enumerator.MoveNext()) {
            tags.Add(enumerator.Current.Tag);
        }

        Assert.Equal([1u], tags);
        Assert.NotNull(enumerator.TerminationError);
        Assert.IsType<RbfCrcMismatchError>(enumerator.TerminationError);
    }

    [Fact]
    public void ScanForward_CorruptedFence_HardStopsWithError() {
        var path = GetTempFilePath();
        SizedPtr ptr1;

        using (var rbf = RbfFile.CreateNew(path)) {
            var result1 = rbf.Append(1, [0x01]);
            Assert.True(result1.IsSuccess);
            ptr1 = result1.Value!;
            Assert.True(rbf.Append(2, [0x02]).IsSuccess);
        }

        CorruptTailFence(path, ptr1);

        using var rbfRead = RbfFile.OpenExisting(path);
        var enumerator = rbfRead.ScanForward().GetEnumerator();

        Assert.False(enumerator.MoveNext());
        Assert.NotNull(enumerator.TerminationError);
        Assert.IsType<RbfFramingError>(enumerator.TerminationError);
    }

    private string CreateTestFileWithMixedFrames(params (uint tag, byte[] payload, bool isTombstone)[] frames) {
        var path = GetTempFilePath();

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(RbfLayout.Fence);

        foreach (var (tag, payload, isTombstone) in frames) {
            byte[] frameBytes = CreateFrameBytes(tag, payload, isTombstone);
            stream.Write(frameBytes);
            stream.Write(RbfLayout.Fence);
        }

        return path;
    }

    private static byte[] CreateFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone) {
        var layout = new FrameLayout(payload.Length);
        byte[] frame = new byte[layout.FrameLength];
        Span<byte> span = frame;

        BinaryPrimitives.WriteUInt32LittleEndian(span[..FrameLayout.HeadLenSize], (uint)layout.FrameLength);
        payload.CopyTo(span.Slice(FrameLayout.PayloadOffset, payload.Length));
        if (layout.PaddingLength > 0) { span.Slice(layout.PaddingOffset, layout.PaddingLength).Clear(); }

        var payloadCrcCoverage = span.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint payloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize), payloadCrc);
        layout.FillTrailer(span.Slice(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, isTombstone);

        return frame;
    }

    private static void CorruptTrailerCrc(string path, SizedPtr ticket) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + ticket.Length - TrailerCodewordHelper.Size;
        int original = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0xFF));
    }

    private static void CorruptPayload(string path, SizedPtr ticket) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + FrameLayout.PayloadOffset;
        int original = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0xFF));
    }

    private static void CorruptTailFence(string path, SizedPtr ticket) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + ticket.Length;
        int original = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0xFF));
    }
}
