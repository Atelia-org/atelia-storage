using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Algorithm fixtures deliberately bypass public open validation to exercise corrupted images.</summary>
internal static class RawRbfTestFile {
    internal static IRbfFile OpenExisting(string path, long? tailOffset = null, RbfCacheMode cacheMode = RbfCacheMode.Off) {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try {
            Span<byte> header = stackalloc byte[4];
            int headerBytes = RandomAccess.Read(handle, header, 0);
            RbfProfile profile = headerBytes == 4 && header.SequenceEqual("RBF3"u8) ? RbfProfile.Rbf3 : RbfProfile.Rbf1;
            return new RbfFileImpl(handle, tailOffset ?? RandomAccess.GetLength(handle), cacheMode, profile: profile);
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    internal static void SetLength(string path, long length) {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        RandomAccess.SetLength(handle, length);
    }

    /// <summary>旧格式只由 fixture 写入器生成；不重新开放生产 RBF1 writer。</summary>
    internal static LegacyWriter CreateLegacy(string path) => new(path);

    internal static byte[] LegacyFrame(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> meta = default,
        bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length, meta.Length);
        byte[] bytes = new byte[layout.FrameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)layout.FrameLength);
        payload.CopyTo(bytes.AsSpan(4));
        meta.CopyTo(bytes.AsSpan(4 + payload.Length));
        uint crc = RollingCrc.CrcForward(bytes.AsSpan(4, layout.PayloadCrcCoverageLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(layout.PayloadCrcOffset), crc);
        layout.FillTrailer(bytes.AsSpan(layout.TrailerCodewordOffset, 16), tag, isTombstone);
        return bytes;
    }

    internal sealed class LegacyWriter : IDisposable {
        private readonly FileStream _stream;
        internal long TailOffset => _stream.Position;

        internal LegacyWriter(string path) {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _stream.Write("RBF1"u8);
        }

        internal AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> meta = default, bool isTombstone = false) {
            byte[] frame = LegacyFrame(tag, payload, meta, isTombstone);
            var ticket = SizedPtr.Create(TailOffset, frame.Length);
            _stream.Write(frame);
            _stream.Write("RBF1"u8);
            return ticket;
        }

        public void Dispose() => _stream.Dispose();
    }
}
