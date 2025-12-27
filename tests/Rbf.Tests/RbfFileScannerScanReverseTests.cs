using System.Collections.Generic;
using System.Linq;
using Atelia.Rbf.Tests.TestHelpers;
using FluentAssertions;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// T-M1-12b: RbfFileScanner.ScanReverse() file-backed 实现的验收测试。
/// </summary>
public class RbfFileScannerScanReverseTests {
    /// <summary>
    /// 验证 file-backed ScanReverse 能正确逆序返回所有帧。
    /// </summary>
    [Fact]
    public void ScanReverse_FileBacked_ReturnsFramesInReverseOrder() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("scan-reverse.rbf");

        var writtenTags = new List<uint>();

        // 写入 5 帧
        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            for (uint i = 1; i <= 5; i++) {
                var tag = new FrameTag(i * 10); // 10, 20, 30, 40, 50
                var payload = new byte[] { (byte)i, (byte)(i + 1), (byte)(i + 2) };

                framer.Append(tag, payload);
                writtenTags.Add(tag.Value);
            }

            framer.Flush();
            framer.Backend.DurableFlush();
        }

        // 使用 file-backed scanner 逆向扫描
        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();

        // 应有 5 帧
        frames.Should().HaveCount(5, "all 5 frames should be recovered");

        // 帧应按逆序返回（最后写入的先返回）
        var scannedTags = frames.Select(f => f.FrameTag).ToList();
        scannedTags.Should().Equal(writtenTags.AsEnumerable().Reverse(), "frames should be in reverse order (50, 40, 30, 20, 10)");
    }

    /// <summary>
    /// 验证大 payload (256KB) 时 CRC 分块计算路径正确。
    /// </summary>
    [Fact]
    public void ScanReverse_LargePayload_CrcChunkedVerification() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("large-payload.rbf");

        const int largePayloadSize = 256 * 1024; // 256KB
        byte[] largePayload = new byte[largePayloadSize];

        // 填充有规律的数据以便验证
        for (int i = 0; i < largePayloadSize; i++) {
            largePayload[i] = (byte)(i % 256);
        }

        // 写入：小帧 + 大帧 + 小帧
        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            framer.Append(new FrameTag(1), [0x11, 0x22]);
            framer.Append(new FrameTag(2), largePayload);
            framer.Append(new FrameTag(3), [0x33, 0x44]);
            framer.Flush();
            framer.Backend.DurableFlush();
        }

        // 使用 file-backed scanner 逆向扫描
        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();

        // 应有 3 帧，逆序
        frames.Should().HaveCount(3);
        frames[0].FrameTag.Should().Be(3u);
        frames[1].FrameTag.Should().Be(2u);
        frames[2].FrameTag.Should().Be(1u);

        // 验证大帧的 payload
        var largeFrame = frames[1];
        largeFrame.PayloadLength.Should().Be(largePayloadSize);

        var recoveredPayload = scanner.ReadPayload(largeFrame);
        recoveredPayload.Should().Equal(largePayload, "large payload should be fully recoverable");
    }

    /// <summary>
    /// 验证空文件返回空集合。
    /// </summary>
    [Fact]
    public void ScanReverse_EmptyFile_ReturnsEmpty() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("nonexistent.rbf");

        // 不创建文件，RbfFileScanner 应处理不存在的文件
        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();

        frames.Should().BeEmpty();
    }

    /// <summary>
    /// 验证只有 Genesis Fence 的文件返回空集合。
    /// </summary>
    [Fact]
    public void ScanReverse_OnlyGenesisFence_ReturnsEmpty() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("genesis-only.rbf");

        // 创建一个只有 Genesis Fence 的文件（写入后立即关闭，不写入任何帧）
        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            // 不写入任何帧，只有 Genesis Fence
            framer.Flush();
            framer.Backend.DurableFlush();
        }

        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();

        frames.Should().BeEmpty("file with only Genesis Fence should have no frames");
    }

    /// <summary>
    /// 验证单帧文件能正确扫描。
    /// </summary>
    [Fact]
    public void ScanReverse_SingleFrame_Works() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("single-frame.rbf");

        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD];

        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            framer.Append(new FrameTag(42), payload);
            framer.Flush();
            framer.Backend.DurableFlush();
        }

        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(42u);
        frames[0].PayloadLength.Should().Be(4);

        var recoveredPayload = scanner.ReadPayload(frames[0]);
        recoveredPayload.Should().Equal(payload);
    }

    /// <summary>
    /// 验证零长度 payload 帧能正确扫描。
    /// </summary>
    [Fact]
    public void ScanReverse_ZeroLengthPayload_Works() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("zero-payload.rbf");

        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            framer.Append(new FrameTag(1), [0x11]);
            framer.Append(new FrameTag(2), []); // 零长度 payload
            framer.Append(new FrameTag(3), [0x33]);
            framer.Flush();
            framer.Backend.DurableFlush();
        }

        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(3);

        var zeroFrame = frames.Single(f => f.FrameTag == 2);
        zeroFrame.PayloadLength.Should().Be(0);

        var recoveredPayload = scanner.ReadPayload(zeroFrame);
        recoveredPayload.Should().BeEmpty();
    }

    /// <summary>
    /// 验证 file-backed ScanReverse 与 RbfScanner 结果一致。
    /// </summary>
    [Fact]
    public void ScanReverse_FileBacked_MatchesMemoryBased() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("consistency.rbf");

        // 写入多个不同大小的帧
        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            framer.Append(new FrameTag(100), new byte[0]);     // 零长度
            framer.Append(new FrameTag(200), new byte[1]);     // 1 字节
            framer.Append(new FrameTag(300), new byte[3]);     // 3 字节
            framer.Append(new FrameTag(400), new byte[4]);     // 4 字节（边界）
            framer.Append(new FrameTag(500), new byte[100]);   // 100 字节
            framer.Append(new FrameTag(600), new byte[1000]);  // 1KB
            framer.Flush();
            framer.Backend.DurableFlush();
        }

        // 读取整个文件用于 memory-based scanner
        var fileData = System.IO.File.ReadAllBytes(path);
        var memoryScanner = new RbfScanner(fileData);
        var memoryFrames = memoryScanner.ScanReverse().ToList();

        // File-backed scanner
        using var fileScanner = new RbfFileScanner(path);
        var fileFrames = fileScanner.ScanReverse().ToList();

        // 数量应一致
        fileFrames.Should().HaveCount(memoryFrames.Count);

        // 每个帧的元数据应一致
        for (int i = 0; i < fileFrames.Count; i++) {
            var ff = fileFrames[i];
            var mf = memoryFrames[i];

            ff.FileOffset.Should().Be(mf.FileOffset, $"frame {i} FileOffset should match");
            ff.FrameTag.Should().Be(mf.FrameTag, $"frame {i} FrameTag should match");
            ff.PayloadOffset.Should().Be(mf.PayloadOffset, $"frame {i} PayloadOffset should match");
            ff.PayloadLength.Should().Be(mf.PayloadLength, $"frame {i} PayloadLength should match");
            ff.Status.Should().Be(mf.Status, $"frame {i} Status should match");
        }
    }
}
