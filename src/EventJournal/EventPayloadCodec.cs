using System.Buffers;
using System.IO.Compression;
using Atelia.Rbf;

namespace Atelia.EventJournal;

public enum EventPayloadCodecId : ushort {
    Identity = 0,
    ZstdFrame = 1,
    Brotli = 2,
    Zlib = 3
}

public enum EventPayloadCodecFallback {
    StoreIdentity = 0,
    FailWrite = 1
}

public readonly record struct EventPayloadCodecPolicy(
    EventPayloadCodecId PreferredCodec,
    int MinimumPayloadLength = 2048,
    int MinimumSavingsBytes = 256,
    double MinimumSavingsRatio = 0.05,
    EventPayloadCodecFallback Fallback = EventPayloadCodecFallback.StoreIdentity
) {
    public static EventPayloadCodecPolicy Identity => new(EventPayloadCodecId.Identity);

    public static EventPayloadCodecPolicy Brotli => new(EventPayloadCodecId.Brotli);

    public static EventPayloadCodecPolicy Zlib => new(EventPayloadCodecId.Zlib);
}

public readonly record struct EventPayloadWriteOptions(
    EventPayloadCodecPolicy? PayloadCodecPolicy = null
);

internal readonly ref struct EventStoredPayloadLease {
    private readonly byte[]? _buffer;
    private readonly bool _returnBufferToPool;

    public EventStoredPayloadLease(EventPayloadCodecId codecId, ReadOnlySpan<byte> payload, byte[]? buffer, bool returnBufferToPool = true) {
        CodecId = codecId;
        Payload = payload;
        _buffer = buffer;
        _returnBufferToPool = returnBufferToPool;
    }

    public EventPayloadCodecId CodecId { get; }
    public ReadOnlySpan<byte> Payload { get; }

    public void Dispose() {
        if (_buffer is not null && _returnBufferToPool) { ArrayPool<byte>.Shared.Return(_buffer); }
    }
}

internal static class EventPayloadCodec {
    private const int BrotliQuality = 3;
    private const int BrotliWindow = 22;

    public static AteliaError? ValidatePolicy(in EventPayloadCodecPolicy policy) {
        if (!IsKnownPolicyCodec(policy.PreferredCodec)) {
            return new EventJournalError(
                "PayloadCodecPolicyInvalid",
                $"Unsupported preferred payload codec id {(ushort)policy.PreferredCodec}.",
                "Use identity, brotli, or zlib for EventJournal payload compression."
            );
        }

        if (policy.MinimumPayloadLength < 0) {
            return new EventJournalError(
                "PayloadCodecPolicyInvalid",
                $"MinimumPayloadLength must be non-negative, got {policy.MinimumPayloadLength}.",
                "Use zero to allow all payload sizes, or a positive threshold."
            );
        }

        if (policy.MinimumSavingsBytes < 0) {
            return new EventJournalError(
                "PayloadCodecPolicyInvalid",
                $"MinimumSavingsBytes must be non-negative, got {policy.MinimumSavingsBytes}.",
                "Use zero to accept any byte savings, or a positive threshold."
            );
        }

        if (double.IsNaN(policy.MinimumSavingsRatio) || policy.MinimumSavingsRatio < 0 || policy.MinimumSavingsRatio > 1) {
            return new EventJournalError(
                "PayloadCodecPolicyInvalid",
                $"MinimumSavingsRatio must be between 0 and 1, got {policy.MinimumSavingsRatio}.",
                "Use a ratio such as 0.05 for 5% minimum savings."
            );
        }

        if (!Enum.IsDefined(policy.Fallback)) {
            return new EventJournalError(
                "PayloadCodecPolicyInvalid",
                $"Unsupported payload codec fallback value {(int)policy.Fallback}.",
                "Use StoreIdentity or FailWrite."
            );
        }

        return null;
    }

    public static EventStoredPayloadLease EncodeForStore(ReadOnlySpan<byte> logicalPayload, in EventPayloadCodecPolicy policy, out AteliaError? error) {
        error = ValidatePolicy(policy);
        if (error is not null) { return default; }

        if (policy.PreferredCodec == EventPayloadCodecId.Identity || logicalPayload.Length < policy.MinimumPayloadLength) { return new EventStoredPayloadLease(EventPayloadCodecId.Identity, logicalPayload, buffer: null); }

        return policy.PreferredCodec switch {
            EventPayloadCodecId.Brotli => TryEncodeBrotli(logicalPayload, policy, out error),
            EventPayloadCodecId.Zlib => TryEncodeZlib(logicalPayload, policy, out error),
            _ => UnsupportedEncodeFallback(logicalPayload, policy, out error)
        };
    }

