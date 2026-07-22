using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfRecoveryScannerTests : IDisposable {
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
    public void ScanBackward_TailGarbage_FindsLastValidFrame() {
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02];
        byte[] payload2 = [0xAA, 0xBB, 0xCC];
        long validTail;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, payload1).IsSuccess);
            Assert.True(rbf.Append(0x22222222, payload2).IsSuccess);
            validTail = rbf.TailOffset;
        }

        File.AppendAllBytes(path, [0x10, 0x20, 0x30]);

        using var scanner = RbfRecovery.OpenReadOnly(path);
        var enumerator = scanner.ScanBackward().GetEnumerator();

        Assert.True(enumerator.MoveNext());
        var hit = enumerator.Current;
        Assert.Equal(0x22222222u, hit.Info.Tag);
        Assert.Equal(RbfRecoveryConfidence.FrameBoundary, hit.Confidence);
        Assert.Equal(validTail, hit.SuggestedTruncateOffset);

        var frameResult = hit.Info.ReadPooledFrame();
        Assert.True(frameResult.IsSuccess);
        using var frame = frameResult.Value!;
        Assert.Equal(payload2, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void ScanBackward_CorruptedLatestTrailer_FindsPreviousFrame() {
        var path = GetTempFilePath();
        SizedPtr ptr3;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, [0x01]).IsSuccess);
            Assert.True(rbf.Append(0x22222222, [0x02, 0x03]).IsSuccess);
            var result3 = rbf.Append(0x33333333, [0x04, 0x05, 0x06]);
            Assert.True(result3.IsSuccess);
            ptr3 = result3.Value!;
        }

        CorruptTrailerCrc(path, ptr3);

        using var scanner = RbfRecovery.OpenReadOnly(path);
        var enumerator = scanner.ScanBackward().GetEnumerator();

        Assert.True(enumerator.MoveNext());
        var hit = enumerator.Current;
        Assert.Equal(0x22222222u, hit.Info.Tag);
        Assert.Equal(RbfRecoveryConfidence.FrameBoundary, hit.Confidence);
        Assert.True(hit.SuggestedTruncateOffset < new FileInfo(path).Length);
    }

    [Fact]
    public void ScanBackward_FullFrameValidation_SkipsPayloadCorruptedFrame() {
        var path = GetTempFilePath();
        SizedPtr ptr2;

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, [0x01, 0x02, 0x03, 0x04]).IsSuccess);
            var result2 = rbf.Append(0x22222222, [0xAA, 0xBB, 0xCC, 0xDD]);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;
        }

        CorruptPayload(path, ptr2);

        using var scanner = RbfRecovery.OpenReadOnly(path);
        var options = new RbfRecoveryScanOptions { ValidationLevel = RbfRecoveryValidationLevel.FullFrame };
        var enumerator = scanner.ScanBackward(options).GetEnumerator();

        Assert.True(enumerator.MoveNext());
        var hit = enumerator.Current;
        Assert.Equal(0x11111111u, hit.Info.Tag);
        Assert.Equal(RbfRecoveryConfidence.FullFrame, hit.Confidence);
    }

    [Fact]
    public void TruncateToSuggestedTail_RemovesUnalignedTailGarbage() {
        var path = GetTempFilePath();

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, [0x01]).IsSuccess);
            Assert.True(rbf.Append(0x22222222, [0x02]).IsSuccess);
        }

        File.AppendAllBytes(path, [0x66, 0x77, 0x88]);

        RbfRecoveryHit hit;
        using (var scanner = RbfRecovery.OpenReadOnly(path)) {
            var enumerator = scanner.ScanBackward().GetEnumerator();
            Assert.True(enumerator.MoveNext());
            hit = enumerator.Current;
        }

        RbfRecovery.TruncateToSuggestedTail(path, hit);

        using var reopened = RbfFile.OpenExisting(path);
        var tags = new List<uint>();
        var scan = reopened.ScanReverse().GetEnumerator();
        while (scan.MoveNext()) {
            tags.Add(scan.Current.Tag);
        }

        Assert.Null(scan.TerminationError);
        Assert.Equal([0x22222222u, 0x11111111u], tags);
    }

    [Fact]
    public void TruncateToSuggestedTail_RejectsMissingSuggestedTailFence() {
        var path = GetTempFilePath();

        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.True(rbf.Append(0x11111111, [0x01]).IsSuccess);
            Assert.True(rbf.Append(0x22222222, [0x02]).IsSuccess);
        }

        File.AppendAllBytes(path, [0x66, 0x77, 0x88]);

        RbfRecoveryHit hit;
        using (var scanner = RbfRecovery.OpenReadOnly(path)) {
            var enumerator = scanner.ScanBackward().GetEnumerator();
            Assert.True(enumerator.MoveNext());
            hit = enumerator.Current;
        }

        long lengthBefore = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.Position = hit.SuggestedTruncateOffset - RbfLayout.FenceSize;
            stream.WriteByte(0x00);
        }

        var exception = Assert.Throws<InvalidDataException>(() => RbfRecovery.TruncateToSuggestedTail(path, hit));
        Assert.Contains("TailFence", exception.Message);
        Assert.Equal(lengthBefore, new FileInfo(path).Length);
    }

    private static void CorruptTrailerCrc(string path, SizedPtr ticket) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + ticket.Length - 16;
        int original = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0xFF));
    }

    private static void CorruptPayload(string path, SizedPtr ticket) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = ticket.Offset + 4;
        int original = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0xFF));
    }
}
