namespace Atelia.VersionStore.Internal.Runtime;

internal sealed record VersionStoreRuntimeError(string Name, string Description)
    : AteliaError("VersionStore." + Name, Description);
