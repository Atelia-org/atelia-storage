using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class Rbf3WriterTests {
    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(false, 1, 3)]
    [InlineData(true, 3, 1)]
    [InlineData(false, 232, 0)]
    [InlineData(true, 232, 0)]
    [InlineData(false, 233, 3)]
    [InlineData(true, 233, 3)]
    [InlineData(false, 4096, 1)]
    [InlineData(true, 8195, 3)]
    [InlineData(false, 1048577, 65535)]
    [InlineData(true, 1048577, 65535)]
    public void AppendAndBuilder_ProduceIndependentUnitsCrcAndMarkerFreeWire(bool builder, int payloadLength, int metaLength) {
        using var fixture = new Rbf3WriterFixture();
        byte[] payload = Rbf3WriterOracle.Pattern(payloadLength);
        byte[] meta = Rbf3WriterOracle.Pattern(metaLength);
        if (payload.Length >= 4) { BinaryPrimitives.WriteUInt32LittleEndian(payload, Rbf3WriterOracle.Fence); }
        byte[] originalPayload = payload.ToArray();
        byte[] originalMeta = meta.ToArray();
        SizedPtr ticket;
        if (builder) {
            using var frame = fixture.File.BeginAppend();
            Rbf3WriterOracle.WriteBuilder(frame, payload.Concat(meta).ToArray());
            Assert.Equal(4, RandomAccess.GetLength(fixture.Handle));
            ticket = frame.EndAppend(Rbf3WriterOracle.Fence, meta.Length).Unwrap();
        }
        else { ticket = fixture.File.Append(Rbf3WriterOracle.Fence, payload, meta).Unwrap(); }

        Assert.Equal(28 + ((payload.Length + meta.Length + 3) & ~3), ticket.Length);
        Assert.Equal(ticket.EndOffsetExclusive + 4, fixture.File.TailOffset);
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), payload, meta, Rbf3WriterOracle.Fence);
        Assert.Equal(originalPayload, payload);
        Assert.Equal(originalMeta, meta);
        using var decoded = fixture.File.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal(payload.Concat(meta).ToArray(), decoded.PayloadAndMeta.ToArray());
        Assert.Equal(meta.Length, decoded.TailMetaLength);
        Assert.Equal(Rbf3WriterOracle.Fence, decoded.Tag);
    }

    [Fact]
    public void FooterTagAloneForbidsZero_AndInclusiveTinyBodyUsesSmallestKey() {
        using var fixture = new Rbf3WriterFixture();
        var empty = fixture.File.Append(Rbf3WriterOracle.Fence, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty).Unwrap();
        byte[] wire = fixture.ReadFrame(empty);
        uint key = Rbf3WriterOracle.AssertWire(wire, Array.Empty<byte>(), Array.Empty<byte>(), Rbf3WriterOracle.Fence);
        Assert.NotEqual(0u, key);
        Assert.Equal(Rbf3WriterOracle.SmallestKey(wire), key);

        byte[] payload = new byte[232];
        for (int i = 0; i < 58; i++) { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4), Rbf3WriterOracle.Fence ^ (uint)i); }
        var boundary = fixture.File.Append(Rbf3WriterOracle.Fence ^ 58u, payload, ReadOnlySpan<byte>.Empty).Unwrap();
        wire = fixture.ReadFrame(boundary);
        key = Rbf3WriterOracle.AssertWire(wire, payload, Array.Empty<byte>(), Rbf3WriterOracle.Fence ^ 58u);
        Assert.Equal(256, boundary.Length - 4);
        Assert.InRange(key, 59u, 63u);
        Assert.Equal(Rbf3WriterOracle.SmallestKey(wire), key);
        Assert.Equal(0, fixture.Pool.RentCalls);
    }

    [Theory]
    [InlineData(0x80000001u, 1048548, 0)]
    [InlineData(uint.MaxValue, 1048577, 3)]
    [InlineData(0xFEDCBA98u, 2097143, 1)]
    public void ExplicitHighSelection_ExercisesBoundedScratchAndCrossSpanPhase(uint key, int payloadLength, int metaLength) {
        using var fixture = new Rbf3WriterFixture();
        byte[] payload = new byte[payloadLength];
        byte[] meta = new byte[metaLength];
        var writes = new List<int>();
        RbfWriteInstrumentation.Current = new() { BeforeWrite = request => { writes.Add(request.RequestedBytes); return request.RequestedBytes; } };

        var ticket = fixture.File.Append(11, payload, meta, Rbf3WriterOracle.KnownKey(key)).Unwrap();

        Assert.Equal(key, Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), payload, meta, 11));
        Assert.All(writes, count => Assert.InRange(count, 1, RbfAppendImpl.EscapeScratchSize));
        Assert.Contains(RbfAppendImpl.EscapeScratchSize, writes);
        if (ticket.Length == RbfAppendImpl.EscapeScratchSize) { Assert.Equal(new[] { RbfAppendImpl.EscapeScratchSize, 4 }, writes); }
        Assert.Equal(1, fixture.Pool.RentCalls);
        Assert.Equal(0, fixture.Pool.ReturnCalls);
        fixture.File.Append(12, payload, meta, Rbf3WriterOracle.KnownKey(key)).Unwrap();
        Assert.Equal(1, fixture.Pool.RentCalls);
        fixture.File.Dispose();
        Assert.Equal(1, fixture.Pool.ReturnCalls);
        fixture.File.Dispose();
        Assert.Equal(1, fixture.Pool.ReturnCalls);
    }

    [Fact]
    public void LargeZeroKey_DoesNotRentScratchAndWritesBorrowedInputs() {
        using var fixture = new Rbf3WriterFixture();
        byte[] payload = new byte[10001];
        byte[] meta = new byte[3];
        var writes = new List<int>();
        RbfWriteInstrumentation.Current = new() { BeforeWrite = request => { writes.Add(request.RequestedBytes); return request.RequestedBytes; } };

        var ticket = fixture.File.Append(11, payload, meta).Unwrap();

        Assert.Equal(0u, Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), payload, meta, 11));
        Assert.Equal(new[] { 4, payload.Length, meta.Length, 28 }, writes);
        Assert.Equal(0, fixture.Pool.RentCalls);
    }

    [Fact]
    public void BuilderReservationsAreBackfilledBeforeEncoding_AndOldEpochCannotCancelNewBuilder() {
        using var fixture = new Rbf3WriterFixture();
        var first = fixture.File.BeginAppend();
        var writer = first.PayloadAndMeta;
        writer.ReserveSpan(3, out int token).Fill(0xA5);
        writer.GetSpan(1)[0] = 0x31;
        writer.Advance(1);
        Assert.True(writer.TryGetReservedSpan(token, out var reservation));
        reservation[0] = 1;
        reservation[1] = 2;
        reservation[2] = 3;
        writer.Commit(token);
        var ticket = first.EndAppend(11).Unwrap();
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), new byte[] { 1, 2, 3, 0x31 }, Array.Empty<byte>(), 11);

        using var second = fixture.File.BeginAppend();
        first.Dispose();
        Rbf3WriterOracle.WriteBuilder(second, new byte[] { 9 });
        var secondTicket = second.EndAppend(12).Unwrap();
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(secondTicket), new byte[] { 9 }, Array.Empty<byte>(), 12);
        Assert.Throws<InvalidOperationException>(() => { writer.GetSpan(1); });
    }

    [Theory]
    [InlineData(-4L)]
    [InlineData(2L)]
    [InlineData(1099511627776L)]
    public void InvalidFrameStart_ReturnsArgumentErrorBeforeOutput(long offset) {
        using var fixture = new Rbf3WriterFixture();
        byte[]? scratch = null;
        int faults = 0;
        long original = offset;

        var result = RbfAppendImpl.AppendRbf3(fixture.Handle, ref offset, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty,
            11, ref scratch, fixture.Pool, () => faults++);

        Assert.True(result.IsFailure);
        Assert.Equal("Rbf.ArgumentError", result.Error!.ErrorCode);
        Assert.Equal(original, offset);
        Assert.Equal(0, faults);
        Assert.Null(scratch);
        Assert.Equal(4, RandomAccess.GetLength(fixture.Handle));
    }
}

