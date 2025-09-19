using System;
using Xunit;

namespace Atelia.Data.Tests;

public class SlidingQueueTests {
    [Fact]
    public void EnsureCapacity_DoesNotExpand_WhenWithinCapacityAndCompactsIfNeeded() {
        var q = new SlidingQueue<int>(capacity: 16);
        // 填充并消费一半，制造前缀空洞
        for (int i = 0; i < 12; i++) {
            q.Enqueue(i);
        }

        for (int i = 0; i < 8; i++) {
            q.Dequeue(); // _head = 8, Count=4, Capacity>=16
        }
        // 现在尾部剩余直接可插入空间 = 16 - 12 = 4
        // 目标容量 12 (<=16)，logicalNeededFree = 12-4=8 > freeTail=4, 触发一次 Compact(force)
        int beforeVersionCap = q.EnsureCapacity(12);
        Assert.Equal(16, beforeVersionCap); // 容量不变
        Assert.Equal(4, q.Count);
        Assert.Equal(0, q.DebugHeadIndex); // 已压缩
    }

    [Fact]
    public void EnqueueRange_SelfReference_Succeeds() {
        var q = new SlidingQueue<int>();
        for (int i = 0; i < 5; i++) {
            q.Enqueue(i);
        }

        q.Dequeue(); // head=1
        // 自引用：应复制当前活动区 [1..4]
        q.EnqueueRange(q);
        // 现在应包含 1,2,3,4,1,2,3,4 (按活动区快照)
        Assert.Equal(8, q.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 1, 2, 3, 4 }, q.ToArray());
    }

    [Fact]
    public void TrimExcess_CompactsThenShrinksCapacity() {
        var q = new SlidingQueue<int>(capacity: 64);
        for (int i = 0; i < 40; i++) {
            q.Enqueue(i);
        }

        for (int i = 0; i < 30; i++) {
            q.Dequeue(); // head=30, Count=10
        }

        int capBefore = q.Capacity;
        q.TrimExcess();
        Assert.Equal(10, q.Count);
        Assert.Equal(0, q.DebugHeadIndex); // 被压缩
        Assert.True(q.Capacity <= capBefore); // 容量收缩（可能策略性 >= Count）
        Assert.Equal(new[] { 30, 31, 32, 33, 34, 35, 36, 37, 38, 39 }, q.ToArray());
    }

    private readonly struct LessThanPredicate : IValuePredicate<int> {
        private readonly int _limit;
        public LessThanPredicate(int limit) => _limit = limit;
        public bool Invoke(int value) => value < _limit;
    }

    [Fact]
    public void DequeueWhile_ValuePredicate_Works() {
        var q = new SlidingQueue<int>();
        for (int i = 0; i < 10; i++) {
            q.Enqueue(i);
        }

        int removed = q.DequeueWhile(new LessThanPredicate(4));
        Assert.Equal(4, removed);
        Assert.Equal(6, q.Count);
        Assert.Equal(4, q.PeekFirst());
    }
}
