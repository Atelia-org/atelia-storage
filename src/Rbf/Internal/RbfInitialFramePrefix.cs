using System.Buffers.Binary;
using Atelia.Data.Binary;
using Atelia.Data.Hashing;

namespace Atelia.Rbf.Internal;

/// <summary>有界 RBF3 初始单帧 codeword 的纯前缀谓词。</summary>
internal static class RbfInitialFramePrefix {
    private const int MaxPayloadLength = 232;
    private const int MaxBodyLength = MaxPayloadLength + RbfFrameWriteCore.PlaintextTailSize;
    private const int MaxImageLength = RbfLayout.HeaderOnlyLength + FrameLayout.HeadLenSize
        + MaxBodyLength + RbfLayout.TailKeySize + RbfLayout.FenceSize;

    internal static bool IsMatch(ReadOnlySpan<byte> prefix, uint tag, ReadOnlySpan<byte> payload) {
        if (payload.Length > MaxPayloadLength) { throw new ArgumentOutOfRangeException(nameof(payload)); }
        FrameLayout layout = FrameLayout.TryCreate(RbfProfile.Rbf3, payload.Length, 0).Unwrap();
        int totalLength = RbfLayout.HeaderOnlyLength + layout.FrameLength + RbfLayout.FenceSize;
        if (prefix.Length > totalLength) { return false; }

        Span<byte> image = stackalloc byte[MaxImageLength];
        image = image[..totalLength];
        RbfLayout.GetFence(RbfProfile.Rbf3).CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image[RbfLayout.FirstFrameOffset..], layout.WireFrameLength);
        const int bodyStart = RbfLayout.FirstFrameOffset + FrameLayout.HeadLenSize;
        int fixedLength = Math.Min(bodyStart, prefix.Length);
        if (!prefix[..fixedLength].SequenceEqual(image[..fixedLength])) { return false; }

        if (prefix.Length < bodyStart + sizeof(uint)) {
            // At most three observed body bytes constrain at most three Key bytes, leaving >=256 choices.
            // The <=63 complete body words and Key != Fence exclude at most 64 choices in total.
            return true;
        }

        Span<byte> body = image.Slice(bodyStart, layout.EncodedBodyLength);
        payload.CopyTo(body);
        body.Slice(payload.Length, layout.PaddingLength).Clear();
        int coverageLength = layout.PayloadCrcCoverageLength;
        uint payloadCrc = RollingCrc.CrcForward(body[..coverageLength]);
        RbfFrameWriteCore.WritePlaintextTail(body[coverageLength..], in layout, tag, false, payloadCrc);

        uint key = BinaryPrimitives.ReadUInt32LittleEndian(prefix[bodyStart..])
            ^ BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint fence = RbfLayout.GetFenceWord(RbfProfile.Rbf3);
        if (key == fence) { return false; }
        XorEscape.InPlace(body, key);
        // Check the complete expected body: even an unobserved aligned Fence makes this Key illegal.
        for (int offset = 0; offset < body.Length; offset += sizeof(uint)) {
            if (BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]) == fence) { return false; }
        }

        int keyStart = bodyStart + body.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(image[keyStart..], key);
        RbfLayout.GetFence(RbfProfile.Rbf3).CopyTo(image[(keyStart + RbfLayout.TailKeySize)..]);
        return prefix.SequenceEqual(image[..prefix.Length]);
    }
}
