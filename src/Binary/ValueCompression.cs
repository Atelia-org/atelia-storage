namespace Atelia.Binary;

/// <summary>Chooses whether preparation attempts compression; these values are not wire control bytes.</summary>
public enum ValueCompression {
    /// <summary>Stores the complete ordinary Bare value without compression.</summary>
    None = 0,
    /// <summary>Tries a self-contained standard Brotli stream and keeps it only when the complete encoding is smaller.</summary>
    Brotli = 1
}
