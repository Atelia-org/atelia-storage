using System.Buffers;

namespace Atelia.HeadStore.Internal.NamedFrameMap;

// Scalar writers request their maximum width. A small scratch satisfies that hint when the
// exact body has fewer remaining bytes, without allocating a larger final output or copying it.
internal sealed class ExactNamedFrameMapBuffer(int length) : IBufferWriter<byte> {
    private readonly byte[] _body = new byte[length];
    private byte[]? _scratch;
    private int _position;
    private bool _usingScratch;

    public void Advance(int count) {
        if (count < 0 || count > _body.Length - _position
            || (_usingScratch && count > _scratch!.Length)) {
            throw new InvalidOperationException("The NamedFrameMap writer exceeded its measured size.");
        }
        if (_usingScratch) { _scratch.AsSpan(0, count).CopyTo(_body.AsSpan(_position)); }
        _position += count;
        _usingScratch = false;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int requested = Math.Max(sizeHint, 1);
        _usingScratch = requested > _body.Length - _position;
        if (!_usingScratch) { return _body.AsMemory(_position); }
        if (_scratch is null || _scratch.Length < requested) { _scratch = new byte[requested]; }
        return _scratch;
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        return GetMemory(sizeHint).Span;
    }

    internal byte[] TakeCompletedBody() {
        if (_position != _body.Length) {
            throw new InvalidOperationException("The NamedFrameMap writer did not fill its measured size.");
        }
        return _body;
    }
}
