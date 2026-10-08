using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.Binary;

public readonly partial struct BareValueWriter {
    /// <summary>Writes the default shortest lossless string representation.</summary>
    public void WriteString(string value) {
        _ = GetDownstream();
        StringEncodingPlan plan = BareValueEncoding.PrepareString(value);
        WriteString(in plan);
    }

    /// <summary>Writes a previously selected representation of an immutable string.</summary>
    public void WriteString(in StringEncodingPlan plan) {
        IBufferWriter<byte> downstream = GetDownstream();
        string value = plan.Value;
        uint header = plan.Header;
        WriteVarUInt32(header);
        if ((header & 1) == 0) {
            WriteUtf16Payload(downstream, value.AsSpan());
        }
        else {
            WriteUtf8Payload(downstream, value.AsSpan(), (int)(header >> 1));
        }
    }

    /// <summary>Writes a shortest VarUInt32 length followed by the byte payload.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value) {
        _ = GetDownstream();
        WriteVarUInt32((uint)value.Length);
        WriteRawBytes(value);
    }

    private static void WriteUtf16Payload(IBufferWriter<byte> downstream, ReadOnlySpan<char> value) {
        while (!value.IsEmpty) {
            int characterCount = Math.Min(value.Length, MaxSizeHint / 2);
            int byteCount = characterCount * 2;
            Span<byte> output = downstream.GetSpan(byteCount).Slice(0, byteCount);
            for (int i = 0; i < characterCount; i++) {
                BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(i * 2, 2), value[i]);
            }
            downstream.Advance(byteCount);
            value = value.Slice(characterCount);
        }
    }

    private static void WriteUtf8Payload(IBufferWriter<byte> downstream, ReadOnlySpan<char> value, int byteCount) {
        if (byteCount == 0) {
            return;
        }
        if (byteCount <= MaxSizeHint) {
            Span<byte> output = downstream.GetSpan(byteCount).Slice(0, byteCount);
            int written = BareValueEncoding.StrictUtf8.GetBytes(value, output);
            downstream.Advance(written);
            return;
        }

        var encoder = BareValueEncoding.StrictUtf8.GetEncoder();
        int remainingBytes = byteCount;
        while (remainingBytes != 0) {
            int sizeHint = Math.Min(remainingBytes, MaxSizeHint);
            Span<byte> output = downstream.GetSpan(sizeHint).Slice(0, sizeHint);
            encoder.Convert(value, output, true, out int charactersUsed, out int bytesUsed, out _);
            downstream.Advance(bytesUsed);
            value = value.Slice(charactersUsed);
            remainingBytes -= bytesUsed;
        }
    }
}
