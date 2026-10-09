using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.FrameStore;

byte[] golden = Convert.FromHexString("EFCDAB89F0DEBC9A78563412");
if (!FrameAddress.TryRead(golden, out var address)) { throw new InvalidOperationException("Golden decode failed."); }
var pair = new FrameAddress[2];
pair[0] = pair[1] = address;
const int iterations = 200_000;
_ = Measure(address, golden, 10_000); // Warm the codec, equality and measurement method before samples.
var samples = new List<Sample>();
for (int i = 0; i < 3; i++) { samples.Add(Measure(address, golden, iterations)); }
string assemblyPath = typeof(FrameAddress).Assembly.Location;
Console.WriteLine(JsonSerializer.Serialize(new {
    sourceBaseline = "3fa45e3 plus R3 probe",
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
    encodedSize = FrameAddress.EncodedSize,
    managedSize = Unsafe.SizeOf<FrameAddress>(),
    arrayStride = (long)Unsafe.ByteOffset(ref pair[0], ref pair[1]),
    nestedValueSize = Unsafe.SizeOf<Envelope>(),
    containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<FrameAddress>(),
    arrayAllocations = new[] { MeasureArray(1), MeasureArray(1024), MeasureArray(65536) },
    samples,
    interpretation = "Managed layout is a runtime observation, not a wire contract. Each timed iteration performs one TryWrite, TryRead and typed Equals; allocation excludes setup and reporting. Wall time includes tiered JIT/host effects, with no throughput acceptance threshold. Array allocations include CLR array overhead; Envelope is byte + FrameAddress + byte."
}, new JsonSerializerOptions { WriteIndented = true }));

[MethodImpl(MethodImplOptions.NoInlining)]
static Sample Measure(FrameAddress address, byte[] golden, int count) {
    Span<byte> wire = stackalloc byte[FrameAddress.EncodedSize];
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    for (int i = 0; i < count; i++) {
        if (!address.TryWrite(wire) || !FrameAddress.TryRead(wire, out var decoded) || !address.Equals(decoded)) {
            throw new InvalidOperationException("Codec/equality rejected the golden address.");
        }
    }
    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    if (!wire.SequenceEqual(golden)) { throw new InvalidOperationException("Encoded bytes changed."); }
    return new Sample(count, elapsed, bytes);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static object MeasureArray(int count) {
    long before = GC.GetAllocatedBytesForCurrentThread();
    var values = new FrameAddress[count];
    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    GC.KeepAlive(values);
    return new { count, allocatedBytes = bytes };
}

readonly record struct Sample(int Iterations, double ElapsedMilliseconds, long AllocatedBytes);
readonly record struct Envelope(byte Kind, FrameAddress Address, byte Flags);
