using Atelia.Rbf;

namespace Atelia.HeadStore.Internal.Tags;

/// <summary>一次正常 EOF 元信息扫描的常数状态；只保留唯一请求名的定位与自有元信息。</summary>
internal sealed class TagNameLookup {
    private readonly RbfFrameInfo _target;
    private readonly TagMetadata _metadata;

    internal bool UnknownName { get; }
    internal int MatchCount { get; }

    internal TagNameLookup(bool unknownName, int matchCount, RbfFrameInfo target, TagMetadata metadata) {
        UnknownName = unknownName;
        MatchCount = matchCount;
        _target = target;
        _metadata = metadata;
    }

    /// <summary>只裁决请求名；唯一命中的元信息不授予 成员表或 HeadEntryId 资格。</summary>
    internal AteliaResult<TagMetadata> GetUniqueMetadata() {
        if (MatchCount == 2) { return new TagDuplicateNameError("The requested tag name has multiple trusted declarations."); }
        if (MatchCount == 0) {
            return UnknownName
                ? new TagLookupUncertainError("An unreadable record name prevents a healthy missing-tag conclusion.")
                : new TagNotFoundError("The requested tag name is absent from the checked bucket.");
        }
        return _metadata;
    }

    internal RbfFrameInfo UniqueTarget {
        get {
            if (MatchCount != 1) { throw new InvalidOperationException("The lookup does not contain a unique tag target."); }
            return _target;
        }
    }
}

internal sealed record TagNotFoundError(string Detail) : AteliaError("HeadStore.TagNotFound", Detail);
internal sealed record TagLookupUncertainError(string Detail) : AteliaError("HeadStore.TagLookupUncertain", Detail);
internal sealed record TagDuplicateNameError(string Detail) : AteliaError("HeadStore.DuplicateTagName", Detail);
