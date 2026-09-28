using System.Text.Json;
using System.Text.Json.Serialization;
namespace Atelia.EventJournal.Toolkit;

public sealed record AuditEventAddress(string TicketPacked, uint SegmentNumber, string Hint) {
    internal static AuditEventAddress From(EventAddress a) => new(a.Ticket.Packed.ToString("x16"), a.SegmentNumber, a.Hint.Packed.ToString("x8"));
}
public sealed record AuditFinding(string Severity, string Code, string RelativePath, long? Offset, string? RefId, AuditEventAddress? EventAddress);
public sealed class AuditCounts {
    public long EventSegments { get; set; }
    public long Events { get; set; }
    public long RefObjects { get; set; }
    public long RefMoves { get; set; }
    public long RefOpFrames { get; set; }
    public long TagBindings { get; set; }
    public long StoredFrameBytesChecked { get; set; }
}
public sealed class AuditReport {
    public int SchemaVersion => 1;
    public bool Completed { get; internal set; }
    public string FactsStatus { get; internal set; } = "Healthy";
    public string IndexesStatus { get; internal set; } = "Consistent";
    public AuditCounts Counts { get; } = new();
    public List<AuditFinding> Findings { get; } = new();
    [JsonIgnore] public int ExitCode => FactsStatus == "Incomplete" ? 3 : FactsStatus != "Healthy" || IndexesStatus != "Consistent" ? 2 : Completed ? 0 : 3;
}
public sealed record SourceFact(string Kind, string RelativePath, long Length, string Sha256);
public sealed record SourceIndex(string Kind, string RelativePath, bool Present, long? Length, string? Sha256);
public sealed record CandidateOutput(string Kind, string RelativePath, long Length, string Sha256);
public sealed record CandidateManifest(int SchemaVersion, bool Completed, string SourceRoot, SourceFact[] SourceFacts, SourceIndex[] SourceIndexes, CandidateOutput[] Outputs);
public static class ToolkitJson {
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