internal sealed class Rbf3WriterFixture : IDisposable {
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rbf3-writer-{Guid.NewGuid():N}.rbf");
    internal SafeFileHandle Handle { get; }
    internal RbfFileImpl File { get; }
    internal Rbf3ScratchPool Pool { get; }

    internal Rbf3WriterFixture(Rbf3ScratchPool? pool = null, RbfProfile profile = RbfProfile.Rbf3, bool readOnly = false,
        ArrayPool<byte>? builderPool = null) {
        Pool = pool ?? new Rbf3ScratchPool();
        Handle = System.IO.File.OpenHandle(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);
        RandomAccess.Write(Handle, RbfLayout.GetFence(profile), 0);
        RbfWriteInstrumentation.RegisterPath(Handle, Path);
        File = new RbfFileImpl(Handle, 4, RbfCacheMode.Off, readOnly: readOnly, appendPool: Pool, profile: profile,
            builderPool: builderPool);
    }

    internal byte[] ReadBytes() {
        byte[] bytes = new byte[(int)RandomAccess.GetLength(Handle)];
        Assert.Equal(bytes.Length, RandomAccess.Read(Handle, bytes, 0));
        return bytes;
    }

    internal byte[] ReadFrame(SizedPtr ticket) {
        byte[] bytes = new byte[ticket.Length + 4];
        Assert.Equal(bytes.Length, RandomAccess.Read(Handle, bytes, ticket.Offset));
        return bytes;
    }

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        File.Dispose();
        System.IO.File.Delete(Path);
    }
}

