namespace Atelia.RbfSegmentStore;

public enum StorageOpenErrorKind { MaintenanceRequired, FormatUnsupported }

public sealed class StorageOpenException : IOException {
    public StorageOpenErrorKind Kind { get; }
    public string ReasonCode { get; }
    public string StoragePath { get; }
    public long? Offset { get; }
    public ushort? ObservedVersion { get; }

    public StorageOpenException(StorageOpenErrorKind kind, string reasonCode, string storagePath, long? offset = null, ushort? observedVersion = null, Exception? innerException = null)
        : base($"Storage open failed: {kind}/{reasonCode}, path={storagePath}, offset={offset}, version={observedVersion}.", innerException) {
        Kind = kind;
        ReasonCode = reasonCode;
        StoragePath = storagePath;
        Offset = offset;
        ObservedVersion = observedVersion;
    }
}
