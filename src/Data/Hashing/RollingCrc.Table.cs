using System.Numerics;
using System.Runtime.CompilerServices;
namespace Atelia.Data.Hashing;

partial class RollingCrc {
    partial class Table {
        [InlineArray(RowCount * RowSize)]
        private struct RemTable {
            private const int RowShift = 8, RowSize = 1 << RowShift, RowCount = 8;
            private uint _element0;

            internal RemTable(int windowSize) {
                this = default;
                int maxRow = Math.Min(RowCount - 1, windowSize - 1);
                if (maxRow < 0) { return; }

                int minZeroCount = windowSize - maxRow - 1;
                for (int i = 0; i < RowSize; i++) {
                    uint crc = BitOperations.Crc32C(0u, (byte)i);
                    crc = CrcZeroBytes(crc, minZeroCount);

                    int rowOffset = maxRow << RowShift;
                    this[rowOffset + i] = crc;
                    for (int row = maxRow; --row >= 0;) {
                        crc = BitOperations.Crc32C(crc, (byte)0);
                        rowOffset -= RowSize;
                        this[rowOffset + i] = crc;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal readonly uint _RollOut8(uint crc, byte outgoing) => crc ^ this[outgoing];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal readonly uint _RollOut16(uint crc, ushort outgoing) => crc ^ this[0 * RowSize + (outgoing & 0xFF)] ^ this[1 * RowSize + (outgoing >> 8)];

            // private uint _RollOut(uint crc, uint outgoing) => crc ^ _remTbl[0*RemTableRowSize + (byte)outgoing] ^ _remTbl[1*RemTableRowSize + (byte)(outgoing >> 8)] ^ _remTbl[2*RemTableRowSize + (byte)(outgoing >> 16)] ^ _remTbl[3*RemTableRowSize + (byte)(outgoing >> 24)]; // RollOutUint:1653 MB/s, RollUint:1243 MB/s
            // private uint _RollOut(uint crc, uint outgoing) => crc ^ _remTbl[0*RemTableRowSize + (int)(outgoing & 0xFF)] ^ _remTbl[1*RemTableRowSize + (int)((outgoing >> 8) & 0xFF)] ^ _remTbl[2*RemTableRowSize + (int)((outgoing >> 16) & 0xFF)] ^ _remTbl[3*RemTableRowSize + (int)(outgoing >> 24)]; // RollOutUint:1721 MB/s, RollUint:1301 MB/s
            // private uint _RollOut(uint crc, uint outgoing) => _remTbl[0*RemTableRowSize + ((int)outgoing & 0xFF)] ^ _remTbl[1*RemTableRowSize + (((int)outgoing >> 8) & 0xFF)] ^ _remTbl[2*RemTableRowSize + (((int)outgoing >> 16) & 0xFF)] ^ _remTbl[3*RemTableRowSize + (((int)outgoing >> 24) & 0xFF)] ^ crc; // RollOutUint:1809 MB/s, RollUint:1329 MB/s
            // private uint _RollOut(uint crc, uint outgoing) => crc ^ _remTbl[0*RemTableRowSize + ((int)outgoing & 0xFF)] ^ _remTbl[1*RemTableRowSize + (((int)outgoing >> 8) & 0xFF)] ^ _remTbl[2*RemTableRowSize + (((int)outgoing >> 16) & 0xFF)] ^ _remTbl[3*RemTableRowSize + (int)(outgoing >> 24)]; // RollOutUint:1826 MB/s, RollUint:1370 MB/s
            // private uint _RollOut(uint crc, int outgoing) => crc ^ _remTbl[0*RemTableRowSize + (outgoing & 0xFF)] ^ _remTbl[1*RemTableRowSize + ((outgoing >> 8) & 0xFF)] ^ _remTbl[2*RemTableRowSize + ((outgoing >> 16) & 0xFF)] ^ _remTbl[3*RemTableRowSize + ((outgoing >> 24) & 0xFF)]; // RollOutUint:1885 MB/s, RollUint:1372 MB/s
            // private uint _RollOut(uint crc, uint outgoing) => crc ^ _remTbl[((int)outgoing & 0xFF) + 0*RemTableRowSize] ^ _remTbl[(((int)outgoing >> 8) & 0xFF) + 1*RemTableRowSize] ^ _remTbl[(((int)outgoing >> 16) & 0xFF) + 2*RemTableRowSize] ^ _remTbl[(((int)outgoing >> 24) & 0xFF) + 3*RemTableRowSize]; // RollOutUint:1836 MB/s, RollUint:1375 MB/s
            // private uint _RollOut32(uint crc, uint outgoing) => crc ^ _remTbl[0*RemTableRowSize + ((int)outgoing & 0xFF)] ^ _remTbl[1*RemTableRowSize + (((int)outgoing >> 8) & 0xFF)] ^ _remTbl[2*RemTableRowSize + (((int)outgoing >> 16) & 0xFF)] ^ _remTbl[3*RemTableRowSize + (((int)outgoing >> 24) & 0xFF)]; // RollOutUint:1846 MB/s, RollUint:1380 MB/s
            // internal uint _RollOut32(uint crc, uint outgoing) => crc ^ this[0*RemTableRowSize + ((int)outgoing & 0xFF)] ^ this[1*RemTableRowSize + (((int)outgoing >> 8) & 0xFF)] ^ this[2*RemTableRowSize + (((int)outgoing >> 16) & 0xFF)] ^ this[3*RemTableRowSize + (((int)outgoing >> 24) & 0xFF)]; // RollOutUint:10 MB/s, RollUint:10 MB/s
            // internal readonly uint _RollOut32(uint crc, uint outgoing) => crc ^ this[0*RowSize + ((int)outgoing & 0xFF)] ^ this[1*RowSize + (((int)outgoing >> 8) & 0xFF)] ^ this[2*RowSize + (((int)outgoing >> 16) & 0xFF)] ^ this[3*RowSize + (((int)outgoing >> 24) & 0xFF)]; // RollOutUint:1880 MB/s, RollUint:1419 MB/s
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal readonly uint _RollOut32(uint crc, int low, int high) => crc ^ this[0 * RowSize + (low & 0xFF)] ^ this[1 * RowSize + ((low >> 8) & 0xFF)] ^ this[2 * RowSize + (high & 0xFF)] ^ this[3 * RowSize + ((high >> 8) & 0xFF)]; // RollOutUint:1953 MB/s, RollUint:1425 MB/s

            // RollOutUint:2251 MB/s, RollUint:1829 MB/s
            // [MethodImpl(MethodImplOptions.AggressiveInlining)]
            // internal readonly uint _RollOut64A(uint crc, int low, int high) => crc ^ this[0*RowSize + (low & 0xFF)] ^ this[1*RowSize + ((low >> 8) & 0xFF)] ^ this[2*RowSize + ((low >> 16) & 0xFF)] ^ this[3*RowSize + ((low >> 24) & 0xFF)] ^ this[4*RowSize + (high & 0xFF)] ^ this[5*RowSize + ((high >> 8) & 0xFF)] ^ this[6*RowSize + ((high >> 16) & 0xFF)] ^ this[7*RowSize + ((high >> 24) & 0xFF)];

            // RollOutUlong:2287 MB/s, RollUlong:1912 MB/s
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal readonly uint _RollOut64B(uint crc, int low, int high) {
                int p1 = low >> 16, p3 = high >> 16;
                return crc ^ this[0 * RowSize + (low & 0xFF)] ^ this[1 * RowSize + ((low >> 8) & 0xFF)] ^ this[2 * RowSize + (p1 & 0xFF)] ^ this[3 * RowSize + ((p1 >> 8) & 0xFF)] ^ this[4 * RowSize + (high & 0xFF)] ^ this[5 * RowSize + ((high >> 8) & 0xFF)] ^ this[6 * RowSize + (p3 & 0xFF)] ^ this[7 * RowSize + ((p3 >> 8) & 0xFF)];
            }

            // RollOutUlong:2259 MB/s, RollUlong:1851 MB/s
            // [MethodImpl(MethodImplOptions.AggressiveInlining)]
            // internal readonly uint _RollOut64C(uint crc, int p0, int p1, int p2, int p3) => crc ^ this[0*RowSize + (p0 & 0xFF)] ^ this[1*RowSize + ((p0 >> 8) & 0xFF)] ^ this[2*RowSize + (p1 & 0xFF)] ^ this[3*RowSize + ((p1 >> 8) & 0xFF)] ^ this[4*RowSize + (p2 & 0xFF)] ^ this[5*RowSize + ((p2 >> 8) & 0xFF)] ^ this[6*RowSize + (p3 & 0xFF)] ^ this[7*RowSize + ((p3 >> 8) & 0xFF)];
        }

        private readonly RemTable _remTbl = new(windowSize);
        private readonly uint _initAndFinalEffect = CrcZeroBytes(initValue, windowSize) ^ finalXor;

        private static uint CrcZeroBytes(uint crc, int zeroCount) {
            int remain = zeroCount;
            while (remain >= sizeof(ulong)) {
                remain -= sizeof(ulong);
                crc = BitOperations.Crc32C(crc, (ulong)0);
            }
            if (remain >= sizeof(uint)) {
                remain -= sizeof(uint);
                crc = BitOperations.Crc32C(crc, (uint)0);
            }
            if (remain >= sizeof(ushort)) {
                remain -= sizeof(ushort);
                crc = BitOperations.Crc32C(crc, (ushort)0);
            }
            if (remain > 0) {
                // remain -= sizeof(byte);
                crc = BitOperations.Crc32C(crc, (byte)0);
            }
            return crc;
        }

        // internal uint RollOutA(uint crc, ulong outgoing) => _remTbl._RollOut64A(crc, (int)outgoing, (int)(outgoing >> 32));
        // internal uint RollA(uint crc, ulong outgoing, ulong incoming) => BitOperations.Crc32C(_remTbl._RollOut64A(crc, (int)outgoing, (int)(outgoing >> 32)), incoming);

        // internal uint RollOutB(uint crc, ulong outgoing) => _remTbl._RollOut64B(crc, (int)outgoing, (int)(outgoing >> 32));
        // internal uint RollB(uint crc, ulong outgoing, ulong incoming) => BitOperations.Crc32C(_remTbl._RollOut64B(crc, (int)outgoing, (int)(outgoing >> 32)), incoming);

        // internal uint RollOutC(uint crc, ulong outgoing) => _remTbl._RollOut64C(crc, (int)outgoing, (int)outgoing>>16, (int)(outgoing >> 32), (int)(outgoing >> 48));
        // internal uint RollC(uint crc, ulong outgoing, ulong incoming) => BitOperations.Crc32C(_remTbl._RollOut64C(crc, (int)outgoing, (int)outgoing>>16, (int)(outgoing >> 32), (int)(outgoing >> 48)), incoming);
    }
}
