using System.Numerics;
using System.Buffers.Binary;
using System.Diagnostics;
namespace Atelia.Data.Hashing;

partial class RollingCrc {
    public interface IDirection {
        abstract static int _AlignedNextCursor(int cursor, int stepSz, int winSz);

        abstract static ushort OrderBytes(ushort incoming);
        abstract static uint OrderBytes(uint incoming);
        abstract static ulong OrderBytes(ulong incoming);

        abstract static int ForInit(int length);
        abstract static bool ForCond(ref int i, int length);
        abstract static ReadOnlySpan<T> RemainPart<T>(ReadOnlySpan<T> chunk, int iUsed);

        abstract static bool IsBackward { get; }
    }

    partial struct Forward {
        public static int _AlignedNextCursor(int cursor, int stepSz, int winSz) {
            AssertCursorAligned(cursor, stepSz, winSz);
            return (cursor += stepSz) < winSz ? cursor : 0;
        }
        public static ushort OrderBytes(ushort incoming) => incoming;
        public static uint OrderBytes(uint incoming) => incoming;
        public static ulong OrderBytes(ulong incoming) => incoming;

        public static int ForInit(int length) => -1;
        public static bool ForCond(ref int i, int length) => ++i < length;
        public static ReadOnlySpan<T> RemainPart<T>(ReadOnlySpan<T> chunk, int iUsed) => chunk[(iUsed + 1)..];
        public static bool IsBackward => false;
    }

    partial struct Backward : IDirection {
        public static int _AlignedNextCursor(int cursor, int stepSz, int winSz) {
            AssertCursorAligned(cursor, stepSz, winSz);
            return (cursor -= stepSz) < 0 ? winSz - stepSz : cursor;
        }
        public static ushort OrderBytes(ushort incoming) => BinaryPrimitives.ReverseEndianness(incoming);
        public static uint OrderBytes(uint incoming) => BinaryPrimitives.ReverseEndianness(incoming);
        public static ulong OrderBytes(ulong incoming) => BinaryPrimitives.ReverseEndianness(incoming);

        public static int ForInit(int length) => length;
        public static bool ForCond(ref int i, int length) => --i >= 0;
        public static ReadOnlySpan<T> RemainPart<T>(ReadOnlySpan<T> chunk, int iUsed) => chunk[..iUsed];
        public static bool IsBackward => true;
    }

    partial class Scanner<D> {
        // Allows shifting head/tail data without full O(N) rotation.
        const int AlignmentRoom = sizeof(ulong);

        Table _table = table;
        uint _rollingRaw;
        long _processed;

        byte[] _buffer = new byte[table.WindowSize + AlignmentRoom];
        int _ringBase;
        private Span<byte> RingBuffer => _buffer.AsSpan(_ringBase, _table.WindowSize);
        int _ringCursor;

        public uint Roll(byte incoming) {
            ++_processed;
            Span<byte> ringBuffer = RingBuffer;
            int cursor = _ringCursor;
            _ringCursor = D._AlignedNextCursor(cursor, 1, _table.WindowSize);
            _rollingRaw = _table.Roll(_rollingRaw, ringBuffer[cursor], incoming);
            ringBuffer[cursor] = incoming;
            return _rollingRaw;
        }
        public bool RollingCheck(byte incoming, uint destFinalCrc) => _table.Check(Roll(incoming), destFinalCrc) && IsFilled;
        public bool RollingCheck(byte incoming) => _table.CheckResidue(Roll(incoming)) && IsFilled;

        internal void EnsureAligned(int stepSz) {
            Debug.Assert(BitOperations.IsPow2(stepSz));
            if ((_table.WindowSize & (stepSz - 1)) != 0) { throw new InvalidOperationException($"WindowSize must be a multiple of step {stepSz}."); }
            if ((_ringCursor & (stepSz - 1)) != 0) {
                AlignCursor(stepSz);
            }
        }
        private Span<byte> _AlignedConsumeSlot(int stepSz) {
            _processed += stepSz;
            int cursor = _ringCursor;
            _ringCursor = D._AlignedNextCursor(cursor, stepSz, _table.WindowSize);
            return RingBuffer.Slice(cursor, stepSz);
        }

        private uint _Roll2B(ushort incoming) {
            var slot = _AlignedConsumeSlot(sizeof(ushort));
            _rollingRaw = _table.Roll(_rollingRaw, BinaryPrimitives.ReadUInt16LittleEndian(slot), D.OrderBytes(incoming));
            BinaryPrimitives.WriteUInt16LittleEndian(slot, incoming);
            return _rollingRaw;
        }
        private bool _RollingCheck2B(ushort incoming, uint destFinalCrc) => _table.Check(_Roll2B(incoming), destFinalCrc) && IsFilled;
        private bool _RollingCheck2B(ushort incoming) => _table.CheckResidue(_Roll2B(incoming)) && IsFilled;

        private uint _Roll4B(uint incoming) {
            var slot = _AlignedConsumeSlot(sizeof(uint));
            _rollingRaw = _table.Roll(_rollingRaw, BinaryPrimitives.ReadUInt32LittleEndian(slot), D.OrderBytes(incoming));
            BinaryPrimitives.WriteUInt32LittleEndian(slot, incoming);
            return _rollingRaw;
        }
        private bool _RollingCheck4B(uint incoming, uint destFinalCrc) => _table.Check(_Roll4B(incoming), destFinalCrc) && IsFilled;
        private bool _RollingCheck4B(uint incoming) => _table.CheckResidue(_Roll4B(incoming)) && IsFilled;

