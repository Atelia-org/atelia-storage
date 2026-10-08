namespace Atelia.Binary;

/// <summary>A lightweight plan retaining a string and its selected Bare header.</summary>
public readonly struct StringEncodingPlan {
    private readonly string? _value;
    private readonly uint _header;

    internal StringEncodingPlan(string value, uint header) {
        _value = value;
        _header = header;
    }

    /// <summary>The exact byte count emitted by writing this plan.</summary>
    /// <exception cref="InvalidOperationException">The plan is uninitialized.</exception>
    public long EncodedLength {
        get {
            _ = Value;
            return BareValueEncoding.MeasureVarUInt32(_header) + (long)PayloadByteCount;
        }
    }

    internal string Value {
        get {
            return _value ?? throw new InvalidOperationException("The string encoding plan is uninitialized.");
        }
    }

    internal uint Header {
        get {
            _ = Value;
            return _header;
        }
    }

    private uint PayloadByteCount {
        get {
            return (_header & 1) == 0 ? _header : _header >> 1;
        }
    }
}
