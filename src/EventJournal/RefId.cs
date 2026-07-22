using System.Globalization;

namespace Atelia.EventJournal;

public readonly record struct RefId(ulong Packed) {
    public bool IsDefault => Packed == 0;

    public string ToHexString() => Packed.ToString("x16", CultureInfo.InvariantCulture);

    public override string ToString() => ToHexString();

    public static AteliaResult<RefId> ParseHex(string value) {
        if (value.Length != 16 || !IsLowerHex(value)) {
            return new EventJournalError(
                "RefIdInvalid",
                $"RefId must be 16 lowercase hex characters, got '{value}'.",
                "Use the full RefId hex string produced by EventJournal."
            );
        }

        return new RefId(ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static bool IsLowerHex(string value) {
        foreach (char c in value) {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { return false; }
        }

        return true;
    }
}
