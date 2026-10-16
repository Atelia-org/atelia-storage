namespace Atelia.HeadStore.Internal.Runtime;

internal sealed record HeadStoreRuntimeError(string Name, string Description)
    : AteliaError("HeadStore." + Name, Description);
