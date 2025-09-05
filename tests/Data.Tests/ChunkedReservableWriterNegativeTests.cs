using System;
using System.Buffers;
using System.IO;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// 针对 ChunkedReservableWriter 的健壮性 / 负面路径 / 关键语义补充测试
/// 覆盖分析中选定的首批高优先级(P0)用例：
/// 1. Commit_InvalidToken_Throws
/// 2. Commit_DoubleCommit_Throws
/// 3. Commit_NonBlockingReservation_DoesNotFlushEarlierData
/// 4. ReserveSpan_NonPositiveCount_Throws
/// 5. Advance_ExceedLastSpan_Throws
/// 6. Reset_InvalidatesOldReservationTokens
/// （保持范围克制：不引入复杂随机 / 属性测试，专注最易遗漏的语义）
/// </summary>
public class ChunkedReservableWriterNegativeTests {
    /// <summary>
    /// 轻量 inner writer：追加写入并可读取已写数据
    /// </summary>
    private sealed class CollectingWriter : IBufferWriter<byte> {
        private MemoryStream _stream = new();
        private int _pos;
        public void Advance(int count) {
            _pos += count;
            if (_pos > _stream.Length) {
                _stream.SetLength(_pos);
            }
        }
        public Memory<byte> GetMemory(int sizeHint = 0) {
            int need = _pos + Math.Max(sizeHint, 1);
            if (_stream.Length < need) {
                _stream.SetLength(need);
            }

            return _stream.GetBuffer().AsMemory(_pos, (int)_stream.Length - _pos);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public byte[] Data() {
            var a = new byte[_pos];
            Array.Copy(_stream.GetBuffer(), 0, a, 0, _pos);
            return a;
        }
    }

    [Fact]
    public void Commit_InvalidToken_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        // 直接使用一个极不可能被分配的 token（实现递增 + 混洗，不会跳到 int.MaxValue）
        Assert.Throws<InvalidOperationException>(() => writer.Commit(int.MaxValue));
    }

    [Fact]
    public void Commit_DoubleCommit_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        writer.ReserveSpan(4, out int token, "r1").Clear();
        writer.Commit(token); // 首次成功
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token)); // 再次应失败
    }

    [Fact]
    public void Commit_NonBlockingReservation_DoesNotFlushEarlierData() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);

        // 写入前置数据（应立即直通）
        var pre = writer.GetSpan(2);
        pre[0] = 0xAA;
        pre[1] = 0xBB;
        writer.Advance(2);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, inner.Data());

        // 建立两个 reservation：先 r1 再 r2
        var r1 = writer.ReserveSpan(2, out int t1, "r1");
        var r2 = writer.ReserveSpan(2, out int t2, "r2");
        r1[0] = 1;
        r1[1] = 2;
        r2[0] = 3;
        r2[1] = 4; // 填充但不提交

        // 在 reservations 之后再追加普通数据 (被 r1 阻塞 flush)
        var tail = writer.GetSpan(3);
        tail[0] = 0xEE;
        tail[1] = 0xEF;
        tail[2] = 0xF0;
        writer.Advance(3);

        // 先提交非阻塞（顺序后来的 r2），不应触发任何新 flush
        writer.Commit(t2);
        // 仍只应看到 pre 数据（因为 r1 仍阻塞）
        Assert.Equal(new byte[] { 0xAA, 0xBB }, inner.Data());

        // 现在提交 r1，触发统一 flush，顺序：AA BB + r1(1,2) + r2(3,4) + tail(EE EF F0)
        writer.Commit(t1);
        var expected = new byte[] { 0xAA, 0xBB, 1, 2, 3, 4, 0xEE, 0xEF, 0xF0 };
        Assert.Equal(expected, inner.Data());
    }

    [Fact]
    public void ReserveSpan_NonPositiveCount_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ReserveSpan(0, out _, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ReserveSpan(-1, out _, null));
    }

    [Fact]
    public void Advance_ExceedLastSpan_Throws() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        var span = writer.GetSpan(8);
        span[0] = 42; // 写入部分
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(9)); // 超过可用长度
    }

    [Fact]
    public void Reset_InvalidatesOldReservationTokens() {
        var inner = new CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        writer.ReserveSpan(4, out int token, "will-reset");
        writer.Reset();
        // Reset 清空 token 结构，再次提交旧 token 应视为无效
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsPassthrough);
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));
    }
}
