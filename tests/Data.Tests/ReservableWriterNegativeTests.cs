using System;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// 针对 IReservableBufferWriter 实现的健壮性 / 负面路径 / 关键语义补充测试
/// 
/// <para>测试架构：</para>
/// <list type="bullet">
/// <item><description>接口级测试（Theory）：验证 IReservableBufferWriter 契约，覆盖 ChunkedReservableWriter 和 SinkReservableWriter</description></item>
/// <item><description>实现级测试（Fact）：验证特定实现的诊断属性和优化行为</description></item>
/// </list>
/// 
/// 覆盖 ChunkedReservableWriter 和 SinkReservableWriter 两种实现：
/// 1. Commit_InvalidToken_Throws
/// 2. Commit_DoubleCommit_Throws
/// 3. Commit_NonBlockingReservation_DoesNotFlushEarlierData
/// 4. ReserveSpan_NonPositiveCount_Throws
/// 5. Advance_ExceedLastSpan_Throws
/// 6. Reset_InvalidatesOldReservationTokens（具体类型测试）
/// </summary>
public class ReservableWriterNegativeTests {
    // ========== 工厂定义 ==========
    // 使用 Func<(IReservableBufferWriter, Func<byte[]>)> 工厂模式，隐藏 CollectingWriter 实现细节
    // 返回值: (writer 实例, getData 委托用于获取输出数据)

    /// <summary>
    /// 用于大多数接口级测试（不涉及 passthrough 断言）
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

    /// <summary>
    /// 用于 passthrough 相关测试（带 shouldTestPassthrough 标志）
    /// - ChunkedReservableWriter: shouldTestPassthrough=true，验证 passthrough 优化
    /// - SinkReservableWriter: shouldTestPassthrough=false，总是 buffered，跳过 passthrough 断言
    /// </summary>
    public static TheoryData<string, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)>, bool> WriterFactoriesWithPassthrough => new() {
        {
            "ChunkedReservableWriter",
            () => {
                var collector = new TestHelpers.CollectingWriter();
                return (new ChunkedReservableWriter(collector), collector.Data);
            },
            true  // shouldTestPassthrough: 验证 passthrough 优化
        },
        {
            "SinkReservableWriter",
            () => {
                var collector = new TestHelpers.CollectingWriter();
                return (new SinkReservableWriter(collector), collector.Data);
            },
            false  // shouldTestPassthrough: 总是 buffered，跳过 passthrough 断言
        },
    };

    // ========== 接口级测试（Theory） ==========

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void Commit_InvalidToken_Throws(string name, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;
        // 直接使用一个极不可能被分配的 token（实现递增 + 混洗，不会跳到 int.MaxValue）
        Assert.Throws<InvalidOperationException>(() => writer.Commit(int.MaxValue));
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void Commit_DoubleCommit_Throws(string name, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;
        writer.ReserveSpan(4, out int token, "r1").Clear();
        writer.Commit(token); // 首次成功
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token)); // 再次应失败
    }

    [Theory]
    [MemberData(nameof(WriterFactoriesWithPassthrough))]
    public void Commit_NonBlockingReservation_DoesNotFlushEarlierData(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory,
        bool shouldTestPassthrough
    ) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, getData) = factory();
        using var disposable = writer as IDisposable;

        // 写入前置数据
        var pre = writer.GetSpan(2);
        pre[0] = 0xAA;
        pre[1] = 0xBB;
        writer.Advance(2);

        // 验证无 reservation 时立即 passthrough（仅 ChunkedReservableWriter）
        if (shouldTestPassthrough) {
            Assert.Equal(new byte[] { 0xAA, 0xBB }, getData());
        }

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

        // 验证非阻塞 reservation 不触发前置数据的 flush（仅 ChunkedReservableWriter）
        if (shouldTestPassthrough) {
            // r1 仍阻塞，只有 pre 数据被 flush
            Assert.Equal(new byte[] { 0xAA, 0xBB }, getData());
        }

        // 现在提交 r1，触发统一 flush，顺序：AA BB + r1(1,2) + r2(3,4) + tail(EE EF F0)
        writer.Commit(t1);
        var expected = new byte[] { 0xAA, 0xBB, 1, 2, 3, 4, 0xEE, 0xEF, 0xF0 };
        Assert.Equal(expected, getData());
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void ReserveSpan_NonPositiveCount_Throws(string name, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ReserveSpan(0, out _, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ReserveSpan(-1, out _, null));
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void Advance_ExceedLastSpan_Throws(string name, Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;
        var span = writer.GetSpan(8);
        span[0] = 42; // 写入部分
        // 注意：GetSpan 返回的实际 span 可能比 sizeHint 更大（取决于 chunk 大小）
        // 所以我们用实际 span 长度 + 1 来保证超出范围
        int actualLen = span.Length;
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(actualLen + 1));
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void GetSpanTwiceWithoutAdvance_Throws(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory
    ) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;
        var first = writer.GetSpan(4);
        first[0] = 42;
        var ex = Assert.Throws<InvalidOperationException>(() => writer.GetSpan(2));
        Assert.Contains("Previous buffer not advanced", ex.Message);
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void GetMemoryTwiceWithoutAdvance_Throws(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory
    ) {
        _ = name; // 用于 xUnit 测试名称显示
        var (writer, _) = factory();
        using var disposable = writer as IDisposable;
        var first = writer.GetMemory(4);
        first.Span[0] = 11;
        var ex = Assert.Throws<InvalidOperationException>(() => writer.GetMemory(1));
        Assert.Contains("Previous buffer not advanced", ex.Message);
    }

    [Theory]
    [MemberData(nameof(WriterFactories))]
    public void ReserveSpanAfterAdvanceZero_Allows(
        string name,
        Func<(IReservableBufferWriter Writer, Func<byte[]> GetData)> factory
    ) {
        _ = name;
        var (writer, getData) = factory();
        using var disposable = writer as IDisposable;

        // GetSpan 后 Advance(0) 应允许后续 ReserveSpan
        var span = writer.GetSpan(4);
        span.Fill(0); // 写入但不保留
        writer.Advance(0); // 显式释放 span

        // 应允许 ReserveSpan（不抛异常）
        var reserved = writer.ReserveSpan(4, out int token, null);
        reserved.Fill(42);
        writer.Commit(token);

        Assert.Equal(new byte[] { 42, 42, 42, 42 }, getData());
    }

    // ========== 具体类型测试（Fact） ==========

    [Fact]
    public void ChunkedReservableWriter_Reset_InvalidatesOldReservationTokens() {
        var inner = new TestHelpers.CollectingWriter();
        using var writer = new ChunkedReservableWriter(inner);
        writer.ReserveSpan(4, out int token, "will-reset");
        writer.Reset();
        // Reset 清空 token 结构，再次提交旧 token 应视为无效
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsPassthrough);
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));
    }

    [Fact]
    public void SinkReservableWriter_Reset_InvalidatesOldReservationTokens() {
        var collector = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(collector);
        writer.ReserveSpan(4, out int token, "will-reset");
        writer.Reset();
        // Reset 清空 token 结构，再次提交旧 token 应视为无效
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsIdle);
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));
    }
}
