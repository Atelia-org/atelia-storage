using System.IO;
using Microsoft.Win32.SafeHandles;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Atelia.Rbf.Internal;

// Mark class as public for BenchmarkDotNet
public class RbfAppendBenchmarks {
    private string? _tempPath;
    private SafeFileHandle? _fileHandle;
    private byte[]? _payload;

    // 定义一个属性来返回需要测试的离散 Payload 长度值
    public IEnumerable<int> PayloadSizes => [2000, 4000, 8000, 16000];

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup() {
        // 2. Prepare payloads
        _payload = new byte[PayloadSize];
        new Random(42).NextBytes(_payload);

        // 3. Prepare file handle (DeleteOnClose)
        _tempPath = Path.Combine(Path.GetTempPath(), "bench-" + Guid.NewGuid());
        _fileHandle = File.OpenHandle(_tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
    }

    [GlobalCleanup]
    public void Cleanup() {
        _fileHandle?.Dispose();
        // File is deleted automatically due to DeleteOnClose
    }

    // Reuse the same file handle and overwrite offset 0 to mock high-throughput appending
    // while avoiding disk space explosion during the benchmark.
    // Real-world scenario would append, but for allocator tuning, overwriting is a valid proxy 
    // for CPU/Memory overhead.

    [Benchmark]
    public void Append() {
        RbfAppendImpl.Append(_fileHandle!, 0, 0x1234, _payload!, out _);
    }
}

// public partial class Program  <-- disable programmatic entry point
// { ... }

// Use top-level statements OR standard Main, but not both mix in a confusing way
// Simplest given the template is using Top Level Statements implicitly sometimes?
// No, the template "console" creates Program.cs with top level statements by default in .NET 6+
// But I wrote a class Program.

// Let's just use standard Main and no top-level statements.
/*
public partial class Program
{
    public static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<RbfAppendBenchmarks>();
    }
}
*/
