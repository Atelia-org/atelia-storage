using System.Collections;
using System.Collections.Frozen;
using Atelia.Binary;
using Atelia.FrameStore;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore.Tests.NamedFrameMap;

public class NamedFrameMapCodecTests {
    [Fact]
    public void IndependentGoldens_EmptyMinimumAndMaximumAddresses() {
        Assert.Equal(new byte[] { 0x00 }, NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress>()).Unwrap());
        Assert.Empty(NamedFrameMapCodec.Decode([0x00]).Unwrap());
        byte[] minimum = Convert.FromHexString("010341018702");
        byte[] maximum = Convert.FromHexString("010341FFFFFFFF0FFFFFFFFFFFFFFFFFFF01");
        foreach (var (wire, address) in new[] {
            (minimum, NamedFrameMapFixture.MinimumAddress()), (maximum, NamedFrameMapFixture.MaximumAddress())
        }) {
            Assert.Equal(wire, NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["A"] = address }).Unwrap());
            var decoded = NamedFrameMapCodec.Decode(wire).Unwrap();
            Assert.Single(decoded);
            Assert.Equal(address, decoded["A"]);
        }
    }

    [Fact]
    public void PublicNumericAddresses_CoverEveryShortestWidthFromThreeThroughFifteen() {
        var observedWidths = new HashSet<int>();
        foreach (uint fileId in new[] { 1u, 128u, 16_384u, 2_097_152u, uint.MaxValue }) {
            for (int bit = 0; bit < 38; bit++) {
                FrameAddress address = NamedFrameMapFixture.NumericAddress(fileId, 4L << bit);
                byte[] wire = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["A"] = address }).Unwrap();
                observedWidths.Add(address.MeasureVarInt());
                Assert.Equal(3 + address.MeasureVarInt(), wire.Length);
                Assert.Equal(address, NamedFrameMapCodec.Decode(wire).Unwrap()["A"]);
            }
        }
        Assert.Equal(Enumerable.Range(3, 13), observedWidths.Order());
    }

    [Fact]
    public void RealIssuedAddresses_PersistAndReadAfterCodecAndStoreReopen() {
        using var fixture = new NamedFrameMapFixture();
        var issued = fixture.CreateClosed();
        var original = new Dictionary<string, FrameAddress> { ["first"] = issued.First, ["second"] = issued.Second };
        byte[] wire = NamedFrameMapCodec.Encode(original).Unwrap();
        string encodedPath = Path.Combine(fixture.Root, "..", "map.bin");
        File.WriteAllBytes(encodedPath, wire);
        var restored = NamedFrameMapCodec.Decode(File.ReadAllBytes(encodedPath)).Unwrap();
        using var store = Store.OpenReadOnly(fixture.Root);
        using var first = store.ReadFrame(restored["first"]).Unwrap();
        using var second = store.ReadFrame(restored["second"]).Unwrap();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first.PayloadAndMeta.ToArray());
        Assert.Equal(17u, first.Tag);
        Assert.Equal(1, first.TailMetaLength);
        Assert.Equal("second"u8.ToArray(), second.PayloadAndMeta.ToArray());
        Assert.Equal(23u, second.Tag);
    }

    [Fact]
    public void ValidUnicodeAndApplicationKeys_PreserveOrdinalIdentity() {
        string[] keys = ["", "A", "a", "中", "😀", "\0", "\uFEFF", "\n\r\t\u0001", " /\\ ", "\"", "é", "e\u0301", "\uFFFD"];
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        var original = keys.ToDictionary(key => key, _ => address, StringComparer.Ordinal);
        var decoded = NamedFrameMapCodec.Decode(NamedFrameMapCodec.Encode(original).Unwrap()).Unwrap();
        Assert.Equal(keys.Length, decoded.Count);
        foreach (string key in keys) { Assert.Equal(address, decoded[key]); }
        var reversed = NamedFrameMapCodec.Decode(NamedFrameMapCodec.Encode(keys.Reverse().ToDictionary(key => key, _ => address)).Unwrap()).Unwrap();
        Assert.Equal(decoded.OrderBy(pair => pair.Key, StringComparer.Ordinal), reversed.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.False(decoded.ContainsKey("É"));
    }

    [Fact]
    public void UnpairedSurrogates_AreRejectedInInputAndLegalBinaryUtf16() {
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        // Attribute string serialization cannot preserve an isolated surrogate, so create at runtime.
        string[] keys = ["\uD800", "\uDC00", "\uD800\uD800", "x\uD800", "\uD800x", "\uD83D\uDE00\uD800", "\uDC00\uD800"];
        foreach (string key in keys) {
            Assert.True(NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { [key] = address }).IsFailure);
            byte[] wire = NamedFrameMapFixture.OneEntry(key, address);
            // Binary accepts these CLR code units; NamedFrameMap has the narrower key domain.
            var reader = new BareValueReader(wire);
            Assert.Equal(1u, reader.ReadVarUInt32());
            Assert.Equal(key, reader.ReadString());
            Assert.True(NamedFrameMapCodec.Decode(wire).IsFailure);
        }
    }

    [Fact]
    public void NullKeysDefaultAddressAndActualDuplicates_AreRejected() {
        Assert.True(NamedFrameMapCodec.Encode(null!).IsFailure);
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        var nullKey = new EnumeratedRoots([new(null!, address)]);
        var defaultAddress = new EnumeratedRoots([new("A", default)]);
        var duplicate = new EnumeratedRoots([new("A", address), new("A", NamedFrameMapFixture.MaximumAddress())]);
        foreach (var input in new[] { nullKey, defaultAddress, duplicate }) {
            Assert.True(NamedFrameMapCodec.Encode(input).IsFailure);
            Assert.True(input.Disposed);
        }
    }

    [Fact]
    public void Capture_UsesActualEnumerationAndOrdinalWithoutSourceCountOrLookup() {
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        var input = new EnumeratedRoots([new("A", address), new("a", address)]);
        byte[] wire = NamedFrameMapCodec.Encode(input).Unwrap();
        Assert.True(input.Disposed);
        var decoded = NamedFrameMapCodec.Decode(wire).Unwrap();
        Assert.Equal(2, decoded.Count);
        Assert.Equal(address, decoded["A"]);
        Assert.Equal(address, decoded["a"]);
        Assert.Equal(2, wire[0]);

        var emptyLyingCount = new EnumeratedRoots([], int.MaxValue);
        Assert.Equal(new byte[] { 0x00 }, NamedFrameMapCodec.Encode(emptyLyingCount).Unwrap());
        var oneLyingCount = new EnumeratedRoots([new("A", address)], 0);
        Assert.Equal(Convert.FromHexString("010341018702"), NamedFrameMapCodec.Encode(oneLyingCount).Unwrap());
        var ignoreCase = new Dictionary<string, FrameAddress>(StringComparer.OrdinalIgnoreCase) { ["A"] = address };
        var capturedIgnoreCase = NamedFrameMapCodec.Decode(NamedFrameMapCodec.Encode(ignoreCase).Unwrap()).Unwrap();
        Assert.True(capturedIgnoreCase.ContainsKey("A"));
        Assert.False(capturedIgnoreCase.ContainsKey("a"));
    }

    [Fact]
    public void EnumerationAndNecessaryCleanupExceptions_PropagateIncludingInvalidDataException() {
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        var enumerationFailure = new InvalidDataException("caller enumeration");
        var cleanupFailure = new InvalidOperationException("caller cleanup");
        var duringEnumeration = new EnumeratedRoots([new("A", address)], moveFailure: enumerationFailure);
        Assert.Same(enumerationFailure, Assert.Throws<InvalidDataException>(() => { NamedFrameMapCodec.Encode(duringEnumeration); }));
        Assert.True(duringEnumeration.Disposed);
        var duringCleanup = new EnumeratedRoots([new("A", address)], disposeFailure: cleanupFailure);
        Assert.Same(cleanupFailure, Assert.Throws<InvalidOperationException>(() => { NamedFrameMapCodec.Encode(duringCleanup); }));
        var cleanupOnRefusal = new EnumeratedRoots([new("A", default)], disposeFailure: cleanupFailure);
        Assert.Same(cleanupFailure, Assert.Throws<InvalidOperationException>(() => { NamedFrameMapCodec.Encode(cleanupOnRefusal); }));
    }

    [Fact]
    public void OwnedOutputs_RemainIndependentAndExposeOnlyFrozenCollections() {
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        var source = new Dictionary<string, FrameAddress> { ["A"] = address };
        byte[] encoded = NamedFrameMapCodec.Encode(source).Unwrap();
        source.Clear();
        var decoded = NamedFrameMapCodec.Decode(encoded).Unwrap();
        Array.Fill(encoded, (byte)0xFF);
        Assert.Equal(address, decoded["A"]);
        Assert.IsAssignableFrom<FrozenDictionary<string, FrameAddress>>(decoded);
        var mutableInterface = Assert.IsAssignableFrom<IDictionary<string, FrameAddress>>(decoded);
        Assert.True(mutableInterface.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableInterface["A"] = default);
        Assert.Throws<NotSupportedException>(() => mutableInterface.Add("B", address));
        Assert.Throws<NotSupportedException>(() => mutableInterface.Remove("A"));
        Assert.Throws<NotSupportedException>(() => mutableInterface.Clear());
        AssertCannotMutate(decoded.Keys);
        AssertCannotMutate(decoded.Values);
        var another = NamedFrameMapCodec.Decode(NamedFrameMapFixture.OneEntry("B", NamedFrameMapFixture.MaximumAddress())).Unwrap();
        Assert.Equal(address, decoded["A"]);
        Assert.False(decoded.ContainsKey("B"));
        Assert.Single(another);
        byte[] secondEncode = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["A"] = address }).Unwrap();
        byte[] thirdEncode = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["A"] = address }).Unwrap();
        secondEncode[0] = 0xFF;
        Assert.Equal(Convert.FromHexString("010341018702"), thirdEncode);
    }

    [Theory]
    [InlineData("8000")] // Empty with redundant count.
    [InlineData("01024100018702")] // Alternate UTF-16LE A.
    [InlineData("01830041018702")] // Redundant string header.
    [InlineData("81000341018702")] // Redundant count.
    [InlineData("01034181008702")] // Redundant FileId.
    [InlineData("01034101878200")] // Redundant ticket.
    [InlineData("010341818080800087828080808080808000")] // Maximum redundant widths.
    public void LegalRedundantForms_ConsumeActualBytesAndRewriteShortest(string hex) {
        byte[] wire = Convert.FromHexString(hex);
        var frames = NamedFrameMapCodec.Decode(wire).Unwrap();
        byte[] rewritten = NamedFrameMapCodec.Encode(frames).Unwrap();
        Assert.Equal(frames.Count == 0 ? new byte[] { 0 } : Convert.FromHexString("010341018702"), rewritten);
        Assert.True(NamedFrameMapCodec.Decode([.. wire, 0x00]).IsFailure);
    }

    [Theory]
    [InlineData("020341018702024100018702")] // Same decoded key, UTF-8 and UTF-16LE.
    [InlineData("020341018702034181008702")] // Same key with different address representation.
    [InlineData("020001870201018702")] // Empty key, UTF-16 and UTF-8 headers.
    public void DuplicateDecodedKeys_AreRejectedAcrossRepresentations(string hex) {
        Assert.True(NamedFrameMapCodec.Decode(Convert.FromHexString(hex)).IsFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("007F")]
    [InlineData("01034101870200")]
    [InlineData("FFFFFFFF0F")]
    [InlineData("8080808010")]
    [InlineData("8080808080")]
    [InlineData("01FFFFFFFF0F018702")]
    [InlineData("0109414243440187")] // Key consumes minimum address suffix.
    [InlineData("020B4142434445018702000187")] // Key leaves too little for following entry.
    [InlineData("0107EDA080018702")] // Invalid UTF-8 surrogate.
    [InlineData("0105C080018702")] // Overlong UTF-8.
    [InlineData("010341008702")] // Zero FileId.
    [InlineData("010341018302")] // Offset zero.
    [InlineData("010341018602")] // Length below minimum.
    [InlineData("0103410100")]
    [InlineData("01034180808080108702")]
    [InlineData("01034180808080808702")]
    [InlineData("0103410180808080808080808002")]
    [InlineData("0103410180808080808080808080")]
    public void InvalidFormatAndRequiredSuffix_AreRejected(string hex) {
        Assert.True(NamedFrameMapCodec.Decode(Convert.FromHexString(hex)).IsFailure);
    }

    [Fact]
    public void EveryGoldenTruncation_IsRejected() {
        foreach (byte[] wire in new[] {
            Convert.FromHexString("010341018702"), Convert.FromHexString("010341FFFFFFFF0FFFFFFFFFFFFFFFFFFF01"),
            Convert.FromHexString("02000187020341018702")
        }) {
            for (int length = 0; length < wire.Length; length++) {
                Assert.True(NamedFrameMapCodec.Decode(wire.AsSpan(0, length)).IsFailure);
            }
            Assert.True(NamedFrameMapCodec.Decode(wire).IsSuccess);
        }
    }

    [Fact]
    public void HugeCountAndStringDeclarations_RejectBeforeLargeMaterialization() {
        byte[][] malformed = [Convert.FromHexString("FFFFFFFF0F"), Convert.FromHexString("01FFFFFFFF0F018702")];
        foreach (byte[] wire in malformed) {
            Assert.True(NamedFrameMapCodec.Decode(wire).IsFailure); // Warm this rejection path.
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool rejected = NamedFrameMapCodec.Decode(wire).IsFailure;
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(rejected);
            Assert.InRange(allocation, 0, 32_768);
        }
    }

    [Fact]
    public void ActualOneMiBBoundary_UsesAddressWidthAndRejectsRedundancyOverLimit() {
        Assert.Equal(1_048_576, HeadStore.MaxHeadEntryPayloadLength);
        foreach (FrameAddress address in new[] { NamedFrameMapFixture.MinimumAddress(), NamedFrameMapFixture.MaximumAddress() }) {
            string key = new('x', HeadStore.MaxHeadEntryPayloadLength - 1 - 3 - address.MeasureVarInt());
            var input = new Dictionary<string, FrameAddress> { [key] = address };
            byte[] wire = NamedFrameMapCodec.Encode(input).Unwrap();
            Assert.Equal(HeadStore.MaxHeadEntryPayloadLength, wire.Length);
            Assert.Equal(address, NamedFrameMapCodec.Decode(wire).Unwrap()[key]);
            Assert.True(NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { [key + "x"] = address }).IsFailure);
            Assert.True(NamedFrameMapCodec.Decode([0x81, 0x00, .. wire.AsSpan(1)]).IsFailure);
            Assert.True(NamedFrameMapCodec.Decode([.. wire, 0x00]).IsFailure);
        }

        FrameAddress minimum = NamedFrameMapFixture.MinimumAddress();
        string belowKey = new('x', HeadStore.MaxHeadEntryPayloadLength - 1 - 3 - minimum.MeasureVarInt() - 1);
        byte[] below = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { [belowKey] = minimum }).Unwrap();
        byte[] fullRedundantCount = [0x81, 0x00, .. below.AsSpan(1)];
        Assert.Equal(HeadStore.MaxHeadEntryPayloadLength, fullRedundantCount.Length);
        Assert.Equal(minimum, NamedFrameMapCodec.Decode(fullRedundantCount).Unwrap()[belowKey]);
        byte[] fullRedundantStringHeader = [.. below.AsSpan(0, 3), (byte)(below[3] | 0x80), 0x00, .. below.AsSpan(4)];
        Assert.Equal(HeadStore.MaxHeadEntryPayloadLength, fullRedundantStringHeader.Length);
        Assert.Equal(minimum, NamedFrameMapCodec.Decode(fullRedundantStringHeader).Unwrap()[belowKey]);
        byte[] fullRedundantAddress = [.. below.AsSpan(0, below.Length - 3), 0x81, 0x00, 0x87, 0x02];
        Assert.Equal(HeadStore.MaxHeadEntryPayloadLength, fullRedundantAddress.Length);
        Assert.Equal(minimum, NamedFrameMapCodec.Decode(fullRedundantAddress).Unwrap()[belowKey]);
    }

    [Fact]
    public void ActualEntryGrowth_AccountsForCountPrefixWidthBeforeOutput() {
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        var frames = Enumerable.Range(0, 128).ToDictionary(i => i.ToString(), _ => address, StringComparer.Ordinal);
        byte[] wire = NamedFrameMapCodec.Encode(frames).Unwrap();
        Assert.Equal((byte)0x80, wire[0]);
        Assert.Equal((byte)0x01, wire[1]);
        long expected = BareValueEncoding.MeasureVarUInt32(128);
        foreach (string key in frames.Keys) { expected += BareValueEncoding.MeasureString(key) + address.MeasureVarInt(); }
        Assert.Equal(expected, wire.LongLength);
        Assert.Equal(128, NamedFrameMapCodec.Decode(wire).Unwrap().Count);
    }

    [Fact]
    public void CapacityRefusal_StopsCaptureAndCompletesNecessaryCleanupWithoutOutput() {
        FrameAddress address = NamedFrameMapFixture.MinimumAddress();
        string fullKey = new('x', HeadStore.MaxHeadEntryPayloadLength - 1 - 3 - address.MeasureVarInt());
        var input = new EnumeratedRoots([new(fullKey, address), new("A", address)],
            moveFailure: new InvalidOperationException("Capture continued after exceeding the byte budget."));
        Assert.True(NamedFrameMapCodec.Encode(input).IsFailure);
        Assert.True(input.Disposed);

        string necessarilyTooLarge = new('x', HeadStore.MaxHeadEntryPayloadLength);
        Assert.True(NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { [necessarilyTooLarge] = address }).IsFailure);
    }

    private static void AssertCannotMutate<T>(IEnumerable<T> values) {
        Assert.False(values is T[]);
        Assert.False(values is List<T>);
        if (values is ICollection<T> collection) {
            Assert.True(collection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => collection.Clear());
        }
        if (values is IList list) {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
    }

    // A collection facade whose metadata and lookup surface cannot be used for capture.
    private sealed class EnumeratedRoots(
        KeyValuePair<string, FrameAddress>[] entries, int? count = null,
        Exception? moveFailure = null, Exception? disposeFailure = null) : IReadOnlyDictionary<string, FrameAddress> {
        internal bool Disposed { get; private set; }
        public int Count => count ?? throw new InvalidOperationException("Source Count must not be read.");
        public IEnumerable<string> Keys => throw new InvalidOperationException("Source Keys must not be read.");
        public IEnumerable<FrameAddress> Values => throw new InvalidOperationException("Source Values must not be read.");
        public FrameAddress this[string key] => throw new InvalidOperationException("Source lookup must not be read.");
        public bool ContainsKey(string key) => throw new InvalidOperationException("Source lookup must not be read.");
        public bool TryGetValue(string key, out FrameAddress value) => throw new InvalidOperationException("Source lookup must not be read.");

        public IEnumerator<KeyValuePair<string, FrameAddress>> GetEnumerator() {
            try {
                foreach (var entry in entries) { yield return entry; }
                if (moveFailure is not null) { throw moveFailure; }
            }
            finally {
                Disposed = true;
                if (disposeFailure is not null) { throw disposeFailure; }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
