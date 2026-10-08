using System.Text;

namespace Atelia.Binary;

public static partial class BareValueEncoding {
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Selects the shortest lossless payload encoding, using UTF-16LE on a tie.</summary>
    public static StringEncodingPlan PrepareString(string value) {
        ArgumentNullException.ThrowIfNull(value);
        long utf16Length = (long)value.Length * 2;
        long utf8Length = 0;
        bool validUtf8 = true;

        for (int i = 0; i < value.Length; i++) {
            char codeUnit = value[i];
            if (codeUnit < 0x80) {
                utf8Length++;
            }
            else if (codeUnit < 0x800) {
                utf8Length += 2;
            }
            else if (!char.IsSurrogate(codeUnit)) {
                utf8Length += 3;
            }
            else if (char.IsHighSurrogate(codeUnit) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) {
                utf8Length += 4;
                i++;
            }
            else {
                validUtf8 = false;
                break;
            }
        }

        bool utf16Fits = utf16Length <= int.MaxValue;
        bool utf8Fits = validUtf8 && utf8Length <= int.MaxValue;
        if (utf8Fits && (!utf16Fits || utf8Length < utf16Length)) {
            return new StringEncodingPlan(value, checked((uint)(utf8Length * 2 + 1)));
        }
        if (utf16Fits) {
            return new StringEncodingPlan(value, checked((uint)utf16Length));
        }
        throw new ArgumentOutOfRangeException(nameof(value), "No lossless string payload fits the supported byte count.");
    }

    /// <summary>Returns the exact byte count of the default string encoding.</summary>
    public static long MeasureString(string value) {
        return PrepareString(value).EncodedLength;
    }

    /// <summary>Returns the length-prefix and payload byte count without allocating a payload.</summary>
    public static long MeasureBytes(int byteLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        return (long)MeasureVarUInt32((uint)byteLength) + byteLength;
    }
}
