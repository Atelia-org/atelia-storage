namespace Atelia.Data;

/// <summary>
/// 保留零的 32bit 带 key 可逆置换。同一函数用于编码和解码，不提供密码学保护。
/// </summary>
/// <remarks>
/// 零和 key 自身保持不变；其他值执行 XOR。同 key 连续应用两次恢复原值，
/// 非零输入始终得到非零输出。key 可以为零，此时置换为恒等映射。
/// 范围、存在性及跨上下文误用检查由调用方负责；错 key 也可能得到有效值。
/// 算法的准确位映射是持久表示的一部分，不能在保留已有编码值时替换算法。
/// </remarks>
public static class ZeroPreservingXor32 {
    /// <summary>对 value 应用自逆置换；编码和解码均调用本方法。</summary>
    public static uint Apply(uint value, uint key) => value == 0 || value == key ? value : value ^ key;
}
