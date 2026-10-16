using System.Buffers.Binary;
using System.Globalization;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;

namespace Atelia.HeadStore.Tests.Tags;

internal sealed class TagBucketFixture : IDisposable {
    private readonly List<string> _paths = [];
    internal string Path { get; }

    internal TagBucketFixture() => Path = NewPath("bucket");

    internal string NewPath(string role) {
        string parent = Directory.Exists(@"W:\") ? @"W:\" : System.IO.Path.GetTempPath();
        string path = System.IO.Path.Combine(parent, $"headstore-tag-{role}-{Guid.NewGuid():N}.rbf");
        _paths.Add(path);
        return path;
    }

    internal IRbfFile Create(uint bucket) {
        IRbfFile file = RbfFile.CreateNew(Path, RbfCacheMode.Off);
        try {
            file.Append(0, TagTestWire.Header(bucket)).Unwrap();
            return file;
        }
        catch {
            file.Dispose();
            throw;
        }
    }

    internal static SizedPtr Append(IRbfFile file, string name, ReadOnlySpan<byte> root, byte[]? metadata = null) =>
        file.Append(1, root, metadata ?? TagTestWire.Metadata(name)).Unwrap();

    internal void FlipByte(long offset) {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int original = stream.ReadByte();
        if (original < 0) { throw new EndOfStreamException(); }
        stream.Position = offset;
        stream.WriteByte((byte)(original ^ 1));
    }

    // Controlled RBF3 image, not an alternative RBF writer: take a publicly written frame,
    // decode its documented continuous body XOR, set the tombstone bit and reseal only TrailerCRC.
    internal void MakeTombstone(SizedPtr ticket) {
        byte[] frame = File.ReadAllBytes(Path).AsSpan(checked((int)ticket.Offset), ticket.Length).ToArray();
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(frame.Length - 4));
        XorBody(frame, key);
        int trailerStart = frame.Length - 20;
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(trailerStart + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(trailerStart + 4), descriptor | 0x80000000u);
        RollingCrc.SealCodewordBackward(frame.AsSpan(trailerStart, 16));
        XorBody(frame, key);
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = ticket.Offset;
        stream.Write(frame);
    }

    private static void XorBody(byte[] frame, uint key) {
        for (int offset = 4; offset < frame.Length - 4; offset += 4) {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), word ^ key);
        }
    }

    internal static (long Offset, int Length)[] Reads(string logPath) => File.ReadAllLines(logPath)
        .Where(line => !line.StartsWith('#') && !line.StartsWith("seq,", StringComparison.Ordinal))
        .Select(line => {
            string[] fields = line.Split(',');
            return (long.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture));
        }).ToArray();

    public void Dispose() {
        foreach (string path in _paths) { File.Delete(path); }
    }
}
