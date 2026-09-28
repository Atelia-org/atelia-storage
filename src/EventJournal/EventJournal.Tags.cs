namespace Atelia.EventJournal;

internal enum TagPublicationStage { BeforeTargetFlush, BeforeAppend, AfterAppend, AfterDurableFlush }

public sealed partial class EventJournal {
    public const uint TagBindingFrameTag = 0x5446_4A45; // "EJFT"
    private readonly Dictionary<string, EventAddress> _tags;

    // Deterministic failure witnesses; never exposed to application code.
    internal Action<TagPublicationStage>? TagPublicationProbe { get; set; }

    /// <summary>Creates an immutable name binding to a checked-readable event in this journal.</summary>
    /// <remarks>Validation failures do not append. Publication exceptions fault this instance.
    /// Confirms the target file before publishing; callers own durability of external payload dependencies.
    /// EventAddress is a local coordinate, not a cross-repository identity.</remarks>
    public AteliaResult<bool> CreateTag(string name, EventAddress target) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var nameError = ValidateTagName(name);
        if (nameError is not null) { return nameError; }
        if (_tags.ContainsKey(name)) {
            return new EventJournalError("TagAlreadyExists", $"Tag '{name}' already exists.", "Choose a new name; tags cannot be rebound.");
        }
        var targetResult = ReadEventHeaderChecked(target);
        if (targetResult.IsFailure) { return InvalidTagTarget(name, targetResult.Error!); }
        byte[] payload = TagBindingFrameCodec.Encode(name, target);
        _tags.EnsureCapacity(checked(_tags.Count + 1));

        var outcome = TagPublicationOutcome.NotAttempted;
        try {
            TagPublicationProbe?.Invoke(TagPublicationStage.BeforeTargetFlush);
            _segments.ConfirmDurable(target.SegmentNumber);
            TagPublicationProbe?.Invoke(TagPublicationStage.BeforeAppend);
            outcome = TagPublicationOutcome.Unknown;
            var appendResult = _refOpLog.Append(TagBindingFrameTag, payload);
            // RBF result failures are pre-I/O rejections; exceptions may follow partial writes.
            if (appendResult.IsFailure) { return appendResult.Error!; }
            TagPublicationProbe?.Invoke(TagPublicationStage.AfterAppend);
            _refOpLog.DurableFlush();
            outcome = TagPublicationOutcome.Confirmed;
            TagPublicationProbe?.Invoke(TagPublicationStage.AfterDurableFlush);
            _tags.Add(name, target);
            return true;
        }
        catch (Exception ex) {
            // Latch before allocating the diagnostic exception, including allocation failures.
            LatchFault(ex);
            throw new TagPublicationException(name, outcome, ex);
        }
    }

    /// <summary>Resolves an immutable tag and checks its target's stored frame and CRC.</summary>
    public AteliaResult<EventAddress> ResolveTag(string name) {
        ThrowIfDisposed();
        var nameError = ValidateTagName(name);
        if (nameError is not null) { return nameError; }
        if (!_tags.TryGetValue(name, out EventAddress target)) {
            return new EventJournalError("TagNotFound", $"Tag '{name}' does not exist.");
        }
        var result = ReadEventHeaderChecked(target);
        if (result.IsFailure) { return InvalidTagTarget(name, result.Error!); }
        return target;
    }

    internal static AteliaError? ValidateTagName(string name) =>
        name is null || ValidateBranchName(name) is not null
            ? new EventJournalError("TagNameInvalid", $"Invalid tag name '{name}'.",
                "Use 1..128 ASCII bytes matching [a-z0-9][a-z0-9._-]*, without a trailing '.' or '.lock'.")
            : null;

    private static EventJournalError InvalidTagTarget(string name, AteliaError cause) =>
        new("TagTargetInvalid", $"Tag '{name}' target is not a checked-readable EventFrame.", Cause: cause);
}
