using System.Buffers.Binary;
using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

/// <summary>真实 public 工厂夹具；磁盘镜像向量不作为外部 kill 或断电证据。</summary>
internal sealed class PublicStoreFixture : IDisposable {
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "FrameStore-public-" + Guid.NewGuid().ToString("N"));
    internal string Root { get; }
    internal string GatePath => Path.Combine(Root, "framestore.format");
    internal static long InitializationBoundary => RbfScanBoundary.Empty.EndExclusive + RbfFile.MeasureWriteSize(24).Unwrap().AppendLength;

    internal PublicStoreFixture() {
        Directory.CreateDirectory(_sandbox);
        Root = Path.Combine(_sandbox, "store");
    }

    internal byte[] CreateClosed(long? threshold = null) {
        using var store = threshold.HasValue ? Store.Create(Root, threshold.Value) : Store.Create(Root);
        return store.StoreId.ToArray();
    }

    internal string Active(uint id) => Path.Combine(Root, "active", $"{id:x8}.rbf");
    internal string Private(uint id) => Path.Combine(Root, "creating", $"{id:x8}.rbf");
    internal string Archive(uint id) => Path.Combine(Root, "archive", $"{id >> 10:x6}", $"{id:x8}.rbf");

    internal byte[] HeaderImage(byte[] identity, uint id, uint version = 1, uint tag = 0,
        byte[]? tailMeta = null, bool userSuffix = false) {
        string path = Path.Combine(_sandbox, "image-" + Guid.NewGuid().ToString("N") + ".rbf");
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, version);
        identity.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), id);
        using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            file.Append(tag, header, tailMeta ?? []).Unwrap();
            if (userSuffix) { file.Append(123, [7, 8, 9]).Unwrap(); }
            file.DurableFlush();
        }
        byte[] image = File.ReadAllBytes(path);
        File.Delete(path);
        return image;
    }

    internal static uint FileId(FrameAddress address) {
        byte[] bytes = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal static void Write(FrameBuilder builder, ReadOnlySpan<byte> bytes) {
        var writer = builder.PayloadAndMeta;
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    internal static void AssertFrame(Store store, FrameAddress address, uint tag, byte[] bytes, int metaLength = 0) {
        using var frame = store.ReadFrame(address).Unwrap();
        Assert.Equal(address, frame.Address);
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(bytes, frame.PayloadAndMeta.ToArray());
        Assert.Equal(metaLength, frame.TailMetaLength);
        Assert.False(frame.IsTombstone);
    }

    internal Dictionary<string, byte[]?> Snapshot() {
        var entries = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)) {
            entries.Add(Path.GetRelativePath(Root, directory) + Path.DirectorySeparatorChar, null);
        }
        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) {
            entries.Add(Path.GetRelativePath(Root, file), File.ReadAllBytes(file));
        }
        return entries;
    }

    internal void AssertUnchanged(Dictionary<string, byte[]?> before) {
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); }
    }

    public void Dispose() {
        // The recursively removed target is this fixture's generated directory directly under TEMP.
        string resolved = Path.GetFullPath(_sandbox);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("FrameStore-public-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Fixture cleanup escaped its generated TEMP directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
