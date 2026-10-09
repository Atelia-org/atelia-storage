using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.FrameStore;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStoreProcessProbe;

internal sealed record Request(string Operation, string Root, string Scenario, string Token,
    FrameExpectation[]? Expected = null, string[]? Unfinished = null);

internal sealed record Message(string Kind, int Pid, string Token, Stage? Stage = null,
    Observation? Observation = null, ErrorInfo? Error = null);

internal sealed record FrameExpectation(string Name, string Address, uint Tag, int Length,
    int TailMetaLength, string Sha256, string PayloadHeadHex);

internal sealed record FrameObserved(string Address, uint Tag, int Length, int TailMetaLength,
    bool IsTombstone, string Sha256, string PayloadHeadHex);

internal sealed record ReadObserved(string Address, FrameObserved? Frame, string? ResultError, ErrorInfo? Exception);
internal sealed record RecoveryObserved(uint FileId, string Action, long OriginalLength,
    long FinalLength, long? AffectedFrameOffset, ulong? FrameTicket);
internal sealed record AuditObserved(uint FileId, string HeaderPayloadHex, long UserFrameCount);
internal sealed record FileLength(string RelativePath, long Length);
internal sealed record FileFingerprint(string RelativePath, long Length, string Sha256);
internal sealed record Manifest(string[] Directories, FileFingerprint[] Files);
internal sealed record BuilderProgress(int Index, long InitialWriterLength, long AdvancedBytes,
    long WriterLengthBeforeConfirm, long WriterLengthAfterConfirm);

internal sealed record Stage(string ReturnReached, bool ConfirmationReturned, string StoreId,
    int ActiveBuilders, FrameExpectation[] Required, FrameExpectation[] Optional, string[] Unfinished,
    FileLength[] BeforeBuilderWrite, FileLength[] AfterBuilderWrite, FrameObserved[] BeforeArchive,
    BuilderProgress[]? BuilderProgress = null);

internal sealed record Observation(string Outcome, string? StoreId = null, bool? IsReadOnly = null,
    RecoveryObserved[]? RecoveryReports = null, FrameObserved[]? Frames = null,
    ReadObserved[]? Reads = null, AuditObserved[]? Audits = null, long? InventoryCount = null,
    long? AuditCount = null, ErrorInfo? Error = null);

internal sealed record ErrorInfo(string Type, string Message, int HResult, int? NativeCode) {
    internal static ErrorInfo From(Exception error) {
        int? native = null;
        for (Exception? nested = error; nested is not null; nested = nested.InnerException) {
            if (nested is Win32Exception win32) { native = win32.NativeErrorCode; break; }
        }
        return new(error.GetType().FullName!, error.Message, error.HResult, native);
    }
}

internal static class Protocol {
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };

    internal static string Encode(FrameAddress address) {
        byte[] bytes = new byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(bytes)) { throw new InvalidOperationException("Address codec rejected an issued address."); }
        return Convert.ToHexString(bytes);
    }

    internal static FrameAddress Decode(string encoded) {
        if (!FrameAddress.TryRead(Convert.FromHexString(encoded), out var address)) {
            throw new InvalidDataException("Invalid saved 12B address.");
        }
        return address;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static FrameExpectation Expect(string name, FrameAddress address, uint tag, byte[] payload) =>
        new(name, Encode(address), tag, payload.Length, 0, Hash(payload),
            Convert.ToHexString(payload.AsSpan(0, Math.Min(64, payload.Length))));

    internal static FrameObserved Read(Store store, FrameAddress address) {
        using var frame = store.ReadFrame(address).Unwrap();
        return Describe(frame);
    }

    private static FrameObserved Describe(FrameRead frame) => new(Encode(frame.Address), frame.Tag,
        frame.PayloadAndMeta.Length, frame.TailMetaLength, frame.IsTombstone, Hash(frame.PayloadAndMeta),
        Convert.ToHexString(frame.PayloadAndMeta[..Math.Min(64, frame.PayloadAndMeta.Length)]));

    internal static ReadObserved TryRead(Store store, string encoded) {
        try {
            var result = store.ReadFrame(Decode(encoded));
            if (result.IsFailure) { return new(encoded, null, result.Error!.ToString(), null); }
            using var frame = result.Unwrap();
            return new(encoded, Describe(frame), null, null);
        }
        catch (Exception error) when (error is IOException or ArgumentException) {
            return new(encoded, null, null, ErrorInfo.From(error));
        }
    }

    internal static Observation Inspect(Store store, Request request) {
        var frames = new List<FrameObserved>();
        long inventoryCount = store.Inventory(info => frames.Add(Read(store, info.Address))).Unwrap();
        var audits = new List<AuditObserved>();
        long auditCount = store.Audit(audit => audits.Add(new(audit.FileId,
            Convert.ToHexString(audit.HeaderPayload.Span), audit.UserFrameCount))).Unwrap();
        var reads = (request.Expected ?? []).Select(item => item.Address)
            .Concat(request.Unfinished ?? []).Distinct(StringComparer.Ordinal)
            .Select(address => TryRead(store, address)).ToArray();
        var reports = store.RecoveryReports.OrderBy(item => item.Key).Select(item => new RecoveryObserved(
            item.Key, item.Value.Action.ToString(), item.Value.OriginalLength, item.Value.FinalLength,
            item.Value.AffectedFrameOffset, item.Value.FrameTicket?.Packed)).ToArray();
        return new("opened", Convert.ToHexString(store.StoreId), store.IsReadOnly, reports,
            frames.OrderBy(frame => frame.Address, StringComparer.Ordinal).ToArray(), reads,
            audits.OrderBy(audit => audit.FileId).ToArray(), inventoryCount, auditCount);
    }

    internal static FileLength[] Lengths(string root) => Directory.GetFiles(root, "*.rbf", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => new FileLength(Relative(root, path), new FileInfo(path).Length)).ToArray();

    // Used only with no held owner. File hashing never writes, repairs, imports, or renames managed files.
    internal static Manifest Fingerprint(string root) => new(
        Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(path => Relative(root, path))
            .Order(StringComparer.Ordinal).ToArray(),
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => {
            using var stream = File.OpenRead(path);
            return new FileFingerprint(Relative(root, path), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }).ToArray());

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static bool Same(Manifest left, Manifest right) =>
        left.Directories.SequenceEqual(right.Directories) && left.Files.SequenceEqual(right.Files);

    internal static bool LockConflict(Exception error) => OperatingSystem.IsWindows()
        ? error is IOException && (ErrorInfo.From(error).NativeCode ?? (error.HResult & 0xffff)) is 32 or 33
        : OperatingSystem.IsLinux() && error is IOException && ErrorInfo.From(error).NativeCode == 11;

    internal static void Write(Message message) {
        Console.WriteLine(JsonSerializer.Serialize(message, Json));
        Console.Out.Flush();
    }
}
