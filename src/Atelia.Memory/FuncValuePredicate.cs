using System;
using System.Runtime.CompilerServices;

namespace Atelia.Memory;

/// <summary>
/// 将 <see cref="Func{T, Boolean}"/> 适配为 <see cref="IValuePredicate{T}"/> 的值类型包装器，
/// 方便在需要“值类型谓词”优化路径的 API（例如 <see cref="SlidingQueue{T}.DequeueWhile{TPredicate}(TPredicate,bool)"/>）中重用已有委托。
/// 典型用途：已有业务逻辑以 lambda 表达，暂不想手写 struct 谓词时的桥接；性能上仍存在一次委托调用的间接，但避免了重复循环实现。
/// </summary>
/// <remarks>
/// 注意：若需要最大化性能（完全内联），建议直接实现一个小型 struct 自定义谓词，而不是通过 <see cref="FuncValuePredicate{T}"/>。
/// </remarks>
public readonly struct FuncValuePredicate<T> : IValuePredicate<T> where T : notnull {
    private readonly Func<T, bool> _func;

    public FuncValuePredicate(Func<T, bool> func) {
        _func = func ?? throw new ArgumentNullException(nameof(func));
    }

    /// <summary>调用底层委托。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Invoke(T value) => _func(value);

    /// <summary>工厂方法：显式创建。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FuncValuePredicate<T> Create(Func<T, bool> func) => new(func);

    /// <summary>从 <see cref="Func{T, Boolean}"/> 到包装器的隐式转换，便于直接传参。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator FuncValuePredicate<T>(Func<T, bool> func) => new(func);
}
