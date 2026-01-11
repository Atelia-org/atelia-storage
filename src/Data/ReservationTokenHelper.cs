using System.Runtime.CompilerServices;

namespace Atelia.Data;

/// <summary>
/// Reservation token 生成与映射辅助类
/// </summary>
internal static class ReservationTokenHelper {
    /// <summary>
    /// Bijection 函数：uint → uint 双射，用于将连续序列号映射为随机分布的 token。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Bijection(uint x) {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    /// <summary>
    /// 分配新的 reservation token，递增序列号并通过 Bijection 映射。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AllocToken(ref uint serial) {
        return unchecked((int)Bijection(++serial));
    }
}
