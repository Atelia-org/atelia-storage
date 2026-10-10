using System.Buffers;
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.Data;
using Atelia.FrameStore;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore.Tests.RootMap;

internal sealed class RootMapFixture : IDisposable {
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "VersionStore-rootmap-" + Guid.NewGuid().ToString("N"));
    internal string Root { get; }

    internal RootMapFixture() {
        Directory.CreateDirectory(_sandbox);
        Root = Path.Combine(_sandbox, "data");
    }

    internal static FrameAddress MinimumAddress() {
        Assert.True(FrameAddress.TryRead(Convert.FromHexString("010000000700000400000000"), out FrameAddress address));
        return address;
    }

    internal static FrameAddress MaximumAddress() {
        Assert.True(FrameAddress.TryRead(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFF"), out FrameAddress address));
        return address;
    }

    internal static FrameAddress NumericAddress(uint fileId, long offset) {
        byte[] fixedWire = new byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedWire, fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedWire.AsSpan(4), SizedPtr.Create(offset, 28).Packed);
        Assert.True(FrameAddress.TryRead(fixedWire, out FrameAddress address));
        return address;
    }

    internal static byte[] AddressBytes(FrameAddress address) {
        var output = new ArrayBufferWriter<byte>();
        address.WriteVarInt(new BareValueWriter(output));
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] BareKeyBytes(string key) {
        var output = new ArrayBufferWriter<byte>();
        new BareValueWriter(output).WriteString(key);
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] OneEntry(string key, FrameAddress address) {
        return [0x01, .. BareKeyBytes(key), .. AddressBytes(address)];
    }

    internal (FrameAddress First, FrameAddress Second) CreateClosed() {
        using var store = Store.Create(Root);
        FrameAddress first = store.Append(17, [1, 2, 3], [4]).Unwrap();
        using var builder = store.BeginAppend(6, 0, out FrameAddress early);
        var writer = builder.PayloadAndMeta;
        "second"u8.CopyTo(writer.GetSpan(6));
        writer.Advance(6);
        FrameAddress second = builder.EndAppend(23).Unwrap();
        Assert.Equal(early, second);
        store.ConfirmDurable();
        return (first, second);
    }

    public void Dispose() {
        string resolved = Path.GetFullPath(_sandbox);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("VersionStore-rootmap-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Fixture cleanup escaped its generated TEMP directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
