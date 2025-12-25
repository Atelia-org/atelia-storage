namespace Atelia.Rbf;

/// <summary>
/// RBF 帧状态标记（位域格式）。
/// </summary>
/// <remarks>
/// <para><b>[F-FRAMESTATUS-VALUES]</b>: FrameStatus 采用位域格式，同时编码帧状态和 StatusLen。</para>
/// <para>
/// 位布局：
/// <list type="bullet">
///   <item>Bit 7: Tombstone (0=Valid, 1=Tombstone)</item>
///   <item>Bit 6-2: Reserved (MUST be 0 for MVP)</item>
///   <item>Bit 1-0: StatusLen - 1 (00=1, 01=2, 10=3, 11=4)</item>
/// </list>
/// </para>
/// </remarks>
public readonly struct FrameStatus : IEquatable<FrameStatus>
{
    /// <summary>
    /// Tombstone 位掩码（Bit 7）。
    /// </summary>
    private const byte TombstoneMask = 0x80;

    /// <summary>
    /// StatusLen 位掩码（Bit 0-1）。
    /// </summary>
    private const byte StatusLenMask = 0x03;

    /// <summary>
    /// 保留位掩码（Bit 2-6）。
    /// </summary>
    private const byte ReservedMask = 0x7C;

    private readonly byte _value;

    private FrameStatus(byte value) => _value = value;

    /// <summary>
    /// 原始字节值。
    /// </summary>
    public byte Value => _value;

    /// <summary>
    /// 是否为墓碑帧。
    /// </summary>
    public bool IsTombstone => (_value & TombstoneMask) != 0;

    /// <summary>
    /// 是否为有效帧（非墓碑）。
    /// </summary>
    public bool IsValid => (_value & TombstoneMask) == 0;

    /// <summary>
    /// 状态字节数（1-4）。
    /// </summary>
    public int StatusLen => (_value & StatusLenMask) + 1;

    /// <summary>
    /// 检查是否为 MVP 合法值（保留位为 0）。
    /// </summary>
    public bool IsMvpValid => (_value & ReservedMask) == 0;

    /// <summary>
    /// 创建有效帧状态（Valid）。
    /// </summary>
    /// <param name="statusLen">状态字节数（1-4）。</param>
    /// <returns>FrameStatus 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">statusLen 不在 1-4 范围内。</exception>
    public static FrameStatus CreateValid(int statusLen)
    {
        ValidateStatusLen(statusLen);
        return new FrameStatus((byte)(statusLen - 1));
    }

    /// <summary>
    /// 创建墓碑帧状态（Tombstone）。
    /// </summary>
    /// <param name="statusLen">状态字节数（1-4）。</param>
    /// <returns>FrameStatus 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">statusLen 不在 1-4 范围内。</exception>
    public static FrameStatus CreateTombstone(int statusLen)
    {
        ValidateStatusLen(statusLen);
        return new FrameStatus((byte)(TombstoneMask | (statusLen - 1)));
    }

    /// <summary>
    /// 从原始字节创建 FrameStatus。
    /// </summary>
    /// <param name="value">原始字节值。</param>
    /// <returns>FrameStatus 实例。</returns>
    public static FrameStatus FromByte(byte value) => new(value);

    private static void ValidateStatusLen(int statusLen)
    {
        if (statusLen < 1 || statusLen > 4)
            throw new ArgumentOutOfRangeException(nameof(statusLen), statusLen, "StatusLen must be between 1 and 4.");
    }

    /// <inheritdoc/>
    public bool Equals(FrameStatus other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FrameStatus other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => $"FrameStatus(0x{_value:X2}, {(IsTombstone ? "Tombstone" : "Valid")}, StatusLen={StatusLen})";

    /// <summary>
    /// 相等运算符。
    /// </summary>
    public static bool operator ==(FrameStatus left, FrameStatus right) => left.Equals(right);

    /// <summary>
    /// 不等运算符。
    /// </summary>
    public static bool operator !=(FrameStatus left, FrameStatus right) => !left.Equals(right);
}
