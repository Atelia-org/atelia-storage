using System.Runtime.InteropServices;
using Atelia.Data.Hashing;
using BenchmarkDotNet.Attributes;

/// <summary>
/// Benchmarks comparing the performance of RollingCrc32C with different step sizes (byte, ushort, uint).
/// </summary>
// [MemoryDiagnoser]
[OperationsPerSecond]
public class RollingCrc32CBenchmarks {
    private RollingCrc.Table _roller = null!;
    private byte[] _data = null!;
    private const int WindowSize = 64;
    public const int DataSize = 1024 * 1024;

    [GlobalSetup]
    public void Setup() {
        _roller = new RollingCrc.Table(WindowSize);
        _data = new byte[DataSize];
        new Random(42).NextBytes(_data);
    }

    [Benchmark()]
    public uint RollByte() {
        uint crc = 0xFFFFFFFF;

        // Initial window fill
        for (int i = 0; i < WindowSize && i < _data.Length; i++) {
            crc = _roller.RollIn(crc, _data[i]);
        }

        // Roll through the rest
        for (int i = WindowSize; i < _data.Length; i++) {
            crc = _roller.Roll(crc, _data[i - WindowSize], _data[i]);
        }

        return crc;
    }

    [Benchmark]
    public uint RollUshort() {
        var typed = MemoryMarshal.Cast<byte, ushort>(_data);
        var typedWinSz = WindowSize / sizeof(ushort);
        var typedLen = typed.Length;
        uint crc = 0xFFFFFFFF;

        for (int i = 0; i < typedWinSz && i < typedLen; ++i) {
            crc = _roller.RollIn(crc, typed[i]);
        }

        for (int inIdx = typedWinSz, outIdx = 0; inIdx < typedLen; ++inIdx, ++outIdx) {
            crc = _roller.Roll(crc, typed[outIdx], typed[inIdx]);
        }

        return crc;
    }

    [Benchmark]
    public uint RollUint() {
        var typed = MemoryMarshal.Cast<byte, uint>(_data);
        var typedWinSz = WindowSize / sizeof(uint);
        var typedLen = typed.Length;
        uint crc = 0xFFFFFFFF;

        for (int i = 0; i < typedWinSz && i < typedLen; ++i) {
            crc = _roller.RollIn(crc, typed[i]);
        }

        for (int inIdx = typedWinSz, outIdx = 0; inIdx < typedLen; ++inIdx, ++outIdx) {
            crc = _roller.Roll(crc, typed[outIdx], typed[inIdx]);
        }

        return crc;
    }

    [Benchmark]
    public uint RollUlong() {
        var typed = MemoryMarshal.Cast<byte, ulong>(_data);
        var typedWinSz = WindowSize / sizeof(ulong);
        var typedLen = typed.Length;
        uint crc = 0xFFFFFFFF;

        for (int i = 0; i < typedWinSz && i < typedLen; ++i) {
            crc = _roller.RollIn(crc, typed[i]);
        }

        for (int inIdx = typedWinSz, outIdx = 0; inIdx < typedLen; ++inIdx, ++outIdx) {
            crc = _roller.Roll(crc, typed[outIdx], typed[inIdx]);
        }

        return crc;
    }

    // [Benchmark]
    // public uint RollUlongA() {
    //     var typed = MemoryMarshal.Cast<byte, ulong>(_data);
    //     var typedWinSz = WindowSize / sizeof(ulong);
    //     var typedLen = typed.Length;
    //     uint crc = 0xFFFFFFFF;

    //     for (int i = 0; i < typedWinSz && i < typedLen; ++i) {
    //         crc = _roller.RollIn(crc, typed[i]);
    //     }

    //     for (int inIdx = typedWinSz, outIdx = 0; inIdx < typedLen; ++inIdx, ++outIdx) {
    //         crc = _roller.RollA(crc, typed[outIdx], typed[inIdx]);
    //     }

    //     return crc;
    // }

    // [Benchmark]
    // public uint RollUlongB() {
    //     var typed = MemoryMarshal.Cast<byte, ulong>(_data);
    //     var typedWinSz = WindowSize / sizeof(ulong);
    //     var typedLen = typed.Length;
    //     uint crc = 0xFFFFFFFF;

    //     for (int i = 0; i < typedWinSz && i < typedLen; ++i) {
    //         crc = _roller.RollIn(crc, typed[i]);
    //     }

