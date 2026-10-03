using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Atelia.Data;

var rows = new List<object>();
foreach (bool failDuringCommit in new[] { false, true }) {
    var pool = new AcceptThenThrowPool();
    var sink = new CountingSink();
    var writer = new SinkReservableWriter(sink, pool);
    _ = writer.ReserveSpan(4, out int token);
    writer.GetSpan(8192)[..8192].Fill(0x5A);
    writer.Advance(8192);
    Require(pool.RentCount == 2 && sink.PushCalls == 0, "Expected two owned chunks behind the head reservation.");
    Exception? failure = null;
    try {
        if (failDuringCommit) { writer.Commit(token); }
        else { writer.Reset(); }
    }
    catch (InjectedAfterAcceptException ex) { failure = ex; }
    Require(failure is not null, "Injected Return failure was not observed.");
    int callsBeforeDispose = pool.ReturnIds.Count;
    writer.Dispose(); // Closing only; no caller retries writing or explicitly retries Reset.
    Require(pool.ReturnIds.Count > callsBeforeDispose, "Dispose should expose the repeated Return in the current implementation.");
    Require(pool.ReturnIds.Distinct().Count() < pool.ReturnIds.Count, "No duplicate Return observed.");
    rows.Add(new {
        Case = failDuringCommit ? "commit-recycle-return-failure-then-dispose" : "reset-return-failure-then-dispose",
        pool.RentCount, sink.PushCalls, sink.PushedBytes,
        InjectedException = failure!.GetType().Name,
        ReturnIds = pool.ReturnIds.ToArray(),
        DuplicateReturns = pool.ReturnIds.Count - pool.ReturnIds.Distinct().Count(),
        Scope = "Public Data writer with a custom pool that records acceptance before one injected Return exception; not actual SharedArrayPool or actual OOM. Caller only disposes after failure."
    });
}
Console.WriteLine(JsonSerializer.Serialize(new {
    TargetCommit = "0999851207978fe947ecabccb1093e5774d007df",
    Runtime = RuntimeInformation.FrameworkDescription,
    TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    Temp = Path.GetTempPath(),
    Rows = rows
}, new JsonSerializerOptions { WriteIndented = true }));

static void Require(bool condition, string message) {
    if (!condition) { throw new InvalidOperationException(message); }
}

sealed class CountingSink : IByteSink {
    public int PushCalls { get; private set; }
    public long PushedBytes { get; private set; }
    public void Push(ReadOnlySpan<byte> data) { PushCalls++; PushedBytes += data.Length; }
}

sealed class AcceptThenThrowPool : ArrayPool<byte> {
    private readonly Dictionary<byte[], int> _ids = new(ReferenceEqualityComparer.Instance);
    public int RentCount { get; private set; }
    public List<int> ReturnIds { get; } = new();
    public override byte[] Rent(int minimumLength) {
        var buffer = new byte[minimumLength];
        _ids.Add(buffer, ++RentCount);
        return buffer;
    }
    public override void Return(byte[] array, bool clearArray = false) {
        ReturnIds.Add(_ids[array]); // Model a pool accepting ownership before its own failure.
        if (ReturnIds.Count == 2) { throw new InjectedAfterAcceptException(); }
    }
}

sealed class InjectedAfterAcceptException : Exception { }
