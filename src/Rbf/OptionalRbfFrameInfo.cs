namespace Atelia.Rbf;

/// <summary>表示直接物理后继帧查询的可空结果。</summary>
public readonly record struct OptionalRbfFrameInfo {
    private readonly RbfFrameInfo _value;

    public OptionalRbfFrameInfo(RbfFrameInfo value) {
        _value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public RbfFrameInfo Value => HasValue
        ? _value
        : throw new InvalidOperationException("OptionalRbfFrameInfo has no value.");

    public static OptionalRbfFrameInfo None => default;
}
