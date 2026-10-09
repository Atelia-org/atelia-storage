using System.Buffers.Binary;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

public sealed class RbfInitialFramePrefixTests {
    private const uint Fence = 0x33464252u;
    private const uint TestTag = 0x01234567u;

    // Fixed real-writer vectors from PrivateInitializationPrefixProbe, independently checked below.
    [Theory]
    [InlineData(false, 0u, "524246330D000000010000000102030405060708090A0B0C0D0E0F10010000001BEC45BAB9E253B100000000000000000D0000000000000052424633")]
    [InlineData(true, 4u, "524246330D00000005000000564246335742463354424633554246330500000055D85282BDE253B10400000004000000090000000400000052424633")]
    [InlineData(false, 0x81234567u, "524246330D0000006645238166472085624324896E4F288D6A4B2C91664523817CA9663BDEA7703067452381674523816A4523816745238152424633")]
    public void RealWriterVectors_AllPrefixesAndInterruptedOutputMatch(bool markers, uint key, string hex) {
        byte[] payload = InitializationPayload(markers);
        byte[] expected = Convert.FromHexString(hex);
        Assert.Equal(expected, EncodeIndependent(payload, 0, key));
        AssertLegalImage(expected);
        string path = TempPath();
        try {
            using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
                AppendInitial(file, payload, key);
                file.DurableFlush();
            }
            Assert.Equal(expected, File.ReadAllBytes(path));
            using (var file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)) {
                var scan = file.ScanForward(showTombstone: true).GetEnumerator();
                Assert.True(scan.MoveNext());
                using var frame = scan.Current.ReadPooledFrame().Unwrap();
                Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
                Assert.Equal(0u, frame.Tag);
                Assert.Equal(0, frame.TailMetaLength);
                Assert.False(frame.IsTombstone);
                Assert.False(scan.MoveNext());
                Assert.Null(scan.TerminationError);
            }
        }
        finally { File.Delete(path); }

        for (int cut = 0; cut <= expected.Length; cut++) {
            path = TempPath();
            bool failed = false;
            RbfWriteInstrumentation.Current = new() {
                BeforeWrite = request => {
                    Assert.Equal(request.Offset == 0 ? 4 : expected.Length - 4, request.RequestedBytes);
                    Assert.True(request.Offset is 0 or 4);
                    return Math.Min(request.RequestedBytes, Math.Max(0, cut - (int)request.Offset));
                }
            };
            try {
                try {
                    using var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
                    if (cut > 4) { AppendInitial(file, payload, key); }
                }
                catch (IOException) { failed = true; }
                finally { RbfWriteInstrumentation.Current = null; }

                Assert.Equal(cut < 4 || (cut > 4 && cut < expected.Length), failed);
                byte[] actual = File.ReadAllBytes(path);
                Assert.Equal(expected.AsSpan(0, cut).ToArray(), actual);
                Assert.True(RbfFile.IsInitialFramePrefix(actual, 0, payload));
                Assert.Equal(actual, File.ReadAllBytes(path));
            }
            finally {
                RbfWriteInstrumentation.Current = null;
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(231)]
    [InlineData(232)]
    public void PublicWriter_BoundariesMatchIndependentEncoding(int length) {
        byte[] payload = Pattern(length);
        string path = TempPath();
        try {
            using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) { file.Append(TestTag, payload).Unwrap(); }
            byte[] actual = File.ReadAllBytes(path);
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(actual.Length - 8));
            Assert.Equal(EncodeIndependent(payload, TestTag, key), actual);
            AssertAllPrefixes(actual, TestTag, payload);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EveryBoundedPayloadLength_AllPrefixesAcceptNonDefaultKeysWithoutChangingInputs() {
        foreach (uint key in new[] { 0u, 4u, 0x81234567u, uint.MaxValue }) {
            for (int length = 0; length <= 232; length++) {
                byte[] payload = Pattern(length);
                byte[] originalPayload = (byte[])payload.Clone();
                byte[] image = EncodeIndependent(payload, TestTag, key);
                byte[] originalImage = (byte[])image.Clone();
                AssertLegalImage(image);
                AssertAllPrefixes(image, TestTag, payload);
                Assert.Equal(originalPayload, payload);
                Assert.Equal(originalImage, image);
            }
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(Fence)]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public void AllTagBits_AreAcceptedWithALegalKey(uint tag) {
        byte[] payload = [1, 2, 3];
        byte[] image = EncodeIndependent(payload, tag, 0x81234567u);
        AssertLegalImage(image);
        AssertAllPrefixes(image, tag, payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(24)]
    [InlineData(232)]
    public void EveryObservedByte_AfterKeyInferenceRejectsSingleBitDamage(int length) {
        byte[] payload = Pattern(length);
        byte[] image = EncodeIndependent(payload, TestTag, 0x81234567u);
        for (int index = 0; index < image.Length; index++) {
            for (int bit = 0; bit < 8; bit++) {
                image[index] ^= (byte)(1 << bit);
                Assert.False(RbfFile.IsInitialFramePrefix(image, TestTag, payload));
                // Fewer than four body bytes can describe a different, still completable Key.
                if (index < 8 || index >= 12) {
                    Assert.False(RbfFile.IsInitialFramePrefix(image.AsSpan(0, index + 1), TestTag, payload));
                }
                image[index] ^= (byte)(1 << bit);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(232)]
    public void PartialFirstBodyWord_AllByteValuesHaveLegalKeyCompletions(int payloadLength) {
        byte[] payload = Pattern(payloadLength);
        byte[] image = EncodeIndependent(payload, TestTag, 0);
        for (int observed = 1; observed <= 3; observed++) {
            byte[] prefix = image.AsSpan(0, 8 + observed).ToArray();
            for (int value = 0; value <= byte.MaxValue; value++) {
                prefix[^1] = (byte)value;
                Assert.True(RbfFile.IsInitialFramePrefix(prefix, TestTag, payload));
            }
        }
    }

    [Fact]
    public void InferredKey_MustEscapeEveryExpectedWordEvenBeforeThoseWordsAreObserved() {
        byte[] payload = Pattern(232);
        byte[] plaintextImage = EncodeIndependent(payload, TestTag, 0);
        for (int offset = 8; offset < plaintextImage.Length - 8; offset += 4) {
            uint forbiddenKey = BinaryPrimitives.ReadUInt32LittleEndian(plaintextImage.AsSpan(offset)) ^ Fence;
            byte[] impossible = EncodeIndependent(payload, TestTag, forbiddenKey);
            Assert.True(RbfFile.IsInitialFramePrefix(impossible.AsSpan(0, 11), TestTag, payload));
            Assert.False(RbfFile.IsInitialFramePrefix(impossible.AsSpan(0, 12), TestTag, payload));
        }
        byte[] rawFenceKey = EncodeIndependent(payload, TestTag, Fence);
        Assert.True(RbfFile.IsInitialFramePrefix(rawFenceKey.AsSpan(0, 11), TestTag, payload));
        Assert.False(RbfFile.IsInitialFramePrefix(rawFenceKey.AsSpan(0, 12), TestTag, payload));
    }

    [Fact]
    public void WrongContentShapeCrcOrSuffix_IsRejected() {
        byte[] payload = [1, 2, 3, 4];
        byte[] image = EncodeIndependent(payload, TestTag, 0);
        byte[] originalPayload = (byte[])payload.Clone();
        byte[] originalImage = (byte[])image.Clone();
        Assert.False(RbfFile.IsInitialFramePrefix(image, TestTag ^ 1, payload));
        Assert.False(RbfFile.IsInitialFramePrefix(image, TestTag, new byte[] { 1, 2, 3, 5 }));
        Assert.False(RbfFile.IsInitialFramePrefix(image, TestTag, Array.Empty<byte>()));
        Assert.False(RbfFile.IsInitialFramePrefix("RBF1"u8, TestTag, payload));
        Assert.False(RbfFile.IsInitialFramePrefix("RBF2"u8, TestTag, payload));
        Assert.False(RbfFile.IsInitialFramePrefix(new byte[8], TestTag, payload));
        Assert.False(RbfFile.IsInitialFramePrefix([.. image, 0], TestTag, payload));
        Assert.False(RbfFile.IsInitialFramePrefix([.. image, .. image.AsSpan(4)], TestTag, payload));
        Assert.False(RbfFile.IsInitialFramePrefix(EncodeIndependent([], TestTag, 0), TestTag, payload));

        // These images have independently recalculated CRCs, so a CRC-only check is insufficient.
        foreach (uint descriptorBits in new[] { 0x80000000u, 1u, 0x00010000u }) {
            Assert.False(RbfFile.IsInitialFramePrefix(EncodeIndependent(payload, TestTag, 0, descriptorBits), TestTag, payload));
        }
        byte[] paddedPayload = [1];
        Assert.False(RbfFile.IsInitialFramePrefix(EncodeIndependent(paddedPayload, TestTag, 0, paddingByte: 0x7F), TestTag, paddedPayload));
        byte[] damaged = (byte[])image.Clone();
        damaged[12] ^= 1;
        byte[] originalDamaged = (byte[])damaged.Clone();
        Assert.False(RbfFile.IsInitialFramePrefix(damaged, TestTag, payload));
        Assert.Equal(originalDamaged, damaged);
        Assert.Equal(originalImage, image);
        Assert.Equal(originalPayload, payload);
    }

    [Fact]
    public void PayloadAboveBound_ThrowsBeforeTestingAnyPrefix() {
        byte[] oversized = new byte[233];
        foreach (byte[] prefix in new[] { Array.Empty<byte>(), "RBF3"u8.ToArray(), new byte[300] }) {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => RbfFile.IsInitialFramePrefix(prefix, TestTag, oversized));
            Assert.Equal("payload", error.ParamName);
        }
    }

    [Fact]
    public void ValidAndIncompatiblePrefixes_AllocateNoManagedBytesAfterWarmup() {
        byte[] payload = Pattern(232);
        byte[] image = EncodeIndependent(payload, TestTag, 0x81234567u);
        byte[] incompatible = (byte[])image.Clone();
        incompatible[12] ^= 1;
        byte[] overlong = [.. image, 0];
        int[] lengths = [0, 1, 3, 4, 5, 7, 8, 9, 10, 11, 12, image.Length - 8, image.Length - 7, image.Length - 4, image.Length - 3, image.Length - 1, image.Length];
        int accepted = 0;
        for (int pass = 0; pass < 2; pass++) {
            long before = GC.GetAllocatedBytesForCurrentThread();
            accepted = 0;
            for (int iteration = 0; iteration < 1000; iteration++) {
                foreach (int length in lengths) {
                    if (RbfFile.IsInitialFramePrefix(image.AsSpan(0, length), TestTag, payload)) { accepted++; }
                }
                if (RbfFile.IsInitialFramePrefix(incompatible, TestTag, payload)) { accepted++; }
                if (RbfFile.IsInitialFramePrefix(overlong, TestTag, payload)) { accepted++; }
                if (RbfFile.IsInitialFramePrefix(image, TestTag ^ 1, payload)) { accepted++; }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(1000 * lengths.Length, accepted);
            if (pass == 1) { Assert.Equal(0L, allocated); }
        }
    }

    private static void AssertAllPrefixes(byte[] image, uint tag, byte[] payload) {
        for (int length = 0; length <= image.Length; length++) {
            Assert.True(RbfFile.IsInitialFramePrefix(image.AsSpan(0, length), tag, payload), $"Rejected {payload.Length}B payload at prefix {length}.");
        }
    }

    private static void AppendInitial(IRbfFile file, byte[] payload, uint key) {
        if (key == 0x81234567u) { ((RbfFileImpl)file).Append(0, payload, default, (_, _, _, _) => key).Unwrap(); }
        else { file.Append(0, payload).Unwrap(); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"rbf-initial-prefix-{Guid.NewGuid():N}.rbf");

    private static byte[] Pattern(int length) {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) { bytes[i] = (byte)(i * 37 + 11); }
        return bytes;
    }

    private static byte[] InitializationPayload(bool markers) {
        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        for (int i = 0; i < 16; i++) { payload[4 + i] = (byte)(i + 1); }
        if (markers) {
            for (uint i = 0; i < 4; i++) { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + (int)i * 4), Fence ^ i); }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 1);
        return payload;
    }

    private static void AssertLegalImage(byte[] image) {
        for (int offset = 4; offset < image.Length - 4; offset += 4) {
            Assert.NotEqual(Fence, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset)));
        }
    }

    // Test-only independent wire/bitwise CRC oracle; no production layout, footer, CRC or XOR calls.
    private static byte[] EncodeIndependent(ReadOnlySpan<byte> payload, uint tag, uint key, uint descriptorBits = 0, byte paddingByte = 0) {
        int alignedLength = (payload.Length + 3) & ~3;
        int frameLength = 28 + alignedLength;
        byte[] image = new byte[frameLength + 8];
        "RBF3"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)frameLength >> 2);
        payload.CopyTo(image.AsSpan(8));
        image.AsSpan(8 + payload.Length, alignedLength - payload.Length).Fill(paddingByte);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8 + alignedLength), CrcIndependent(image.AsSpan(8, alignedLength)));
        int trailer = 12 + alignedLength;
        uint descriptor = ((uint)(alignedLength - payload.Length) << 29) | descriptorBits;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(trailer + 4), descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(trailer + 8), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(trailer + 12), (uint)frameLength >> 2);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(trailer), CrcIndependent(image.AsSpan(trailer + 4, 12), reverse: true));
        for (int offset = 8; offset < image.Length - 8; offset++) { image[offset] ^= (byte)(key >> (((offset - 8) & 3) * 8)); }
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 8), key);
        "RBF3"u8.CopyTo(image.AsSpan(image.Length - 4));
        return image;
    }

    private static uint CrcIndependent(ReadOnlySpan<byte> bytes, bool reverse = false) {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length; i++) {
            crc ^= bytes[reverse ? bytes.Length - 1 - i : i];
            for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0); }
        }
        return crc ^ uint.MaxValue;
    }
}
