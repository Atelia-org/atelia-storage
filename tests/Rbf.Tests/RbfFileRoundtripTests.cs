using System.Collections.Generic;
using System.Linq;
using Atelia.Rbf.Tests.TestHelpers;
using FluentAssertions;
using Xunit;

namespace Atelia.Rbf.Tests;

public class RbfFileRoundtripTests {
    [Fact]
    public void WriteClose_OpenScan_AllFramesRecovered() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("roundtrip.rbf");

        var addresses = new List<Address64>();
        var payloads = new Dictionary<uint, byte[]>();

        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            for (uint i = 1; i <= 5; i++) {
                var tag = new FrameTag(i);
                var payload = new byte[] { (byte)i, (byte)(i + 1), (byte)(i + 2) };

                payloads[tag.Value] = payload;
                addresses.Add(framer.Append(tag, payload));
            }

            framer.Flush();
            framer.Backend.DurableFlush();
        }

        using var scanner = new RbfFileScanner(path);

        var frames = scanner.ScanReverse().ToList();
        frames.Should().HaveCount(5);

        foreach (var address in addresses) {
            scanner.TryReadAt(address, out var frame).Should().BeTrue();
            var payload = scanner.ReadPayload(frame);
            payload.Should().Equal(payloads[frame.FrameTag]);
        }
    }

    [Fact]
    public void TruncateTo_OnlyFramesBeforeBoundaryRemainVisible() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("truncate.rbf");

        long lengthAfterSecondCommit;

        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            framer.Append(new FrameTag(1), new byte[] { 1 });
            framer.Append(new FrameTag(2), new byte[] { 2, 3 });
            framer.Flush();
            lengthAfterSecondCommit = framer.Position;

            framer.Append(new FrameTag(3), new byte[] { 4, 5, 6, 7 });
            framer.Flush();

            framer.Backend.TruncateTo(lengthAfterSecondCommit);
            framer.Backend.DurableFlush();
        }

        using var scanner = new RbfFileScanner(path);
        scanner.ScanReverse().Should().HaveCount(2);
    }

    /// <summary>
    /// 验证 file-backed TryReadAt 可正确读取帧（T-M1-12a 验收测试）。
    /// </summary>
    [Fact]
    public void TryReadAt_FileBacked_ReadsFrameCorrectly() {
        using var temp = new TempFileFixture();
        var path = temp.GetFilePath("file-backed-read.rbf");

        Address64 savedAddress;
        byte[] expectedPayload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE];
        uint expectedTag = 0x12345678;

        // 写入一个帧
        using (var framer = new RbfFileFramer(path, RbfFileMode.Create)) {
            savedAddress = framer.Append(new FrameTag(expectedTag), expectedPayload);
            framer.Flush();
            framer.Backend.DurableFlush();
        }

        // 使用 file-backed scanner 读取
        using var scanner = new RbfFileScanner(path);

        // TryReadAt 应成功
        scanner.TryReadAt(savedAddress, out var frame).Should().BeTrue("frame should be readable at saved address");

        // 验证帧元数据
        frame.FrameTag.Should().Be(expectedTag);
        frame.PayloadLength.Should().Be(expectedPayload.Length);
        frame.Status.IsValid.Should().BeTrue();

        // ReadPayload 应返回正确的内容
        var payload = scanner.ReadPayload(frame);
        payload.Should().Equal(expectedPayload);
    }
}