internal sealed class Rbf3ScratchPool : ArrayPool<byte> {
    internal int RentCalls { get; private set; }
    internal int ReturnCalls { get; private set; }
    internal bool FailRent { get; set; }
    internal bool ReturnUndersized { get; set; }
    private readonly HashSet<byte[]> _owned = new();

    public override byte[] Rent(int minimumLength) {
        RentCalls++;
        if (FailRent) { throw new OutOfMemoryException("Simulated output scratch pool failure."); }
        byte[] bytes = new byte[ReturnUndersized ? minimumLength - 1 : minimumLength];
        _owned.Add(bytes);
        return bytes;
    }

    public override void Return(byte[] array, bool clearArray = false) {
        Assert.True(_owned.Remove(array), "Only the outstanding owned scratch may be returned, once.");
        ReturnCalls++;
        if (clearArray) { array.AsSpan().Clear(); }
    }
}

internal static class Rbf3WriterOracle {
    internal const uint Fence = 0x33464252;

    internal static byte[] Pattern(int length) {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) { bytes[i] = (byte)(i * 37 + 11); }
        return bytes;
    }

    internal static void WriteBuilder(RbfFrameBuilder frame, byte[] bytes) {
        var writer = frame.PayloadAndMeta;
        int offset = 0;
        while (offset < bytes.Length) {
            int count = Math.Min(offset == 0 ? 1 : 4092, bytes.Length - offset);
            bytes.AsSpan(offset, count).CopyTo(writer.GetSpan(count));
            writer.Advance(count);
            offset += count;
        }
    }

    internal static uint AssertWire(byte[] wire, byte[] payload, byte[] meta, uint tag) {
        int coverageLength = (payload.Length + meta.Length + 3) & ~3;
        int length = coverageLength + 28;
        Assert.Equal(length + 4, wire.Length);
        Assert.Equal((uint)length >> 2, BinaryPrimitives.ReadUInt32LittleEndian(wire));
        Assert.Equal(Fence, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(length)));
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(length - 4));
        Assert.NotEqual(Fence, key);
        for (int i = 0; i < length; i += 4) { Assert.NotEqual(Fence, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(i))); }
        byte[] plain = Plaintext(wire);
        Assert.Equal(payload, plain.AsSpan(4, payload.Length).ToArray());
        Assert.Equal(meta, plain.AsSpan(4 + payload.Length, meta.Length).ToArray());
        Assert.All(plain.AsSpan(4 + payload.Length + meta.Length, coverageLength - payload.Length - meta.Length).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(Crc(plain.AsSpan(4, coverageLength)), BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4 + coverageLength)));
        int trailer = 8 + coverageLength;
        Assert.Equal(Crc(plain.AsSpan(trailer + 4, 12), reverse: true), BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(trailer)));
        Assert.Equal((uint)((coverageLength - payload.Length - meta.Length) << 29) | (uint)meta.Length,
            BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(trailer + 4)));
        Assert.Equal(tag, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(trailer + 8)));
        Assert.Equal((uint)length >> 2, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(trailer + 12)));
        Assert.Equal(key, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(length - 8)) ^ ((uint)length >> 2));
        return key;
    }

    private static byte[] Plaintext(byte[] wire) {
        byte[] plain = wire.ToArray();
        int length = wire.Length - 4;
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(length - 4));
        for (int i = 4; i < length - 4; i++) { plain[i] ^= (byte)(key >> (((i - 4) & 3) * 8)); }
        return plain;
    }

    internal static uint SmallestKey(byte[] wire) {
        byte[] plain = Plaintext(wire);
        var forbidden = new HashSet<uint> { Fence };
        for (int i = 4; i < plain.Length - 8; i += 4) { forbidden.Add(BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(i)) ^ Fence); }
        uint key = 0;
        while (forbidden.Contains(key)) { key++; }
        return key;
    }

    internal static RbfEscapeKeySelector KnownKey(uint key) {
        return (fence, payload, meta, footer) => {
            Assert.NotEqual(fence, key);
            uint word = 0;
            int count = 0;
            Check(payload, fence, key, ref word, ref count);
            Check(meta, fence, key, ref word, ref count);
            Check(footer, fence, key, ref word, ref count);
            Assert.Equal(0, count);
            return key;
        };
    }

    private static void Check(ReadOnlySpan<byte> bytes, uint fence, uint key, ref uint word, ref int count) {
        foreach (byte value in bytes) {
            word |= (uint)value << (count * 8);
            if (++count == 4) {
                Assert.NotEqual(fence, word ^ key);
                word = 0;
                count = 0;
            }
        }
    }

    // Independent bitwise CRC32C; no production RollingCrc or trailer codec is used by the oracle.
    private static uint Crc(ReadOnlySpan<byte> bytes, bool reverse = false) {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length; i++) {
            crc ^= bytes[reverse ? bytes.Length - 1 - i : i];
            for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0); }
        }
        return crc ^ uint.MaxValue;
    }
}
