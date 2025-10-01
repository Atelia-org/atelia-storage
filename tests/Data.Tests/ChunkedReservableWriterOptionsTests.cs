using System;
using System.Buffers;
using System.IO;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// Tests covering ChunkedReservableWriterOptions based configurable behaviors.
/// </summary>
public class ChunkedReservableWriterOptionsTests {
    private sealed class CollectingWriter : IBufferWriter<byte> {
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
        public byte[] Data() {
            var a = new byte[_pos];
            Array.Copy(_ms.GetBuffer(), 0, a, 0, _pos);
            return a;
        }
    }

    [Fact]
    public void ReserveSpanAfterUnadvancedGetSpan_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        var span = writer.GetSpan(8);
        span[0] = 1; // write something but forget Advance
        Assert.Throws<InvalidOperationException>(() => writer.ReserveSpan(4, out _, null));
    }

    [Fact]
    public void GetSpanTwiceWithoutAdvance_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        var first = writer.GetSpan(4);
        first[0] = 42;
        var ex = Assert.Throws<InvalidOperationException>(() => writer.GetSpan(2));
        Assert.Contains("Previous buffer not advanced", ex.Message);
    }

    [Fact]
    public void GetMemoryTwiceWithoutAdvance_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        var first = writer.GetMemory(4);
        first.Span[0] = 11;
        var ex = Assert.Throws<InvalidOperationException>(() => writer.GetMemory(1));
        Assert.Contains("Previous buffer not advanced", ex.Message);
    }

    [Fact]
    public void ReserveSpanAfterAdvanceZero_Allows() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        writer.GetSpan(16); // acquire
        writer.Advance(0); // explicitly cancel
        var r = writer.ReserveSpan(4, out int token, null);
        r.Fill(0xAB);
        writer.Commit(token);
        Assert.Equal(new byte[] { 0xAB, 0xAB, 0xAB, 0xAB }, inner.Data());
    }

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

    [Fact]
    public void StrictContract_DoesNotBreakFlushSemantics() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        var head = writer.GetSpan(3);
        head[0] = 0x01;
        head[1] = 0x02;
        head[2] = 0x03;
        writer.Advance(3); // passthrough
        writer.ReserveSpan(2, out int t, "r").Fill(0x10);
        var tail = writer.GetSpan(2);
        tail[0] = 0xEE;
        tail[1] = 0xEF;
        writer.Advance(2);
        writer.Commit(t);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x10, 0x10, 0xEE, 0xEF }, inner.Data());
    }
}
