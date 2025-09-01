using System.Buffers;

namespace Atelia.Data;

/// <summary>
/// Configuration options for <see cref="ChunkedReservableWriter"/> (chunk-size based).
/// </summary>
public sealed class ChunkedReservableWriterOptions {
    // ---------------- New (preferred) properties ----------------
    /// <summary>
    /// 最小 Chunk 字节数。必须 >= 1024。默认：4096。
    /// </summary>
    public int MinChunkSize { get; set; } = 4096;

    /// <summary>
    /// 最大 Chunk 字节数（不含一次性超大直租请求）。默认：65536 (64KB)，避免落入 LOH。
    /// </summary>
    public int MaxChunkSize { get; set; } = 64 * 1024;

    /// <summary>
    /// 是否严格要求在再次获取缓冲或预留前必须 Advance 已获取的缓冲。
    /// </summary>
    public bool EnforceStrictAdvance { get; set; } = false;

    /// <summary>
    /// 可选显式 ArrayPool。为空则使用 ArrayPool<byte>.Shared。
    /// </summary>
    public ArrayPool<byte>? Pool { get; set; } = null;

    internal ChunkedReservableWriterOptions Clone() => new() {
        MinChunkSize = MinChunkSize,
        MaxChunkSize = MaxChunkSize,
        EnforceStrictAdvance = EnforceStrictAdvance,
        Pool = Pool
    };
}
