using System.Text.Json.Serialization;
namespace Atelia.EventJournal.Toolkit;
public sealed class UpgradeReport {
    public int SchemaVersion => 1;
    public string Operation { get; internal set; } = "CheckOnly";
    public string SourceProfile { get; internal set; } = LegacyUpgrade.Profile;
    public int TargetLayoutVersion => 2;
    public string Status { get; internal set; } = "Rejected";
    public bool SourceScanCompleted { get; internal set; }
    public AuditCounts Counts { get; internal set; } = new();
    public List<AuditFinding> Findings { get; } = new();
    public string? OutputRoot { get; internal set; }
    [JsonIgnore] public int ExitCode => Status is "Eligible" or "Created" ? 0 : Status == "Incomplete" ? 3 : 2;
}
public sealed record UpgradeValidation(string SourceFullScan, string TargetFullAudit, string DailyReadCheck, string SourceUnchanged);
public sealed record UpgradeManifest(int SchemaVersion, string Kind, bool Completed, string SourceProfile,
    string ProfileBaselineRevision, int TargetLayoutVersion, string SourceRoot, string[] SourceDirectories,
    SourceFact[] SourceFiles, SourceFact[] Outputs, string[] TargetDirectories, bool FactsCopiedByteExactly,
    UpgradeValidation Validation, long WarningCount);
