using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// ChunkedReservableWriter P1 优先级测试
/// 
/// <para>测试架构：</para>
/// <list type="bullet">
/// <item><description>接口级测试（Theory）：LargeSizeHint, TokenUniqueness, Dispose_Idempotent</description></item>
/// <item><description>实现级测试（Fact）：CRW 特有的 PendingReservationCount, IsPassthrough 等诊断属性</description></item>
/// </list>
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

    /// <summary>
    /// 用于接口级测试（不涉及 passthrough 断言）
    /// </summary>
    public static TheoryData<string, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)>> WriterFactories => new() {
        {
            "ChunkedReservableWriter",
            () => {
                var collector = new TestHelpers.CollectingWriter();
                return (new ChunkedReservableWriter(collector), collector.Data);
            }
        },
        {
            "SinkReservableWriter",
            () => {
                var collector = new TestHelpers.CollectingWriter();
                return (new SinkReservableWriter(collector), collector.Data);
            }
        },
    };

    // ========== 接口级测试（Theory） ==========

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void Dispose_Idempotent(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory
    ) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        var s = writer.GetSpan(3);
        s[0] = 1;
        s[1] = 2;
        s[2] = 3;
        writer.Advance(3);
        (writer as IDisposable)?.Dispose();
        // 第二次不应抛异常
        (writer as IDisposable)?.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
    }

    // ========== 接口级边界测试（Theory） ==========

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void LargeSizeHint_GetSpan_ReturnsSpanLengthAtLeastSizeHint(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory
    ) {
        _ = name;
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;

        // 测试大 sizeHint（10MB）是否返回足够空间
        int sizeHint = 10 * 1024 * 1024;
        var span = writer.GetSpan(sizeHint);
        Assert.True(span.Length >= sizeHint,
            $"GetSpan({sizeHint}) returned span of length {span.Length}"
        );
        writer.Advance(0); // release span
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void Bijection_TokenUniqueness_NoImmediateCollision(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory
    ) {
        _ = name;
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;

        // 验证 2000 个 reservation token 无重复
        const int n = 2000;
        var tokens = new HashSet<int>(n);
        var tokenList = new int[n];
        for (int i = 0; i < n; i++) {
            writer.ReserveSpan(1, out int token, null)[0] = (byte)(i & 0xFF);
            tokenList[i] = token;
            Assert.True(tokens.Add(token), $"Token collision at iteration {i}: token={token}");
        }

        // 全部提交确保结构清理逻辑正常
        foreach (var tk in tokenList) {
            writer.Commit(tk);
        }
    }
}
