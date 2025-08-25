using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Atelia.Memory.Tests;

/// <summary>
/// P2 批次：轻量随机不变量 + 边界与多 chunk 行为测试（保持执行时间短）。
/// </summary>
public class PagedReservableWriterP2Tests {
    private sealed class CollectingWriter : IBufferWriter<byte> {
        private MemoryStream _ms = new();
        private int _pos;
        public void Advance(int count) { _pos += count; if (_pos > _ms.Length) _ms.SetLength(_pos); }
        public Memory<byte> GetMemory(int sizeHint = 0) { int need = _pos + Math.Max(sizeHint,1); if (_ms.Length < need) _ms.SetLength(need); return _ms.GetBuffer().AsMemory(_pos, (int)_ms.Length - _pos); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public byte[] Data() { var a=new byte[_pos]; Array.Copy(_ms.GetBuffer(),0,a,0,_pos); return a; }
        public int Length => _pos;
    }

    private static void AssertInvariants(PagedReservableWriter w) {
        Assert.True(w.FlushedLength <= w.WrittenLength, "Flushed <= Written 违反");
        Assert.Equal(w.WrittenLength - w.FlushedLength, w.PendingLength);
        Assert.True(w.PendingLength >= 0, "PendingLength < 0");
        if (w.BlockingReservationToken.HasValue) Assert.True(w.PendingReservationCount > 0, "有阻塞 token 但计数为0");
        if (w.IsPassthrough) {
            Assert.Equal(0, w.PendingReservationCount);
            Assert.Equal(0, w.PendingLength);
        }
    }

    [Fact]
    public void MiniRandomSequenceInvariantTest() {
        var inner = new CollectingWriter();
        using var writer = new PagedReservableWriter(inner);
        var rnd = new Random(12345);
        var activeTokens = new List<int>();

        int steps = 2000; // 轻量级
        for (int i = 0; i < steps; i++) {
            int op = rnd.Next(100);
            switch (op) {
                case < 50: { // Write 普通写入（可能在直通或缓冲模式）
                    int size = rnd.Next(1, 64);
                    var span = writer.GetSpan(size);
                    int adv = rnd.Next(1, Math.Min(size, span.Length));
                    // 填充一段可辨识数据（不必全填）
                    span.Slice(0, adv).Fill((byte)(adv % 251));
                    writer.Advance(adv);
                    break; }
                case < 75: { // Reserve
                    int len = rnd.Next(1, 32);
                    var span = writer.ReserveSpan(len, out int token, tag: null);
                    // 填充部分（模拟用户覆盖）
                    int fill = rnd.Next(0, len + 1);
                    if (fill > 0) span.Slice(0, fill).Fill((byte)(fill % 97));
                    activeTokens.Add(token);
                    break; }
                case < 90: { // Commit 随机一个 token
                    if (activeTokens.Count > 0) {
                        int idx = rnd.Next(activeTokens.Count);
                        int tk = activeTokens[idx];
                        activeTokens.RemoveAt(idx);
                        writer.Commit(tk);
                    }
                    break; }
                case < 95: { // Reset
                    writer.Reset();
                    activeTokens.Clear();
                    break; }
                default: { // No-op / Advance(0) 验证
                    writer.Advance(0);
                    break; }
            }
            AssertInvariants(writer);
        }

        // 最终确保全部可提交
        foreach (var tk in activeTokens) writer.Commit(tk);
        AssertInvariants(writer);
    }

    [Fact]
    public void LargeReservationBlocksThenFlushesAllAtCommit() {
        var inner = new CollectingWriter();
        using var writer = new PagedReservableWriter(inner);

        // 大 reservation 放在最前面，阻塞后续所有写入 flush
        int large = 800_000; // < 1MB 上限
        var res = writer.ReserveSpan(large, out int token, "big");
        // 只填充前 16 字节作为标记
    res.Slice(0, Math.Min(16, large)).Fill(0xAB);

        // 写入很多小块（这些应被阻塞）
        int smallWrites = 200;
        int smallSize = 32;
        for (int i = 0; i < smallWrites; i++) {
            var s = writer.GetSpan(smallSize);
            s[0] = (byte)i; // 标记首字节
            writer.Advance(1); // 只前进 1 字节，制造碎片
        }

        Assert.Equal(0, inner.Length); // 全被阻塞
        Assert.True(writer.PendingLength > 0);

        writer.Commit(token);
        // 一次性 flush（不验证全部内容，只验证长度 & 状态）
        Assert.Equal(writer.WrittenLength, writer.FlushedLength);
        Assert.Equal(inner.Length, writer.FlushedLength);
        Assert.True(writer.IsPassthrough);
    }

    [Fact]
    public void MultipleChunksPartialFlushThenRecycleToPassthrough() {
        var inner = new CollectingWriter();
        using var writer = new PagedReservableWriter(inner);

        // 在第一个 chunk 前部放一个小 reservation 阻塞 flush
        var r = writer.ReserveSpan(8, out int t, "head-block");
        r.Fill(0x11);

        // 写入 > PageSize 的数据保证产生多个 chunk
        int total = PagedReservableWriter.PageSize * 3 + 500; // 3.5 pages
        int written = 0;
        while (written < total) {
            int size = Math.Min(4096, total - written);
            var span = writer.GetSpan(size);
            int adv = Math.Min(size, span.Length);
            span.Slice(0, adv).Fill(0xEE);
            writer.Advance(adv);
            written += adv;
        }
        Assert.True(writer.WrittenLength >= total + 8); // 包含 reservation
        Assert.Equal(0, inner.Length); // 仍被阻塞

        // 提交阻塞 reservation -> 应 flush 全部并回到直通
        writer.Commit(t);
        Assert.Equal(writer.WrittenLength, writer.FlushedLength);
        Assert.True(writer.IsPassthrough);

        // 再执行一次小写入，应直接直通不再缓存
        var tail = writer.GetSpan(5); tail[0]=1; tail[1]=2; tail[2]=3; tail[3]=4; tail[4]=5; writer.Advance(5);
        Assert.Equal(writer.WrittenLength, writer.FlushedLength);
        Assert.True(writer.IsPassthrough);
    }
}
