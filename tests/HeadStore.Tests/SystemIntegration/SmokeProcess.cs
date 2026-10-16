using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.HeadStore.Tests.SystemIntegration;

// Owns only the Process it started. Standard input stays open until Kill/Release;
// disposing the pipe first would turn a supposed crash into managed unwinding.
internal sealed class SmokeProcess : IAsyncDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Task _stdout;
    private readonly Task<string> _stderr;
    private readonly ITestOutputHelper _output;
    private readonly string? _evidence;
    private readonly List<string> _transcript = [];
    private bool _killed;

    internal int Id => _process.Id;

    internal SmokeProcess(ITestOutputHelper output, params string[] args) {
        _output = output;
        string assembly = Path.Combine(AppContext.BaseDirectory, "HeadStore.Smoke", "Atelia.HeadStore.Smoke.dll");
        Assert.True(File.Exists(assembly), $"Build the solution first: missing {assembly}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        foreach (string arg in args) { start.ArgumentList.Add(arg); }
        string? evidenceRoot = Environment.GetEnvironmentVariable("HEADSTORE_PROCESS_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrEmpty(evidenceRoot)) {
            Directory.CreateDirectory(evidenceRoot);
            _evidence = Path.Combine(evidenceRoot, $"{Guid.NewGuid():N}.json");
        }
        _process = Process.Start(start) ?? throw new IOException("Smoke process did not start.");
        try {
            _output.WriteLine($"Started PID {Id}: {string.Join(' ', args)}");
            _transcript.Add(JsonSerializer.Serialize(new { kind = "started", pid = Id, args }));
            _stderr = _process.StandardError.ReadToEndAsync();
            _stdout = Pump();
        }
        catch (Exception initial) {
            // A failed constructor never reaches await using at the caller.
            try {
                if (!_process.HasExited) { _process.Kill(); }
                if (!_process.WaitForExit((int)Timeout.TotalMilliseconds)) {
                    throw new TimeoutException("Failed to reap a process after initialization failed.");
                }
            }
            catch (Exception cleanup) { throw new AggregateException(initial, cleanup); }
            finally { _process.Dispose(); }
            throw;
        }
    }

    private async Task Pump() {
        try {
            while (await _process.StandardOutput.ReadLineAsync() is { } line) {
                _transcript.Add(line);
                await _lines.Writer.WriteAsync(line);
            }
            _lines.Writer.TryComplete();
        }
        catch (Exception error) { _lines.Writer.TryComplete(error); throw; }
    }

    internal async Task<JsonElement> Receive() {
        string line;
        try { line = await _lines.Reader.ReadAsync().AsTask().WaitAsync(Timeout); }
        catch (ChannelClosedException) {
            throw new InvalidOperationException($"PID {Id} ended before its message: {await _stderr.WaitAsync(Timeout)}");
        }
        _output.WriteLine($"PID {Id}: {line}");
        using var json = JsonDocument.Parse(line);
        JsonElement result = json.RootElement.Clone();
        Assert.Equal(Id, result.GetProperty("pid").GetInt32());
        return result;
    }

    internal async Task Finish() {
        await _process.WaitForExitAsync().WaitAsync(Timeout);
        await _stdout.WaitAsync(Timeout);
        string error = await _stderr.WaitAsync(Timeout);
        Assert.True(_process.ExitCode == 0, $"PID {Id} exit {_process.ExitCode}: {error}");
        Assert.Equal("", error);
        Assert.False(_lines.Reader.TryRead(out var extra), $"Unexpected message: {extra}");
    }

    internal async Task Release() {
        Assert.False(_process.HasExited);
        await _process.StandardInput.WriteLineAsync("release").WaitAsync(Timeout);
        await _process.StandardInput.FlushAsync().WaitAsync(Timeout);
        var result = await Receive();
        Assert.Equal("completed", result.GetProperty("kind").GetString());
        await Finish();
    }

    internal async Task<string> ExpectFailure() {
        await _process.WaitForExitAsync().WaitAsync(Timeout);
        await _stdout.WaitAsync(Timeout);
        string error = await _stderr.WaitAsync(Timeout);
        Assert.Equal(1, _process.ExitCode);
        Assert.NotEmpty(error);
        Assert.False(_lines.Reader.TryRead(out var extra), $"Failed command emitted success output: {extra}");
        return error;
    }

    internal async Task Kill() {
        Assert.False(_process.HasExited, "The process exited before the requested kill.");
        _process.Kill();
        _killed = true;
        await _process.WaitForExitAsync().WaitAsync(Timeout);
        await _stdout.WaitAsync(Timeout);
        Assert.NotEqual(0, _process.ExitCode);
        Assert.Equal("", await _stderr.WaitAsync(Timeout));
        Assert.False(_lines.Reader.TryRead(out var extra), $"Worker continued after ready: {extra}");
    }

    public async ValueTask DisposeAsync() {
        try {
            if (!_process.HasExited) {
                _process.Kill();
                _killed = true;
                await _process.WaitForExitAsync().WaitAsync(Timeout);
            }
            await _stdout.WaitAsync(Timeout);
            string stderr = await _stderr.WaitAsync(Timeout);
            _output.WriteLine($"Exited PID {Id}: {_process.ExitCode}, killed={_killed}");
            if (_evidence is not null) {
                await File.WriteAllTextAsync(_evidence, JsonSerializer.Serialize(new {
                    pid = Id, killed = _killed, exitCode = _process.ExitCode, stderr, transcript = _transcript
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { _process.Dispose(); }
    }
}
