using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.Storage;

/// <summary>真实目录夹具；优先使用设计实验盘 W:，目录字节注入仅表示受控测试输入。</summary>
internal sealed class StorageFixture : IDisposable {
    private readonly string _parent = OperatingSystem.IsWindows() && Directory.Exists(@"W:\")
        ? @"W:\" : Path.GetTempPath();
    internal string BasePath { get; }
    internal string Root { get; }
    internal string DataPath { get; }
    internal string GatePath => Path.Combine(Root, HeadStorePaths.FormatFileName);
    internal string LockPath => Path.Combine(Root, HeadStorePaths.LockFileName);
    internal string HeadsPath => Path.Combine(Root, HeadStorePaths.HeadsDirectoryName);
    internal string CreatingPath => Path.Combine(Root, HeadStorePaths.CreatingDirectoryName);
    internal string PendingPath => Path.Combine(CreatingPath, HeadStorePaths.PendingFileName);
    internal DataStore Data { get; private set; }

    internal StorageFixture() {
        BasePath = Path.Combine(_parent, "HeadStore-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BasePath);
        Root = Path.Combine(BasePath, "version");
        DataPath = Path.Combine(BasePath, "data");
        Data = DataStore.Create(DataPath);
    }

    internal byte[] Create(uint upperBound = 17) {
        using var store = HeadStore.Create(Root, Data, upperBound);
        return store.HeadStoreId.ToArray();
    }

    internal void MakeDataReadOnly() {
        Data.Dispose();
        Data = DataStore.OpenReadOnly(DataPath);
    }

    internal string HeadPath(uint localId) => Path.Combine(HeadsPath, HeadStorePaths.GetHeadFileName(localId));

    internal void SeedFormal(uint localId, byte[]? payload = null) {
        FormatGateCodec.TryRead(File.ReadAllBytes(GatePath), out FormatGate gate);
        var headId = HeadId.Create(gate.HeadStoreId, localId);
        Span<byte> header = stackalloc byte[HeadHeaderCodec.NoOriginPayloadSize];
        HeadHeaderCodec.TryWrite(gate.HeadStoreId, headId, null, header);
        Span<byte> meta = stackalloc byte[RecordMetadataCodec.EncodedSize];
        RecordMetadataCodec.TryWrite(123, meta);
        using IRbfFile file = RbfFile.CreateNew(HeadPath(localId), RbfCacheMode.Off);
        file.Append(0, header).Unwrap();
        file.Append(1, payload ?? [1], meta).Unwrap();
        file.DurableFlush();
    }

    public void Dispose() {
        try { Data.Dispose(); }
        finally {
            string resolved = Path.GetFullPath(BasePath);
            string expectedParent = Path.GetFullPath(_parent).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("HeadStore-storage-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Fixture cleanup escaped its generated storage directory.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
}
