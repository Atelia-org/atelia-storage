using System;

namespace Atelia.Data;

/// <summary>
/// A synchronous, push-based sink for byte data.
/// </summary>
/// <remarks>
/// Contract:
/// - The sink must fully consume the provided data before <see cref="Push"/> returns.
/// - The sink must not retain references to the underlying buffer after returning.
/// - Exceptions indicate failure; the caller will not advance internal positions when <see cref="Push"/> throws.
///
/// This interface exists to decouple <see cref="SinkReservableWriter"/> from the pull-based
/// <c>IBufferWriter&lt;byte&gt;</c> API, enabling one-layer buffering with backfill (reserve/commit) semantics.
/// </remarks>
public interface IByteSink {
    /// <summary>
    /// Consumes a contiguous span of bytes synchronously.
    /// </summary>
    void Push(ReadOnlySpan<byte> data);
}
