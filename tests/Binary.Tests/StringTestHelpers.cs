using System.Buffers;

namespace Atelia.Binary.Tests;

internal sealed class StringRecordingSink : IBufferWriter<byte> {
    private readonly int _extraCapacity;
    private byte[]? _current;
    private readonly List<byte> _output = [];

    internal StringRecordingSink(int extraCapacity = 0) {
        _extraCapacity = extraCapacity;
    }

    internal List<int> SizeHints { get; } = [];
    internal int AdvanceCalls { get; private set; }
    internal int? ThrowOnGetSpanCall { get; set; }
    internal int? ThrowOnAdvanceCall { get; set; }
    internal StringSinkException Failure { get; } = new();
    internal byte[] WrittenBytes {
        get {
            return _output.ToArray();
        }
    }

    public void Advance(int count) {
        AdvanceCalls++;
        if (AdvanceCalls == ThrowOnAdvanceCall) {
            throw Failure;
        }
        if (_current is null || count < 0 || count > _current.Length) {
            throw new InvalidOperationException("Advance requires the current sink buffer.");
        }
        _output.AddRange(_current.AsSpan(0, count).ToArray());
        // Retire and overwrite the block: subsequent requests are independent.
        _current.AsSpan().Fill(0xCC);
        _current = null;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        return GetBuffer(sizeHint);
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        return GetBuffer(sizeHint);
    }

    private byte[] GetBuffer(int sizeHint) {
        SizeHints.Add(sizeHint);
        if (SizeHints.Count == ThrowOnGetSpanCall) {
            throw Failure;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        _current = new byte[Math.Max(1, sizeHint) + _extraCapacity];
        return _current;
    }
}

internal sealed class StringSinkException : Exception { }

internal static class StringTestHelpers {
    internal static byte[] EncodeString(string value, StringRecordingSink? sink = null) {
        sink ??= new StringRecordingSink();
        new BareValueWriter(sink).WriteString(value);
        return sink.WrittenBytes;
    }

    internal static byte[] Prefix(uint header, ReadOnlySpan<byte> payload) {
        var bytes = new List<byte>();
        while (header >= 128) {
            bytes.Add((byte)((header & 0x7F) | 0x80));
            header >>= 7;
        }
        bytes.Add((byte)header);
        bytes.AddRange(payload.ToArray());
        return bytes.ToArray();
    }
}
