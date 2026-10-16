using System.Globalization;
using System.Security.Cryptography;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.Public;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.RecoveryReportsPublic;

/// <summary>真实公开工厂与离线尾部镜像；不作为进程终止、断电或发布恢复证据。</summary>
internal sealed class RecoveryReportsFixture : IDisposable {
    private readonly PublicHeadStoreFixture _storage = new();
    internal string Root => _storage.Root;
    internal string DataRoot => _storage.DataRoot;
    internal DataStore Data => _storage.Data;
    internal static HeadHistoryBudget Unlimited => new(long.MaxValue, long.MaxValue);
    internal Store Create() => Store.Create(Root, Data, 256);
    internal Store Open(bool readOnly = false) => readOnly ? Store.OpenReadOnly(Root, Data) : Store.Open(Root, Data);
    internal string HeadPath(uint id) => _storage.HeadPath(id);

    internal string OnlyTagPath() => Directory.EnumerateFiles(Path.Combine(Root, "tags"), "*.rbf").Single();
    internal static uint LocalId(string path) => uint.Parse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber);

    internal HeadEntryId SeedHead(bool later = false, uint id = 7) {
        using var store = Create();
        HeadEntryId initial = store.CreateHead(id, [0xff, 1, 2], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        if (!later) { return initial; }
        HeadEntryId current = store.PublishHead(initial.HeadId, [0xfe, 3, 4], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return current;
    }

    internal static SizedPtr LastTicket(string path) {
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
        var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
        Assert.True(reverse.MoveNext());
        return reverse.Current.Ticket;
    }

    internal static TailImage Damage(string path, bool completeTail, int prefixLength = 1) {
        byte[] complete = File.ReadAllBytes(path);
        SizedPtr ticket = LastTicket(path);
        byte[] damaged = completeTail ? complete[..^8] : [.. complete, .. new byte[prefixLength]];
        File.WriteAllBytes(path, damaged);
        return new TailImage(path, complete, damaged, ticket, completeTail);
    }

    internal Dictionary<string, byte[]?> CaptureFiles() => _storage.CaptureFiles();

    internal void AssertUnchangedWithHashes(Dictionary<string, byte[]?> before) {
        var after = CaptureFiles();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) {
            Assert.Equal(bytes, after[path]);
            if (bytes is not null) {
                Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(after[path]!));
            }
        }
    }

    internal static string NameForBucket(uint bucket) => Enumerable.Range(0, 100_000)
        .Select(index => $"recovery-report-{index}").First(name => TagPublicFixture.Bucket(name) == bucket);

    internal static void AssertReleased(string path) {
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => _storage.Dispose();
}

internal sealed record TailImage(string Path, byte[] Complete, byte[] Damaged, SizedPtr LastTicket, bool CompleteTail) {
    internal void AssertReport(HeadStoreRecoveryReport report, HeadStoreRecoveryFileKind kind, uint id) {
        Assert.Equal(kind, report.FileKind);
        Assert.Equal(id, report.LocalId);
        RbfTailRecoveryReport recovery = report.Recovery;
        Assert.Equal(CompleteTail ? RbfTailRecoveryAction.CompletedTail : RbfTailRecoveryAction.Truncated, recovery.Action);
        Assert.Equal((long)Damaged.Length, recovery.OriginalLength);
        Assert.Equal((long)Complete.Length, recovery.FinalLength);
        Assert.Equal(CompleteTail ? LastTicket.Offset : (long)Complete.Length, recovery.AffectedFrameOffset);
        Assert.Equal(CompleteTail ? LastTicket : (SizedPtr?)null, recovery.FrameTicket);
    }

    internal void AssertRestored() => Assert.Equal(Complete, File.ReadAllBytes(Path));
}
