using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Atelia.Data.Binary;

/// <summary>Selects a word XOR key that escapes an aligned marker, and applies that key to bytes.</summary>
/// <remarks>
/// This is framing encoding, not encryption. Words are little-endian and aligned to the logical
/// input's beginning, independently of span boundaries. Borrowed input must remain unchanged
/// throughout selection and the subsequent transform.
/// </remarks>
public static partial class XorEscape {
    private const uint MinimumFence = 0x04000000u;

    /// <summary>Chooses a key for the concatenation of up to three borrowed spans.</summary>
    /// <remarks>
    /// The combined byte length must fit a nonnegative <see cref="int"/> and be divisible by four;
    /// individual spans need not be aligned. Fence must be at least 2^26. The returned key differs
    /// from fence, and XORing each complete little-endian word with it cannot produce fence.
    /// Zero is preferred. At most 63 words use a scalar bitmap; larger inputs use independently
    /// sampled system random candidates with no fixed retry limit. This method does not copy or
    /// modify input. Random-source failures propagate.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Fence or combined length is outside its supported range.</exception>
    /// <exception cref="ArgumentException">The combined length is not divisible by four.</exception>
    public static uint SelectKey(
        uint fence,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second = default,
        ReadOnlySpan<byte> third = default
    ) {
        ValidateFence(fence);
        int byteLength = ValidateByteLength((long)first.Length + second.Length + third.Length);
        return SelectKey(fence, byteLength, new SpanSource(first, second, third));
    }

    internal static void ValidateFence(uint fence) {
        if (fence < MinimumFence) {
            throw new ArgumentOutOfRangeException(nameof(fence), fence, "Fence must be at least 2^26.");
        }
    }

    internal static int ValidateByteLength(long byteLength) {
        if (byteLength < 0 || byteLength > int.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(byteLength), byteLength, "Combined byte length must fit a nonnegative int.");
        }
        if ((byteLength & 3) != 0) {
            throw new ArgumentException("Combined byte length must be divisible by four.", nameof(byteLength));
        }
        return (int)byteLength;
    }

    // Callers validate fence/length before entering. Every pass receives a fresh copy of the
    // initial source value. The optional candidate provider belongs only to internal tests.
    internal static uint SelectKey<TSource>(uint fence, int byteLength, TSource source, Func<uint>? randomCandidate = null)
        where TSource : struct, IXorEscapeSource, allows ref struct {
        if (!Contains(source, byteLength, fence)) { return 0; }
        if (byteLength <= 63 * sizeof(uint)) { return SelectTinyKey(source, byteLength, fence); }

        while (true) {
            uint key = randomCandidate is null ? NextRandomKey() : randomCandidate();
            if (key == 0 || key == fence) { continue; }
            if (!Contains(source, byteLength, fence ^ key)) { return key; }
        }
    }

    private static uint NextRandomKey() {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private ref struct SpanSource : IXorEscapeSource {
        private readonly ReadOnlySpan<byte> _first;
        private readonly ReadOnlySpan<byte> _second;
        private readonly ReadOnlySpan<byte> _third;
        private int _index;

        public SpanSource(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third) {
            _first = first;
            _second = second;
            _third = third;
            _index = 0;
        }

        public bool TryGetNext(out ReadOnlySpan<byte> bytes) {
            while (_index < 3) {
                switch (_index++) {
                    case 0:
                        bytes = _first;
                        break;
                    case 1:
                        bytes = _second;
                        break;
                    default:
                        bytes = _third;
                        break;
                }
                if (!bytes.IsEmpty) { return true; }
            }
            bytes = default;
            return false;
        }
    }
}

// A source is an initial value cursor owned by Data, not a public borrowing protocol.
internal interface IXorEscapeSource {
    bool TryGetNext(out ReadOnlySpan<byte> bytes);
}
