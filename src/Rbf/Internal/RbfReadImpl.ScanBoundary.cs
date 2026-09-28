using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf.ReadCache;

namespace Atelia.Rbf.Internal;

internal static partial class RbfReadImpl {
    internal static AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(RandomAccessReader reader, SizedPtr ticket, long end, long fileLength) {
        if (ticket.Offset < RbfLayout.FirstFrameOffset || ticket.Length < RbfLayout.MinFrameLength || end > fileLength) {
            return new RbfArgumentError("Anchor ticket must identify a complete frame within the file.");
        }
        var result = ReadPooledFrame(reader, ticket);
        if (result.IsFailure) { return result.Error!; }
        using var frame = result.Value;
        Span<byte> fence = stackalloc byte[RbfLayout.FenceSize];
        if (reader.Read(fence, end - RbfLayout.FenceSize) != fence.Length || !fence.SequenceEqual(RbfLayout.Fence)) {
            return new RbfFramingError("Anchor tail Fence is missing or corrupted.");
        }
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, frame.Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)frame.TailMetaLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], frame.IsTombstone ? 1u : 0u);
        uint raw = RollingCrc.CrcForward(RollingCrc.DefaultInitValue, header);
        uint crc = RollingCrc.CrcForward(raw, frame.PayloadAndMeta) ^ RollingCrc.DefaultFinalXor;
        return new RbfScanBoundary(end, ticket, crc);
    }
}