        private uint _Roll8B(ulong incoming) {
            var slot = _AlignedConsumeSlot(sizeof(ulong));
            _rollingRaw = _table.Roll(_rollingRaw, BinaryPrimitives.ReadUInt64LittleEndian(slot), D.OrderBytes(incoming));
            BinaryPrimitives.WriteUInt64LittleEndian(slot, incoming);
            return _rollingRaw;
        }
        private bool _RollingCheck8B(ulong incoming, uint destFinalCrc) => _table.Check(_Roll8B(incoming), destFinalCrc) && IsFilled;
        private bool _RollingCheck8B(ulong incoming) => _table.CheckResidue(_Roll8B(incoming)) && IsFilled;

        public partial ReadOnlySpan<byte> BorrowBufferView() {
            var span = RingBuffer;
            int cursor = _ringCursor;
            if (cursor > 0) {
                // 三次翻转法 (Three Reversal Algorithm) 实现原地旋转 (In-Place Rotation)
                span[..cursor].Reverse();
                span[cursor..].Reverse();
                span.Reverse();
                _ringCursor = 0;
            }
            return span;
        }

        internal void AlignCursor(int stepSz) {
            Debug.Assert(BitOperations.IsPow2(stepSz));
            Debug.Assert(stepSz <= AlignmentRoom, "stepSz must not exceed AlignmentPadding");

            int cursor = _ringCursor;
            int mask = stepSz - 1;
            int alignDown = cursor & mask;
            if (alignDown == 0) { return; } // Already aligned

            int winSz = _table.WindowSize;
            Debug.Assert((winSz & mask) == 0, "WindowSize must be a multiple of stepSz");

            if (_ringBase + alignDown <= AlignmentRoom) {
                AlignDown(alignDown, winSz);
            }
            else {
                AlignUp(stepSz - alignDown, winSz);
            }
        }
        private void AlignDown(int n, int winSz) {
            Debug.Assert(0 < n && _ringBase + n <= AlignmentRoom);
            var src = _buffer.AsSpan(_ringBase, n);
            var dst = _buffer.AsSpan(_ringBase + winSz, n);
            src.CopyTo(dst);
            _ringBase += n;
            _ringCursor -= n;
        }
        private void AlignUp(int n, int winSz) {
            Debug.Assert(0 < n && 0 <= _ringBase - n);
            var src = _buffer.AsSpan(_ringBase + winSz - n, n);
            var dst = _buffer.AsSpan(_ringBase - n, n);
            src.CopyTo(dst);
            _ringBase -= n;
            if ((_ringCursor += n) >= winSz) { _ringCursor -= winSz; }
        }

        public partial bool TryCopyTo(Span<byte> destBuffer) {
            int windowSize = _table.WindowSize;
            if (destBuffer.Length < windowSize) { return false; }

            var ringBuffer = RingBuffer;
            int cursor = _ringCursor;
            ringBuffer[cursor..].CopyTo(destBuffer);
            if (cursor > 0) {
                int tailLength = windowSize - cursor;
                ringBuffer[..cursor].CopyTo(destBuffer[tailLength..]);
            }
            return true;
        }

        public partial void Reset(Table? newTable) {
            if (newTable is not null) {
                _table = newTable;
            }
            _rollingRaw = RollingCrc.EmptyRollingRaw;
            _processed = 0;

            int requiredLength = _table.WindowSize + AlignmentRoom;
            if (_buffer.Length != requiredLength) { _buffer = new byte[requiredLength]; }
            else { Array.Clear(_buffer); }
            _ringBase = 0;
            _ringCursor = 0;

            _isAtMatch = false;
        }
    }
}

partial class RollingCrc {
    partial class Scanner<D> {
        bool _isAtMatch;

        public partial bool TryFindCodeword(ReadOnlySpan<byte> remainChunk, out CodewordMatch<byte> match) {
            for (int i = D.ForInit(remainChunk.Length); D.ForCond(ref i, remainChunk.Length);) {
                _isAtMatch = RollingCheck(remainChunk[i]);
                if (_isAtMatch) {
                    match = new(D.RemainPart(remainChunk, i), Processed, BorrowBufferView(), D.IsBackward);
                    return true;
                }
            }
            match = default;
            return false;
        }

        public partial bool TryFindCodeword(ReadOnlySpan<ushort> remainChunk, out CodewordMatch<ushort> match) {
            EnsureAligned(sizeof(ushort));
            for (int i = D.ForInit(remainChunk.Length); D.ForCond(ref i, remainChunk.Length);) {
                _isAtMatch = _RollingCheck2B(remainChunk[i]);
                if (_isAtMatch) {
                    match = new(D.RemainPart(remainChunk, i), Processed, BorrowBufferView(), D.IsBackward);
                    return true;
                }
            }
            match = default;
            return false;
        }

        public partial bool TryFindCodeword(ReadOnlySpan<uint> remainChunk, out CodewordMatch<uint> match) {
            EnsureAligned(sizeof(uint));
            for (int i = D.ForInit(remainChunk.Length); D.ForCond(ref i, remainChunk.Length);) {
                _isAtMatch = _RollingCheck4B(remainChunk[i]);
                if (_isAtMatch) {
                    match = new(D.RemainPart(remainChunk, i), Processed, BorrowBufferView(), D.IsBackward);
                    return true;
                }
            }
            match = default;
            return false;
        }

        public partial bool TryFindCodeword(ReadOnlySpan<ulong> remainChunk, out CodewordMatch<ulong> match) {
            EnsureAligned(sizeof(ulong));
            for (int i = D.ForInit(remainChunk.Length); D.ForCond(ref i, remainChunk.Length);) {
                _isAtMatch = _RollingCheck8B(remainChunk[i]);
                if (_isAtMatch) {
                    match = new(D.RemainPart(remainChunk, i), Processed, BorrowBufferView(), D.IsBackward);
                    return true;
                }
            }
            match = default;
            return false;
        }
    }
}
