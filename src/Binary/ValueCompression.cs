namespace Atelia.Binary;

/// <summary>Chooses whether preparation attempts compression; these values are not wire control bytes.</summary>
/// <remarks>
/// The supported method set may grow while preparation, exact measurement, writing, and reading keep their public signatures.
/// TODO (.NET 11 GA): add Zstandard and raw RFC 1951 Deflate after their BCL codecs pass qualification.
/// Add RFC 1950 ZLib separately if needed; do not guess its wrapper from a Deflate payload.
/// Preserve existing enum/control values and the None default; allocate new wire controls only with implemented codecs.
/// </remarks>
public enum ValueCompression {
    /// <summary>Stores the complete ordinary Bare value without compression.</summary>
    None = 0,
    /// <summary>Tries a self-contained standard Brotli stream and keeps it only when the complete encoding is smaller.</summary>
    Brotli = 1,
    /// <summary>Tries one independent standard LZ4 block, without a Frame or Pickler wrapper, and keeps it only when the complete encoding is smaller.</summary>
    Lz4Block = 2
}
