using System.Buffers;

namespace Atelia.Memory;

/// <summary>
/// Configuration options for <see cref="PagedReservableWriter"/>.
/// </summary>
public sealed class PagedReservableWriterOptions {
    /// <summary>
    /// Size of a logical page used for chunk sizing heuristics. Must be positive power of two.
    /// Default: 4096.
    /// </summary>
    public int PageSize { get; set; } = PagedReservableWriter.PageSize; // keep backward const as default

    /// <summary>
    /// Minimum pages per pooled chunk. Default: 1.
    /// </summary>
    public int MinChunkPages { get; set; } = 1;

    /// <summary>
    /// Maximum pages per pooled chunk. Requests larger than MaxChunkPages*PageSize rent a direct oversized buffer.
    /// Default: 256 (1MB at 4KB pages).
    /// </summary>
    public int MaxChunkPages { get; set; } = 256;

    /// <summary>
    /// When true, calling ReserveSpan (or another GetSpan/GetMemory) while a previously obtained buffer
    /// is still outstanding (i.e., Advance not yet called) throws InvalidOperationException.
    /// Default: false (permissive, PipeWriter-like behavior).
    /// </summary>
    public bool EnforceStrictAdvance { get; set; } = false;

    /// <summary>
    /// Optional explicit pool. If null, ArrayPool<byte>.Shared is used.
    /// </summary>
    public ArrayPool<byte>? Pool { get; set; } = null;

    /// <summary>
    /// Shallow clone helper allowing safe mutation of user-provided options after construction.
    /// </summary>
    internal PagedReservableWriterOptions Clone() => new() {
        PageSize = PageSize,
        MinChunkPages = MinChunkPages,
        MaxChunkPages = MaxChunkPages,
        EnforceStrictAdvance = EnforceStrictAdvance,
        Pool = Pool
    };
}
