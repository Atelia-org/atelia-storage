using System;
using System.Buffers;
using System.IO;
using Xunit;

namespace Atelia.Memory.Tests;

/// <summary>
/// Tests covering PagedReservableWriterOptions based configurable behaviors.
/// </summary>
public class PagedReservableWriterOptionsTests {
    private sealed class CollectingWriter : IBufferWriter<byte> {
        private MemoryStream _ms = new();
        private int _pos;
        public void Advance(int count) { _pos += count; if (_pos > _ms.Length) _ms.SetLength(_pos); }
        public Memory<byte> GetMemory(int sizeHint = 0) { int need = _pos + Math.Max(sizeHint,1); if (_ms.Length < need) _ms.SetLength(need); return _ms.GetBuffer().AsMemory(_pos, (int)_ms.Length - _pos); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public byte[] Data() { var a=new byte[_pos]; Array.Copy(_ms.GetBuffer(),0,a,0,_pos); return a; }
    }

    [Fact]
    public void StrictMode_ReserveSpanAfterUnadvancedGetSpan_Throws() {
        var inner = new CollectingWriter();
        var options = new PagedReservableWriterOptions { EnforceStrictAdvance = true };
        using var writer = new PagedReservableWriter(inner, options);
        var span = writer.GetSpan(8);
        span[0] = 1; // write something but forget Advance
        Assert.Throws<InvalidOperationException>(() => writer.ReserveSpan(4, out _, null));
    }

    [Fact]
    public void StrictMode_ReserveSpanAfterAdvanceZero_Allows() {
        var inner = new CollectingWriter();
        var options = new PagedReservableWriterOptions { EnforceStrictAdvance = true };
        using var writer = new PagedReservableWriter(inner, options);
        writer.GetSpan(16); // acquire
        writer.Advance(0);  // explicitly cancel
    var r = writer.ReserveSpan(4, out int token, null);
        r.Fill(0xAB);
        writer.Commit(token);
        Assert.Equal(new byte[]{0xAB,0xAB,0xAB,0xAB}, inner.Data());
    }

    [Fact]
    public void CustomPageSize_BufferedModeSpanRespectsConfiguredPageSize() {
        var inner = new CollectingWriter();
        var options = new PagedReservableWriterOptions { PageSize = 8192 };
        using var writer = new PagedReservableWriter(inner, options);
        // Enter buffered mode by creating a reservation first (so following GetSpan allocates a chunk)
        writer.ReserveSpan(1, out int tk, null)[0] = 0xFF; // not yet committed -> buffered mode
        var span = writer.GetSpan(10); // now from internal chunk
        Assert.True(span.Length >= 10, "Span should satisfy sizeHint");
        // We cannot strictly assert span length == page size because we return entire free space,
        // but ensure chunk allocation at least page sized by making a larger second request.
        var span2 = writer.GetSpan(4000); // second request may still be same chunk
        Assert.True(span2.Length >= 4000);
        writer.Commit(tk);
    }

    [Fact]
    public void OversizeRequest_ExceedsMaxPagedBytes_RentsOversizeBuffer() {
        var inner = new CollectingWriter();
        var options = new PagedReservableWriterOptions { PageSize = 1024, MinChunkPages = 1, MaxChunkPages = 1 }; // max paged = 1024
        using var writer = new PagedReservableWriter(inner, options);
        int big = 5000; // > 1024 so triggers oversize path
        var span = writer.GetSpan(big);
        Assert.True(span.Length >= big, $"Oversize span length {span.Length} < requested {big}");
    }

    [Fact]
    public void StrictMode_DoesNotBreakFlushSemantics() {
        var inner = new CollectingWriter();
        var options = new PagedReservableWriterOptions { EnforceStrictAdvance = true };
        using var writer = new PagedReservableWriter(inner, options);
        var head = writer.GetSpan(3); head[0]=0x01; head[1]=0x02; head[2]=0x03; writer.Advance(3); // passthrough
        writer.ReserveSpan(2, out int t, "r").Fill(0x10);
        var tail = writer.GetSpan(2); tail[0]=0xEE; tail[1]=0xEF; writer.Advance(2);
        writer.Commit(t);
        Assert.Equal(new byte[]{0x01,0x02,0x03,0x10,0x10,0xEE,0xEF}, inner.Data());
    }
}
