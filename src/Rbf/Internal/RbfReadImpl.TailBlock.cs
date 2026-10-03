using System.Buffers.Binary;

namespace Atelia.Rbf.Internal;

internal static partial class RbfReadImpl {
    // The shared wire codec checks the original plaintext TrailerCRC and only then
    // normalizes length units to bytes. The raw Key is outside that codeword.
    private static AteliaResult<TrailerCodewordData> ParseTailBlock(RbfProfile profile, ReadOnlySpan<byte> tailBlock, out uint key) {
        key = 0;
        int size = TrailerCodewordHelper.Size + RbfLayout.GetTailKeySize(profile);
        if (tailBlock.Length < size) { return new RbfFramingError("Frame tail block is truncated."); }
        if (profile == RbfProfile.Rbf1) { return RbfWireCodec.ParseTrailer(profile, tailBlock[..TrailerCodewordHelper.Size]); }
        key = BinaryPrimitives.ReadUInt32LittleEndian(tailBlock.Slice(TrailerCodewordHelper.Size, sizeof(uint)));
        return RbfWireCodec.ParseEncodedTrailer(tailBlock[..TrailerCodewordHelper.Size], key);
    }
}
