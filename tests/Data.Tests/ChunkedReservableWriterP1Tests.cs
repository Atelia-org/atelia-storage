using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// P1 批次：语义补充分层（不引入额外框架）
/// 覆盖：
/// - NonBlockingCommitThenBlockingCommit_FlushesAll
/// - Reset_ClearsLengthsAndPassthrough
/// - Dispose_Idempotent
/// - LargeSizeHint_GetSpan_ReturnsSpanLengthAtLeastSizeHint
/// - Bijection_TokenUniqueness_NoImmediateCollision
/// </summary>
public class ChunkedReservableWriterP1Tests {
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
    public void NonBlockingCommitThenBlockingCommit_FlushesAll() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);

        // 两个 reservation + 后续普通数据
        var r1 = writer.ReserveSpan(2, out int t1, "r1");
        r1[0] = 1;
        r1[1] = 2;
        var r2 = writer.ReserveSpan(2, out int t2, "r2");
        r2[0] = 3;
        r2[1] = 4;
        var tail = writer.GetSpan(3);
        tail[0] = 0xEE;
        tail[1] = 0xEF;
        tail[2] = 0xF0;
        writer.Advance(3);

        // 先提交 r2（非阻塞）不应 flush
        writer.Commit(t2);
        Assert.Empty(inner.Data());
        Assert.Equal(1, writer.PendingReservationCount); // r1 仍存在

        // 再提交 r1 -> 应一次性 flush r1 + r2 + tail
        writer.Commit(t1);
        var expected = new byte[] { 1, 2, 3, 4, 0xEE, 0xEF, 0xF0 };
        Assert.Equal(expected, inner.Data());
        Assert.True(writer.IsPassthrough);
    }

    [Fact]
    public void Reset_ClearsLengthsAndPassthrough() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        // 构造一些状态：写入 + reservation
        var s = writer.GetSpan(5);
        s[0] = 10;
        s[1] = 11;
        s[2] = 12;
        s[3] = 13;
        s[4] = 14;
        writer.Advance(5);
        writer.ReserveSpan(4, out int token, "block");
        Assert.False(writer.IsPassthrough);
        Assert.True(writer.WrittenLength > 0);
        Assert.True(writer.PendingReservationCount == 1);

        writer.Reset();
        Assert.Equal(0, writer.WrittenLength);
        Assert.Equal(0, writer.FlushedLength);
        Assert.Equal(0, writer.PendingLength);
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsPassthrough);
        // 旧 token 失效（已在 P0 测试覆盖，再做一次冗余防护）
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));
    }

    [Fact]
    public void Dispose_Idempotent() {
        var inner = new CollectingWriter();
        var writer = new ChunkedReservableWriter(inner);
        var s = writer.GetSpan(3);
        s[0] = 1;
        s[1] = 2;
        s[2] = 3;
        writer.Advance(3);
        writer.Dispose();
        // 第二次不应抛异常
        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
    }

    [Fact]
    public void LargeSizeHint_GetSpan_ReturnsSpanLengthAtLeastSizeHint() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        int large = 500_000; // < 256 * 4096 (1,048,576) 保证可分配
        var span = writer.GetSpan(large);
        Assert.True(span.Length >= large);
        writer.Advance(0); // release passthrough span before switching to buffered mode
        // 进入缓冲模式后再次测试（通过一个 reservation 强制 chunk 分配，再请求大 span）
        writer.ReserveSpan(16, out int t, null).Clear();
        var span2 = writer.GetSpan(large);
        Assert.True(span2.Length >= large);
        // Advance 一部分确保无异常
        writer.Advance(1000);
        writer.Commit(t);
    }

    [Fact]
    public void Bijection_TokenUniqueness_NoImmediateCollision() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        const int n = 2000; // 快速，但足以证明线性迭代无碰撞
        var set = new HashSet<int>();
        var tokens = new int[n];
        for (int i = 0; i < n; i++) {
            writer.ReserveSpan(1, out int token, null)[0] = (byte)i;
            tokens[i] = token;
            Assert.True(set.Add(token), $"Duplicate token at {i} -> {token}");
        }
        // 全部提交（保证结构清理逻辑不崩）
        foreach (var tk in tokens) {
            writer.Commit(tk);
        }

        Assert.True(writer.IsPassthrough);
    }
}
