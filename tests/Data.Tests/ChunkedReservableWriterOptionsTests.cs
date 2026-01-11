using System;
using System.Buffers;
using System.IO;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// Tests covering ChunkedReservableWriterOptions based configurable behaviors.
/// </summary>
public class ChunkedReservableWriterOptionsTests {
    /// <summary>
    /// 轻量 inner writer：追加写入并可读取已写数据
    /// 同时实现 IBufferWriter&lt;byte&gt;（供 ChunkedReservableWriter）和 IByteSink（供 SinkReservableWriter）
    /// </summary>
    private sealed class CollectingWriter : IBufferWriter<byte>, IByteSink {
        private MemoryStream _ms = new();
        private int _pos;
        public void Advance(int count) {
            _pos += count;
            if (_pos > _ms.Length) {
                _ms.SetLength(_pos);
            }
        }
        public Memory<byte> GetMemory(int sizeHint = 0) {
            int need = _pos + Math.Max(sizeHint, 1);
            if (_ms.Length < need) {
                _ms.SetLength(need);
            }
            return _ms.GetBuffer().AsMemory(_pos, (int)_ms.Length - _pos);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        // ========== IByteSink ==========
        public void Push(ReadOnlySpan<byte> data) {
            int need = _pos + data.Length;
            if (_ms.Length < need) {
                _ms.SetLength(need);
            }
            data.CopyTo(_ms.GetBuffer().AsSpan(_pos, data.Length));
            _pos += data.Length;
        }

        public byte[] Data() {
            var a = new byte[_pos];
            Array.Copy(_ms.GetBuffer(), 0, a, 0, _pos);
            return a;
        }
    }

    /// <summary>
    /// 用于 passthrough 相关测试（带 shouldTestPassthrough 标志）
    /// - ChunkedReservableWriter: shouldTestPassthrough=true，验证 passthrough 优化
    /// - SinkReservableWriter: shouldTestPassthrough=false，总是 buffered，跳过 passthrough 断言
    /// </summary>
    public static TheoryData<string, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)>, bool> WriterFactoriesWithPassthrough => new() {
        {
            "ChunkedReservableWriter",
            () => {
                var collector = new CollectingWriter();
                return (new ChunkedReservableWriter(collector), collector.Data);
            },
            true  // shouldTestPassthrough: 验证 passthrough 优化
        },
        {
            "SinkReservableWriter",
            () => {
                var collector = new CollectingWriter();
                return (new SinkReservableWriter(collector), collector.Data);
            },
            false  // shouldTestPassthrough: 总是 buffered，跳过 passthrough 断言
        },
    };

    [Fact]
    public void ReserveSpanAfterUnadvancedGetSpan_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        var span = writer.GetSpan(8);
        span[0] = 1; // write something but forget Advance
        Assert.Throws<InvalidOperationException>(() => writer.ReserveSpan(4, out _, null));
    }

    // ReserveSpanAfterAdvanceZero_Allows 已移至 ReservableWriterNegativeTests
    // GetSpanTwiceWithoutAdvance_Throws 已移至 ReservableWriterNegativeTests
    // GetMemoryTwiceWithoutAdvance_Throws 已移至 ReservableWriterNegativeTests

    [Fact]
    public void CustomMinMaxChunkSize_BufferedModeSpanRespectsConfiguredMin() {
        var inner = new CollectingWriter();
        var options = new ChunkedReservableWriterOptions { MinChunkSize = 8192, MaxChunkSize = 8192 };
        using var writer = new ChunkedReservableWriter(inner, options);
        writer.ReserveSpan(1, out int tk, null)[0] = 0xFF; // force buffered mode
        var span = writer.GetSpan(16);
        Assert.True(span.Length >= 16);
        writer.Advance(0); // release without writing
        // second span request should still come from same chunk unless exhausted
        var span2 = writer.GetSpan(4000);
        Assert.True(span2.Length >= 4000);
        writer.Commit(tk);
    }

    [Fact]
    public void OversizeRequest_ExceedsMaxChunkSize_RentsOversizeBuffer() {
        var inner = new CollectingWriter();
        var options = new ChunkedReservableWriterOptions { MinChunkSize = 4096, MaxChunkSize = 8192 };
        using var writer = new ChunkedReservableWriter(inner, options);
        int big = 50_000; // >> max chunk size triggers direct rent
        var span = writer.GetSpan(big);
        Assert.True(span.Length >= big, $"Oversize span length {span.Length} < requested {big}");
    }

    [Fact]
    public void AdaptiveGrowth_IncreasesChunkTarget() {
        var inner = new CollectingWriter();
        // Allow growth: min 4KB, max 32KB
        var options = new ChunkedReservableWriterOptions { MinChunkSize = 4096, MaxChunkSize = 32 * 1024 };
        using var writer = new ChunkedReservableWriter(inner, options);
        // Force buffered mode and allocate sequentially increasing sizeHints to encourage growth
        writer.ReserveSpan(10, out int t0, null); // allocate first chunk
        // Request several spans so internal heuristic grows target size. We cannot read internal target; infer indirectly.
        var s1 = writer.GetSpan(2000);
        writer.Advance(2000);
        var s2 = writer.GetSpan(6000);
        writer.Advance(6000);
        var s3 = writer.GetSpan(12000);
        writer.Advance(12000);
        // By now a larger chunk should have been allocated at least once (>= 8192)
        Assert.True(Math.Max(s1.Length, Math.Max(s2.Length, s3.Length)) >= 8192, "Expected growth to allocate >= 8KB chunk");
        writer.Commit(t0);
    }

    [Theory]
    [MemberData(nameof(WriterFactoriesWithPassthrough))]
    public void StrictContract_DoesNotBreakFlushSemantics(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory,
        bool shouldTestPassthrough) {
        _ = name;
        var (writer, getData) = factory();
        using var disposable = writer as IDisposable;

        var head = writer.GetSpan(3);
        head[0] = 0x01;
        head[1] = 0x02;
        head[2] = 0x03;
        writer.Advance(3);

        // 验证无 reservation 时 passthrough（仅 ChunkedReservableWriter）
        if (shouldTestPassthrough) {
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, getData());
        }

        writer.ReserveSpan(2, out int t, "r").Fill(0x10);
        var tail = writer.GetSpan(2);
        tail[0] = 0xEE;
        tail[1] = 0xEF;
        writer.Advance(2);
        writer.Commit(t);

        // 最终数据验证（所有实现）
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x10, 0x10, 0xEE, 0xEF }, getData());
    }
}
