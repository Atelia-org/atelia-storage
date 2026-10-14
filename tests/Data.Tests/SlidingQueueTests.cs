using System;
using System.Collections;
using System.Collections.Generic;
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

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void EnqueueRange_InputThrows_KeepsPrefixAndInvalidatesOnlyAfterMutation(bool collection, int added) {
        var queue = new SlidingQueue<int>(16);
        queue.Enqueue(10);
        var enumerator = queue.GetEnumerator();
        var failure = new InvalidOperationException("input failed");
        IEnumerable<int> source = FailAfter(added, failure);
        if (collection) { source = new ReadOnlyInput(source, added + 1); }

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => queue.EnqueueRange(source)));
        Assert.Equal(added + 1, queue.Count);
        Assert.Equal(10, queue[0]);
        for (int i = 0; i < added; i++) { Assert.Equal(i, queue[i + 1]); }
        AssertEnumeratorState(ref enumerator, changed: added != 0);
        queue.Enqueue(20);
        Assert.Equal(20, queue.PeekLast());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void DequeueWhile_PredicateThrows_KeepsRemainingSuffixAndInvalidatesOnlyAfterMutation(bool valuePredicate, int removed) {
        var queue = new SlidingQueue<int>(16);
        queue.EnqueueRange(new[] { 0, 1, 2, 3 });
        var enumerator = queue.GetEnumerator();
        var failure = new InvalidOperationException("predicate failed");

        Exception actual = valuePredicate
            ? Assert.Throws<InvalidOperationException>(() => queue.DequeueWhile(new FailingPredicate(removed, failure)))
            : Assert.Throws<InvalidOperationException>(() => queue.DequeueWhile(value => value < removed ? true : throw failure));

        Assert.Same(failure, actual);
        Assert.Equal(4 - removed, queue.Count);
        Assert.Equal(removed, queue.PeekFirst());
        Assert.Equal(new[] { 0, 1, 2, 3 }.AsSpan(removed).ToArray(), queue.ToArray());
        AssertEnumeratorState(ref enumerator, changed: removed != 0);
        Assert.Equal(4 - removed, queue.DequeueWhile(_ => true));
        Assert.True(queue.IsEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnqueueRange_InputObservesPartialMutation_ExistingEnumeratorsFailFast(bool collection) {
        var queue = new SlidingQueue<int>(16);
        var enumerator = queue.GetEnumerator();
        IEnumerable<int> Input() {
            yield return 1;
            Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
            enumerator = queue.GetEnumerator();
            yield return 2;
            Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        }

        IEnumerable<int> source = Input();
        if (collection) { source = new ReadOnlyInput(source, 2); }
        queue.EnqueueRange(source);
        Assert.Equal(new[] { 1, 2 }, queue.ToArray());
    }

    [Fact]
    public void DequeueWhile_PredicateObservesPartialMutation_ExistingEnumeratorsFailFast() {
        var queue = new SlidingQueue<int>(16);
        queue.EnqueueRange(new[] { 1, 2, 3 });
        var enumerator = queue.GetEnumerator();
        Assert.Equal(3, queue.DequeueWhile(value => {
            if (value > 1) { Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext()); }
            enumerator = queue.GetEnumerator();
            return true;
        }));
        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
    }

    private static IEnumerable<int> FailAfter(int count, Exception failure) {
        for (int i = 0; i < count; i++) { yield return i; }
        throw failure;
    }

    private static void AssertEnumeratorState(ref SlidingQueue<int>.Enumerator enumerator, bool changed) {
        if (changed) {
            var copy = enumerator;
            Assert.Throws<InvalidOperationException>(() => copy.MoveNext());
            Assert.Throws<InvalidOperationException>(() => copy.Reset());
        }
        else { Assert.True(enumerator.MoveNext()); }
    }

    private readonly record struct FailingPredicate(int FirstFailure, Exception Failure) : IValuePredicate<int> {
        public bool Invoke(int value) => value < FirstFailure ? true : throw Failure;
    }

    private sealed class ReadOnlyInput(IEnumerable<int> source, int count) : ICollection<int> {
        public int Count => count;
        public bool IsReadOnly => true;
        public IEnumerator<int> GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Add(int item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(int item) => throw new NotSupportedException();
        public void CopyTo(int[] array, int arrayIndex) => throw new NotSupportedException();
        public bool Remove(int item) => throw new NotSupportedException();
    }
}