    public static AteliaResult<byte[]> DecodeToArray(EventPayloadCodecId codecId, ReadOnlySpan<byte> storedPayload, uint logicalLength) {
        if (logicalLength > int.MaxValue) {
            return new EventJournalError(
                "PayloadLogicalLengthExceeded",
                $"EventFrame logical payload length {logicalLength} exceeds the current ReadEvent span limit.",
                "Use a future streaming payload API for larger logical payloads."
            );
        }

        return codecId switch {
            EventPayloadCodecId.Brotli => DecodeBrotli(storedPayload, (int)logicalLength),
            EventPayloadCodecId.Zlib => DecodeZlib(storedPayload, (int)logicalLength),
            EventPayloadCodecId.Identity => IdentityDecodeShouldNotAllocate(),
            _ => new EventJournalError(
                "PayloadCodecUnsupported",
                $"Unsupported EventFrame payload codec id {(ushort)codecId}.",
                "Open this journal with an EventJournal implementation that supports the stored payload codec."
            )
        };
    }

    private static bool IsKnownPolicyCodec(EventPayloadCodecId codecId) =>
        codecId is EventPayloadCodecId.Identity or EventPayloadCodecId.Brotli or EventPayloadCodecId.Zlib;

    private static EventStoredPayloadLease TryEncodeBrotli(ReadOnlySpan<byte> logicalPayload, in EventPayloadCodecPolicy policy, out AteliaError? error) {
        error = null;
        int maxCompressedLength;
        try {
            maxCompressedLength = BrotliEncoder.GetMaxCompressedLength(logicalPayload.Length);
        }
        catch (ArgumentOutOfRangeException ex) {
            return CompressionFailedFallback(logicalPayload, policy,
                new EventJournalError(
                    "PayloadCodecEncodeFailed",
                    $"Brotli max compressed length calculation failed: {ex.Message}",
                    "Store the payload as identity, or reduce the logical payload size.",
                    Cause: new EventJournalError("CompressionException", ex.GetType().FullName ?? ex.GetType().Name, ex.Message)
                ), out error
            );
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxCompressedLength);
        bool compressed = BrotliEncoder.TryCompress(logicalPayload, buffer.AsSpan(0, maxCompressedLength), out int bytesWritten, BrotliQuality, BrotliWindow);
        if (!compressed) {
            ArrayPool<byte>.Shared.Return(buffer);
            return CompressionFailedFallback(logicalPayload, policy,
                new EventJournalError(
                    "PayloadCodecEncodeFailed",
                    "Brotli compression failed.",
                    "Store the payload as identity, or inspect codec availability."
                ), out error
            );
        }

        if (!HasRequiredSavings(logicalPayload.Length, bytesWritten, policy)) {
            ArrayPool<byte>.Shared.Return(buffer);
            return new EventStoredPayloadLease(EventPayloadCodecId.Identity, logicalPayload, buffer: null);
        }

        return new EventStoredPayloadLease(EventPayloadCodecId.Brotli, buffer.AsSpan(0, bytesWritten), buffer);
    }

    private static EventStoredPayloadLease TryEncodeZlib(ReadOnlySpan<byte> logicalPayload, in EventPayloadCodecPolicy policy, out AteliaError? error) {
        error = null;
        try {
            using var stream = new MemoryStream(logicalPayload.Length);
            using (var compressor = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true)) {
                compressor.Write(logicalPayload);
            }

            byte[] compressedPayload = stream.ToArray();
            if (!HasRequiredSavings(logicalPayload.Length, compressedPayload.Length, policy)) {
                return new EventStoredPayloadLease(EventPayloadCodecId.Identity, logicalPayload, buffer: null);
            }

            return new EventStoredPayloadLease(EventPayloadCodecId.Zlib, compressedPayload, compressedPayload, returnBufferToPool: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException) {
            return CompressionFailedFallback(logicalPayload, policy,
                new EventJournalError(
                    "PayloadCodecEncodeFailed",
                    $"Zlib compression failed: {ex.Message}",
                    "Store the payload as identity, or inspect codec availability.",
                    Cause: new EventJournalError("CompressionException", ex.GetType().FullName ?? ex.GetType().Name, ex.Message)
                ), out error
            );
        }
    }

