using Atelia.Data;
using Atelia.FrameStore.Internal.Format;
using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Admission;

/// <summary>Checks the first physical frame of an already admitted, idle RBF handle.</summary>
/// <remarks>
/// This does not open paths, recover files, acquire an owner lock, or authorize cleanup.
/// The caller supplies identity from the checked gate and the canonical/planned FileId.
/// Writable open must independently enforce the pre-recovery initialization boundary.
/// </remarks>
internal static class FrameHeaderReader {
    internal static AteliaResult<SizedPtr> Check(IRbfFile file, StoreIdentity expectedIdentity, uint expectedFileId) {
        ArgumentNullException.ThrowIfNull(file);
        if (expectedIdentity == default) { throw new ArgumentException("An admitted store identity is required.", nameof(expectedIdentity)); }
        if (expectedFileId == 0) { throw new ArgumentOutOfRangeException(nameof(expectedFileId)); }
        if (file.Format != RbfFormat.Rbf3) { return new FrameHeaderError("Data files must use RBF3."); }

        var enumerator = file.ScanForward(showTombstone: true).GetEnumerator();
        if (!enumerator.MoveNext()) {
            return enumerator.TerminationError ?? new FrameHeaderError("The first physical header frame is missing.");
        }

        var info = enumerator.Current;
        if (info.Ticket.Offset != RbfScanBoundary.Empty.EndExclusive
            || info.Ticket.Length != FileHeaderCodec.HeaderFrameLength
            || info.PayloadLength != FileHeaderCodec.PayloadSize
            || info.TailMetaLength != 0 || info.Tag != 0 || info.IsTombstone) {
            return new FrameHeaderError("The first physical frame has an invalid header shape.");
        }

        // The shape check bounds this stack buffer before any content read.
        Span<byte> buffer = stackalloc byte[FileHeaderCodec.HeaderFrameLength];
        var read = file.ReadFrame(info.Ticket, buffer);
        if (read.IsFailure) { return read.Error; }
        if (!FileHeaderCodec.TryValidate(read.Value.PayloadAndMeta, expectedIdentity, expectedFileId)) {
            return new FrameHeaderError("The header version, store identity, or FileId does not match the admitted context.");
        }
        if (file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != FileHeaderCodec.InitializationBoundary) {
            return new FrameHeaderError("The checked header does not end at the initialization boundary.");
        }
        return info.Ticket;
    }
}

internal sealed record FrameHeaderError(string Detail) : AteliaError("FrameStore.InvalidHeader", Detail);
