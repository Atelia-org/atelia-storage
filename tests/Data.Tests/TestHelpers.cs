using System;
using System.Buffers;
using System.IO;

namespace Atelia.Data.Tests;

/// <summary>
/// 共享测试辅助类
/// </summary>
internal static class TestHelpers {
    /// <summary>
    /// 轻量 writer/sink：追加写入并可读取已写数据
    /// 同时实现 IBufferWriter&lt;byte&gt;（供 ChunkedReservableWriter）和 IByteSink（供 SinkReservableWriter）
    /// </summary>
    internal sealed class CollectingWriter : IBufferWriter<byte>, IByteSink {
        private MemoryStream _stream = new();
        private int _pos;

        // ========== IBufferWriter<byte> ==========
        public void Advance(int count) {
            _pos += count;
            if (_pos > _stream.Length) {
                _stream.SetLength(_pos);
            }
        }
        public Memory<byte> GetMemory(int sizeHint = 0) {
            int need = _pos + Math.Max(sizeHint, 1);
            if (_stream.Length < need) {
                _stream.SetLength(need);
            }

            return _stream.GetBuffer().AsMemory(_pos, (int)_stream.Length - _pos);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        // ========== IByteSink ==========
        public void Push(ReadOnlySpan<byte> data) {
            int need = _pos + data.Length;
            if (_stream.Length < need) {
                _stream.SetLength(need);
            }
            data.CopyTo(_stream.GetBuffer().AsSpan(_pos, data.Length));
            _pos += data.Length;
        }

        // ========== 辅助方法 ==========
        public byte[] Data() {
            var a = new byte[_pos];
            Array.Copy(_stream.GetBuffer(), 0, a, 0, _pos);
            return a;
        }

        public void Reset() {
            _pos = 0;
            _stream.SetLength(0);
        }
    }
}
