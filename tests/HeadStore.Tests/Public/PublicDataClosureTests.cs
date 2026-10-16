using System.Buffers.Binary;
using Atelia.Data;
using Atelia.FrameStore;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Public;

/// <summary>关闭全部 owner 后构造 data 损坏镜像；不作为进程终止或设备故障证据。</summary>
public sealed class PublicDataClosureTests {
    [Fact]
    public void ColdHealthyRootRetainsLatestEntryIdWhileItsDamagedDataFailsApplicationRead() {
        using var fixture = new PublicHeadStoreFixture();
        byte[] oldDataBytes = Enumerable.Repeat((byte)0x11, 128).ToArray();
        byte[] latestDataBytes = Enumerable.Repeat((byte)0x22, 128).ToArray();
        FrameAddress oldAddress;
        FrameAddress latestAddress;
        HeadEntryId initial;
        HeadEntryId latest;
        byte[] latestRoots;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            oldAddress = fixture.Data.Append(17, oldDataBytes).Unwrap();
            byte[] oldRoots = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> {
                ["state"] = oldAddress
            }).Unwrap();
            initial = store.CreateHead(oldRoots, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);

            latestAddress = fixture.Data.Append(23, latestDataBytes).Unwrap();
            latestRoots = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> {
                ["state"] = latestAddress
            }).Unwrap();
            Assert.NotEqual(oldAddress, latestAddress);
            Assert.False(oldRoots.AsSpan().SequenceEqual(latestRoots));
            latest = store.PublishHead(initial.HeadId, latestRoots, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(initial, latest);
        }
        fixture.Data.Dispose();

        // Residue: decode only the public address representation to locate an offline payload
        // byte. RBF3 payload starts after raw HeadLenUnits; leave framing, Key and CRC untouched.
        byte[] addressBytes = new byte[FrameAddress.EncodedSize];
        Assert.True(latestAddress.TryWrite(addressBytes));
        uint fileId = BinaryPrimitives.ReadUInt32LittleEndian(addressBytes);
        SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(addressBytes.AsSpan(4)));
        string dataPath = Path.Combine(fixture.DataRoot, "active", $"{fileId:x8}.rbf");
        byte[] healthyFile = File.ReadAllBytes(dataPath);
        int damagedOffset = checked((int)(ticket.Offset + sizeof(uint) + latestDataBytes.Length / 2));
        Assert.InRange(damagedOffset, checked((int)ticket.Offset + sizeof(uint)),
            checked((int)ticket.Offset + sizeof(uint) + latestDataBytes.Length - 1));
        byte[] damagedFile = healthyFile.ToArray();
        damagedFile[damagedOffset] ^= 1;
        File.WriteAllBytes(dataPath, damagedFile);
        Assert.Equal(damagedFile, File.ReadAllBytes(dataPath));
        var before = CaptureFiles(fixture.Sandbox);

        using (var coldData = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, coldData)) {
            HeadEntry current = cold.ReadHead(initial.HeadId).Unwrap();
            Assert.Equal(latest, current.Id);
            Assert.NotEqual(initial, current.Id);
            Assert.Equal(latestRoots, current.Payload.ToArray());
            var applicationRoots = NamedFrameMapCodec.Decode(current.Payload).Unwrap();
            Assert.Equal(latestAddress, applicationRoots["state"]);

            using var oldData = coldData.ReadFrame(oldAddress).Unwrap();
            Assert.Equal(oldDataBytes, oldData.PayloadAndMeta.ToArray());
            var damagedData = coldData.ReadFrame(applicationRoots["state"]);
            Assert.True(damagedData.IsFailure);
            Assert.Equal("Rbf.CrcMismatch", damagedData.Error!.ErrorCode);

            // Application data failure does not replace the confirmed root with an older one.
            HeadEntry afterFailure = cold.ReadHead(initial.HeadId).Unwrap();
            Assert.Equal(latest, afterFailure.Id);
            Assert.Equal(latestRoots, afterFailure.Payload.ToArray());
        }

        var after = CaptureFiles(fixture.Sandbox);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); }
    }

    private static Dictionary<string, byte[]?> CaptureFiles(string root) {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(root, directory) + Path.DirectorySeparatorChar, null);
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            result.Add(Path.GetRelativePath(root, file), File.ReadAllBytes(file));
        }
        return result;
    }
}
