namespace Atelia.Rbf;

/// <summary>
/// RBF 帧扫描器接口。支持逆向扫描和随机读取。
/// </summary>
/// <remarks>
/// <para><b>[A-RBF-SCANNER-INTERFACE]</b>: IRbfScanner 接口定义。</para>
/// <para><b>[R-REVERSE-SCAN-ALGORITHM]</b>: 从文件尾部向前扫描 Fence，验证帧完整性。</para>
/// <para><b>[R-RESYNC-BEHAVIOR]</b>: 校验失败时按 4B 步长向前搜索下一个 Fence。</para>
/// <para><b>[S-RBF-TOMBSTONE-VISIBLE]</b>: Scanner MUST 产出所有通过 framing/CRC 校验的帧，包括 Tombstone。</para>
/// </remarks>
public interface IRbfScanner
{
    /// <summary>
    /// 读取指定地址的帧。
    /// </summary>
    /// <param name="address">帧起始地址（指向 HeadLen 字段）。</param>
    /// <param name="frame">输出：帧元数据。</param>
    /// <returns>是否成功读取（通过 framing/CRC 校验）。</returns>
    /// <remarks>
    /// <para><b>[F-ADDRESS64-ALIGNMENT]</b>: 地址必须 4 字节对齐。</para>
    /// <para><b>[F-FRAMING-FAIL-REJECT]</b>: 任一校验不满足时返回 false。</para>
    /// <para><b>[F-CRC-FAIL-REJECT]</b>: CRC 不匹配时返回 false。</para>
    /// </remarks>
    bool TryReadAt(Address64 address, out RbfFrame frame);

    /// <summary>
    /// 从文件尾部逆向扫描所有帧。
    /// </summary>
    /// <returns>帧枚举（从尾到头）。</returns>
    /// <remarks>
    /// <para><b>[R-REVERSE-SCAN-ALGORITHM]</b>: 从尾部向前扫描，遇到损坏时 Resync。</para>
    /// <para><b>[S-RBF-TOMBSTONE-VISIBLE]</b>: 包含 Tombstone 帧。</para>
    /// </remarks>
    IEnumerable<RbfFrame> ScanReverse();

    /// <summary>
    /// 读取帧的 Payload 数据。
    /// </summary>
    /// <param name="frame">帧元数据。</param>
    /// <returns>Payload 字节数组（拷贝）。</returns>
    /// <remarks>
    /// 返回的数组是数据的拷贝，生命周期独立于 Scanner。
    /// </remarks>
    byte[] ReadPayload(in RbfFrame frame);
}
