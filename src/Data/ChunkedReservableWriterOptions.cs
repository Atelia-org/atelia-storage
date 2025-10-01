using System;
using System.Buffers;

namespace Atelia.Data;

/// <summary>
/// Configuration options for <see cref="ChunkedReservableWriter"/> (chunk-size based).
/// </summary>
public sealed class ChunkedReservableWriterOptions {
    // ---------------- New (preferred) properties ----------------
    /// <summary>
    /// 最小 Chunk 字节数。必须 &gt;= 1024。默认：4096。
    /// </summary>
    public int MinChunkSize { get; set; } = 4096;

    /// <summary>
    /// 最大 Chunk 字节数（不含一次性超大直租请求）。默认：65536 (64KB)，避免落入 LOH。
    /// </summary>
    public int MaxChunkSize { get; set; } = 64 * 1024;

    /// <summary>
    /// 可选显式 ArrayPool。为空则使用 ArrayPool&lt;byte&gt;.Shared。
    /// </summary>
    public ArrayPool<byte>? Pool { get; set; } = null;

    /// <summary>
    /// 可选调试输出回调，例如 DebugUtil.Print。参数依次为 category 和 message。
    /// </summary>
    public Action<string, string>? DebugLog { get; set; } = null;

    /// <summary>
    /// DebugLog 使用的默认类别。未设置 DebugLog 时忽略。默认："BinaryLog"。
    /// </summary>
    public string DebugCategory { get; set; } = "BinaryLog";

    internal ChunkedReservableWriterOptions Clone() => new() {
        MinChunkSize = MinChunkSize,
        MaxChunkSize = MaxChunkSize,
        Pool = Pool,
        DebugLog = DebugLog,
        DebugCategory = DebugCategory
    };
}
