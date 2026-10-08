using Xunit;

namespace Atelia.Binary.Tests;

public class StringTests {
    public static IEnumerable<object[]> DefaultGoldens {
        get {
            yield return ["", new byte[] { 0x00 }];
            yield return ["A", new byte[] { 0x03, 0x41 }];
            yield return ["é", new byte[] { 0x02, 0xE9, 0x00 }];
            yield return ["中", new byte[] { 0x02, 0x2D, 0x4E }];
            yield return ["😀", new byte[] { 0x04, 0x3D, 0xD8, 0x00, 0xDE }];
            yield return ["\uD800", new byte[] { 0x02, 0x00, 0xD8 }];
            yield return ["A\uD800", new byte[] { 0x04, 0x41, 0x00, 0x00, 0xD8 }];
            yield return ["\uDC00A", new byte[] { 0x04, 0x00, 0xDC, 0x41, 0x00 }];
            yield return ["\uFEFF", new byte[] { 0x02, 0xFF, 0xFE }];
            yield return ["A\uFEFF", new byte[] { 0x04, 0x41, 0x00, 0xFF, 0xFE }];
        }
    }

    [Theory]
    [MemberData(nameof(DefaultGoldens))]
    public void DefaultEncodingMatchesIndependentGolden(string value, byte[] expected) {
        Assert.Equal(expected, StringTestHelpers.EncodeString(value));
        Assert.Equal((long)expected.Length, BareValueEncoding.MeasureString(value));
        StringEncodingPlan plan = BareValueEncoding.PrepareString(value);
        Assert.Equal((long)expected.Length, plan.EncodedLength);
        var sink = new StringRecordingSink();
        new BareValueWriter(sink).WriteString(in plan);
        Assert.Equal(expected, sink.WrittenBytes);
        var reader = new BareValueReader(expected);
        Assert.Equal(value, reader.ReadString());
        Assert.Equal(expected.Length, reader.ConsumedCount);
        reader.EnsureFullyConsumed();
    }

    public static IEnumerable<object[]> TolerantGoldens {
        get {
            yield return [new byte[] { 0x02, 0x41, 0x00 }, "A", 3];
            yield return [new byte[] { 0x07, 0xE4, 0xB8, 0xAD }, "中", 4];
            yield return [new byte[] { 0x01 }, "", 1];
            yield return [new byte[] { 0x83, 0x00, 0x41 }, "A", 3];
            yield return [new byte[] { 0x80, 0x80, 0x00 }, "", 3];
            yield return [new byte[] { 0x07, 0xEF, 0xBB, 0xBF }, "\uFEFF", 4];
            yield return [new byte[] { 0x0F, 0xEF, 0xBB, 0xBF, 0x41, 0xEF, 0xBB, 0xBF }, "\uFEFFA\uFEFF", 8];
        }
    }

    [Theory]
    [MemberData(nameof(TolerantGoldens))]
    public void ReaderAcceptsLegalAlternativesAndConsumesActualHeader(byte[] encoded, string expected, int consumed) {
        byte[] source = [.. encoded, 0x7F];
        var reader = new BareValueReader(source);
        Assert.Equal(expected, reader.ReadString());
        Assert.Equal(consumed, reader.ConsumedCount);
        Assert.Equal(0x7F, reader.ReadByte());
        Assert.True(reader.End);
    }

