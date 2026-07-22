namespace Atelia.EventJournal;

public sealed record EventJournalError(
    string ErrorName,
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("EventJournal." + ErrorName, Message, RecoveryHint, Details, Cause);
