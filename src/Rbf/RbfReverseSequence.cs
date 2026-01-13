namespace Atelia.Rbf;

/// <summary>
/// 逆向扫描序列（duck-typed 枚举器，支持 foreach）。
/// </summary>
/// <remarks>
/// <para><b>设计说明</b>：返回 ref struct 而非 IEnumerable，因为 RbfFrame 是 ref struct。</para>
/// <para>上层通过 foreach 消费，不依赖 LINQ。</para>
/// </remarks>
public ref struct RbfReverseSequence {
    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfReverseEnumerator GetEnumerator() {
        throw new NotImplementedException();
    }
}
