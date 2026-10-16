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
            return new RbfFileImpl(handle, tailOffset ?? RandomAccess.GetLength(handle), cacheMode);
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

    /// <summary>受控 RBF3 Key=0 fixture，允许构造墓碑与损坏输入。</summary>
    internal static FixtureWriter Create(string path) => new(path);

    internal static byte[] Frame(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> meta = default,
        bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length, meta.Length);
        byte[] bytes = new byte[layout.FrameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, layout.WireFrameLength);
        payload.CopyTo(bytes.AsSpan(4));
        meta.CopyTo(bytes.AsSpan(4 + payload.Length));
        uint crc = RollingCrc.CrcForward(bytes.AsSpan(4, layout.PayloadCrcCoverageLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(layout.PayloadCrcOffset), crc);
        layout.FillTrailer(bytes.AsSpan(layout.TrailerCodewordOffset, 16), tag, isTombstone);
        for (int offset = 4; offset < layout.TailKeyOffset; offset += 4) {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) == RbfLayout.FenceWord) {
                throw new InvalidOperationException("The Key=0 fixture body must not contain an aligned RBF3 Fence.");
            }
        }
        return bytes;
    }

    internal sealed class FixtureWriter : IDisposable {
        private readonly FileStream _stream;
        internal long TailOffset => _stream.Position;

        internal FixtureWriter(string path) {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _stream.Write("RBF3"u8);
        }

        internal AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> meta = default, bool isTombstone = false) {
            byte[] frame = Frame(tag, payload, meta, isTombstone);
            var ticket = SizedPtr.Create(TailOffset, frame.Length);
            _stream.Write(frame);
            _stream.Write("RBF3"u8);
            return ticket;
        }

        public void Dispose() => _stream.Dispose();
    }
}
