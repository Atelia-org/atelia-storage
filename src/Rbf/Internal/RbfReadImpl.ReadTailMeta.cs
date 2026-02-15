using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf.Internal;

/// <summary>RBF TailMeta 读取操作。</summary>
internal static partial class RbfReadImpl {
    /// <summary>从 SizedPtr 读取 TailMeta（调用方提供 buffer，L2 信任）。</summary>
    /// <param name="file">RBF 文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 实现：内部先调用 ReadFrameInfo 获取已验证的 RbfFrameInfo，再调用实例方法。
    /// I/O：读取 TrailerCodeword（16B）+ TailMeta 区域。
    /// 生命周期：返回的 TailMeta 直接引用 buffer，调用方 MUST 确保 buffer 有效。
    /// </remarks>
    public static AteliaResult<RbfTailMeta> ReadTailMeta(
        RandomAccessReader reader,
        SizedPtr ticket,
        Span<byte> buffer
    ) {
        // 1. 先获取帧元信息（读取 TrailerCodeword 16B，完成所有结构性验证）
        var infoResult = ReadFrameInfo(reader, ticket);
        if (!infoResult.IsSuccess) { return AteliaResult<RbfTailMeta>.Failure(infoResult.Error!); }

        // 2. 委托到实例方法（只做 I/O 级校验）
        return infoResult.Value.ReadTailMeta(buffer);
    }

    /// <summary>从 SizedPtr 读取 TailMeta（自动租用 buffer，L2 信任）。</summary>
    /// <param name="file">RBF 文件句柄。</param>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledTailMeta，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 实现：内部先调用 ReadFrameInfo 获取已验证的 RbfFrameInfo，再调用实例方法。
    /// I/O：读取 TrailerCodeword（16B）+ TailMeta 区域。
    /// </remarks>
    public static AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(
        RandomAccessReader reader,
        SizedPtr ticket
    ) {
        // 1. 先获取帧元信息（读取 TrailerCodeword 16B，完成所有结构性验证）
        var infoResult = ReadFrameInfo(reader, ticket);
        if (!infoResult.IsSuccess) { return AteliaResult<RbfPooledTailMeta>.Failure(infoResult.Error!); }

        // 2. 委托到实例方法（只做 I/O 级校验）
        return infoResult.Value.ReadPooledTailMeta();
    }
}
