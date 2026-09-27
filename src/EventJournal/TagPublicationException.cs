namespace Atelia.EventJournal;

/// <summary>Evidence available when tag publication fails.</summary>
public enum TagPublicationOutcome {
    /// <summary>The tag append was never attempted.</summary>
    NotAttempted,
    /// <summary>The append was attempted, but durable publication was not confirmed.</summary>
    Unknown,
    /// <summary>The publication log's durable flush returned successfully.</summary>
    Confirmed
}

/// <summary>A tag publication failure that faults the owning journal. Dispose and reopen strictly.</summary>
public sealed class TagPublicationException : IOException {
    internal TagPublicationException(string tagName, TagPublicationOutcome outcome, Exception innerException)
        : base($"Tag '{tagName}' publication failed ({outcome}). Dispose this journal and reopen without recovery to inspect the binding.", innerException) {
        TagName = tagName;
        Outcome = outcome;
    }

    public string TagName { get; }
    public TagPublicationOutcome Outcome { get; }
}