    private static EventStoredPayloadLease UnsupportedEncodeFallback(ReadOnlySpan<byte> logicalPayload, in EventPayloadCodecPolicy policy, out AteliaError? error) =>
        CompressionFailedFallback(logicalPayload, policy,
            new EventJournalError(
                "PayloadCodecUnsupported",
                $"Payload codec id {(ushort)policy.PreferredCodec} is reserved but not implemented.",
                "Use identity, brotli, or zlib for current writes."
            ), out error
        );

    private static EventStoredPayloadLease CompressionFailedFallback(ReadOnlySpan<byte> logicalPayload, in EventPayloadCodecPolicy policy, AteliaError failure, out AteliaError? error) {
        if (policy.Fallback == EventPayloadCodecFallback.StoreIdentity) {
            error = null;
            return new EventStoredPayloadLease(EventPayloadCodecId.Identity, logicalPayload, buffer: null);
        }

        error = failure;
        return default;
    }

    private static bool HasRequiredSavings(int logicalLength, int compressedLength, in EventPayloadCodecPolicy policy) {
        int savings = logicalLength - compressedLength;
        if (savings < policy.MinimumSavingsBytes) { return false; }
        if (logicalLength == 0) { return false; }

        double ratio = (double)savings / logicalLength;
        return ratio >= policy.MinimumSavingsRatio;
    }

    private static AteliaResult<byte[]> DecodeBrotli(ReadOnlySpan<byte> storedPayload, int logicalLength) {
        byte[] decoded = ArrayPool<byte>.Shared.Rent(logicalLength);
        if (!BrotliDecoder.TryDecompress(storedPayload, decoded.AsSpan(0, logicalLength), out int bytesWritten)) {
            ArrayPool<byte>.Shared.Return(decoded);
            return new EventJournalError(
                "PayloadCodecDecodeFailed",
                "Brotli payload decode failed.",
                "Treat this EventFrame payload as corrupted, or use recovery tooling to inspect stored bytes."
            );
        }

        if (bytesWritten != logicalLength) {
            ArrayPool<byte>.Shared.Return(decoded);
            return new EventJournalError(
                "PayloadLengthMismatch",
                $"Decoded payload length {bytesWritten} does not match EventFrame header logical payload length {logicalLength}.",
                "Treat this EventFrame as corrupted."
            );
        }

        return decoded;
    }

    private static AteliaResult<byte[]> DecodeZlib(ReadOnlySpan<byte> storedPayload, int logicalLength) {
        byte[] decoded = ArrayPool<byte>.Shared.Rent(logicalLength);
        try {
            using var source = new MemoryStream(storedPayload.ToArray(), writable: false);
            using var decompressor = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: false);

            int totalRead = 0;
            while (totalRead < logicalLength) {
                int bytesRead = decompressor.Read(decoded.AsSpan(totalRead, logicalLength - totalRead));
                if (bytesRead == 0) { break; }
                totalRead += bytesRead;
            }

            if (totalRead != logicalLength || decompressor.ReadByte() != -1) {
                ArrayPool<byte>.Shared.Return(decoded);
                return new EventJournalError(
                    "PayloadLengthMismatch",
                    $"Decoded payload length does not match EventFrame header logical payload length {logicalLength}.",
                    "Treat this EventFrame as corrupted."
                );
            }

            return decoded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) {
            ArrayPool<byte>.Shared.Return(decoded);
            return new EventJournalError(
                "PayloadCodecDecodeFailed",
                $"Zlib payload decode failed: {ex.Message}",
                "Treat this EventFrame payload as corrupted, or use recovery tooling to inspect stored bytes.",
                Cause: new EventJournalError("CompressionException", ex.GetType().FullName ?? ex.GetType().Name, ex.Message)
            );
        }
    }

    private static AteliaResult<byte[]> IdentityDecodeShouldNotAllocate() => new EventJournalError(
        "PayloadCodecDecodeFailed",
        "Identity payload decode was routed through the compressed decode path.",
        "Report this EventJournal internal error."
    );
}
