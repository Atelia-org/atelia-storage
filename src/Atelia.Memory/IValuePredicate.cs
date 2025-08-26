using System;

namespace Atelia.Memory;

/// <summary>
/// 高性能值类型谓词接口：用于 <see cref="SlidingQueue{T}.DequeueWhile{TPredicate}(TPredicate,bool)"/>
/// 以避免 <see cref="Func{T, Boolean}"/> 委托调用的间接与可能的分配。
/// 建议实现为小型只读 struct；若需要捕获外部状态，请显式传入所需字段。
/// </summary>
/// <typeparam name="T">元素类型。</typeparam>
public interface IValuePredicate<T> where T : notnull {
    /// <summary>返回该元素是否应被消费（true=继续出队）。</summary>
    bool Invoke(T value);
}