    //     for (int inIdx = typedWinSz, outIdx = 0; inIdx < typedLen; ++inIdx, ++outIdx) {
    //         crc = _roller.RollB(crc, typed[outIdx], typed[inIdx]);
    //     }

    //     return crc;
    // }

    // [Benchmark]
    // public uint RollUlongC() {
    //     var typed = MemoryMarshal.Cast<byte, ulong>(_data);
    //     var typedWinSz = WindowSize / sizeof(ulong);
    //     var typedLen = typed.Length;
    //     uint crc = 0xFFFFFFFF;

    //     for (int i = 0; i < typedWinSz && i < typedLen; ++i) {
    //         crc = _roller.RollIn(crc, typed[i]);
    //     }

    //     for (int inIdx = typedWinSz, outIdx = 0; inIdx < typedLen; ++inIdx, ++outIdx) {
    //         crc = _roller.RollC(crc, typed[outIdx], typed[inIdx]);
    //     }

    //     return crc;
    // }

    [Benchmark]
    public uint RollOutByte() {
        var typed = _data;
        var typedLen = typed.Length;
        uint crc = 0x12345678;

        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollOut(crc, typed[i]);
        }

        return crc;
    }

    [Benchmark]
    public uint RollOutUshort() {
        var typed = MemoryMarshal.Cast<byte, ushort>(_data);
        var typedLen = typed.Length;
        uint crc = 0x12345678;

        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollOut(crc, typed[i]);
        }

        return crc;
    }

    [Benchmark]
    public uint RollOutUint() {
        var typed = MemoryMarshal.Cast<byte, uint>(_data);
        var typedLen = typed.Length;
        uint crc = 0x12345678;

        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollOut(crc, typed[i]);
        }

        return crc;
    }

    [Benchmark]
    public uint RollOutUlong() {
        var typed = MemoryMarshal.Cast<byte, ulong>(_data);
        var typedLen = typed.Length;
        uint crc = 0x12345678;

        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollOut(crc, typed[i]);
        }

        return crc;
    }

    // [Benchmark]
    // public uint RollOutUlongA() {
    //     var typed = MemoryMarshal.Cast<byte, ulong>(_data);
    //     var typedLen = typed.Length;
    //     uint crc = 0x12345678;

    //     for (int i = 0; i < typedLen; ++i) {
    //         crc = _roller.RollOutA(crc, typed[i]);
    //     }

    //     return crc;
    // }

    // [Benchmark]
    // public uint RollOutUlongB() {
    //     var typed = MemoryMarshal.Cast<byte, ulong>(_data);
    //     var typedLen = typed.Length;
    //     uint crc = 0x12345678;

    //     for (int i = 0; i < typedLen; ++i) {
    //         crc = _roller.RollOutB(crc, typed[i]);
    //     }

    //     return crc;
    // }

    // [Benchmark]
    // public uint RollOutUlongC() {
    //     var typed = MemoryMarshal.Cast<byte, ulong>(_data);
    //     var typedLen = typed.Length;
    //     uint crc = 0x12345678;

    //     for (int i = 0; i < typedLen; ++i) {
    //         crc = _roller.RollOutC(crc, typed[i]);
    //     }

    //     return crc;
    // }

    [Benchmark]
    public uint RollInByte() {
        uint crc = 0;
        for (int i = 0; i < _data.Length; i++) {
            crc = _roller.RollIn(crc, _data[i]);
        }
        return crc;
    }

    [Benchmark]
    public uint RollInUshort() {
        var typed = MemoryMarshal.Cast<byte, ushort>(_data);
        var typedLen = typed.Length;
        uint crc = 0;
        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollIn(crc, typed[i]);
        }
        return crc;
    }

    [Benchmark]
    public uint RollInUint() {
        var typed = MemoryMarshal.Cast<byte, uint>(_data);
        var typedLen = typed.Length;
        uint crc = 0;
        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollIn(crc, typed[i]);
        }
        return crc;
    }

    [Benchmark]
    public uint RollInUlong() {
        var typed = MemoryMarshal.Cast<byte, ulong>(_data);
        var typedLen = typed.Length;
        uint crc = 0;
        for (int i = 0; i < typedLen; ++i) {
            crc = _roller.RollIn(crc, typed[i]);
        }
        return crc;
    }
}