    public static IEnumerable<object[]> InvalidUtf8 {
        get {
            yield return [new byte[] { 0x05, 0xC0, 0x80 }]; // Overlong NUL.
            yield return [new byte[] { 0x07, 0xED, 0xA0, 0x80 }]; // Surrogate.
            yield return [new byte[] { 0x09, 0xF4, 0x90, 0x80, 0x80 }]; // Above U+10FFFF.
            yield return [new byte[] { 0x03, 0x80 }]; // Bare continuation.
            yield return [new byte[] { 0x05, 0xC2, 0x41 }]; // Invalid continuation.
            yield return [new byte[] { 0x03, 0xC2 }]; // Truncated code point in complete payload.
        }
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public void IllegalUtf8IsInvalidDataAndLeavesCursorUnchanged(byte[] encoded) {
        AssertReadFailure<InvalidDataException>(encoded);
    }

    [Theory]
    [MemberData(nameof(DefaultGoldens))]
    public void EveryTruncatedStringLeavesCursorUnchanged(string _, byte[] encoded) {
        for (int length = 0; length < encoded.Length; length++) {
            AssertReadFailure<EndOfStreamException>(encoded.AsSpan(0, length).ToArray());
        }
    }

    [Fact]
    public void LimitsAndDeclaredRangesAreCheckedBeforePayloadAllocation() {
        AssertReadFailure<ArgumentOutOfRangeException>([], -1);
        AssertReadFailure<InvalidDataException>([0x03, 0x41], 0);
        AssertReadFailure<InvalidDataException>([0x02, 0x41, 0x00], 1);
        AssertReadFailure<InvalidDataException>([0x80, 0x80, 0x80, 0x80, 0x08]); // UTF16 length 2^31.
        AssertReadFailure<EndOfStreamException>([0xFF, 0xFF, 0xFF, 0xFF, 0x0F]); // UTF8 length int.MaxValue.
        AssertReadFailure<InvalidDataException>([0xFF, 0xFF, 0xFF, 0xFF, 0x0F], 100);
        var utf16Empty = new BareValueReader([0x00]);
        Assert.Equal("", utf16Empty.ReadString(0));
        var utf8Empty = new BareValueReader([0x01]);
        Assert.Equal("", utf8Empty.ReadString(0));
        var atLimit = new BareValueReader([0x03, 0x41]);
        Assert.Equal("A", atLimit.ReadString(1));
    }

    [Fact]
    public void NullAndDefaultPlanAreRejectedBeforeTouchingSink() {
        var sink = new StringRecordingSink();
        var writer = new BareValueWriter(sink);
        Assert.Throws<ArgumentNullException>(() => writer.WriteString((string)null!));
        Assert.Throws<ArgumentNullException>(() => BareValueEncoding.PrepareString(null!));
        Assert.Throws<ArgumentNullException>(() => BareValueEncoding.MeasureString(null!));
        StringEncodingPlan plan = default;
        Assert.Throws<InvalidOperationException>(() => plan.EncodedLength);
        Assert.Throws<InvalidOperationException>(() => writer.WriteString(in plan));
        Assert.Empty(sink.SizeHints);
        Assert.Equal(0, sink.AdvanceCalls);
        Assert.Throws<InvalidOperationException>(() => default(BareValueWriter).WriteString(""));
        Assert.Throws<InvalidOperationException>(() => default(BareValueWriter).WriteString(in plan));
    }

    [Fact]
    public void PlanRetainsTheSelectedImmutableStringAcrossRepeatedWrites() {
        StringEncodingPlan plan = BareValueEncoding.PrepareString("A\uD800");
        byte[] expected = [0x04, 0x41, 0x00, 0x00, 0xD8];
        var first = new StringRecordingSink();
        var second = new StringRecordingSink();
        new BareValueWriter(first).WriteString(in plan);
        new BareValueWriter(second).WriteString(in plan);
        Assert.Equal(expected, first.WrittenBytes);
        Assert.Equal(expected, second.WrittenBytes);
        Assert.Equal(5L, plan.EncodedLength);
    }

    private static void AssertReadFailure<TException>(byte[] encoded, int limit = int.MaxValue) where TException : Exception {
        byte[] source = [0x42, .. encoded];
        var reader = new BareValueReader(source);
        reader.ReadByte();
        int beforeConsumed = reader.ConsumedCount;
        int beforeRemaining = reader.RemainingCount;
        Exception? failure = null;
        try {
            reader.ReadString(limit);
        }
        catch (Exception exception) {
            failure = exception;
        }
        Assert.IsType<TException>(failure);
        Assert.Equal(beforeConsumed, reader.ConsumedCount);
        Assert.Equal(beforeRemaining, reader.RemainingCount);
    }
}
