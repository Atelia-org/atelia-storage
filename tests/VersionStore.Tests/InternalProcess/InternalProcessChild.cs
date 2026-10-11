using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.VersionStore.Tests.InternalProcess;

// The parent keeps stdin open and kills the parked process before any pipe cleanup.
internal sealed class InternalProcessChild : IAsyncDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Task _stdout;
    private readonly Task<string> _stderr;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _transcript = [];
    private readonly string[] _args;
    private readonly string? _evidenceFile;
    private bool _killed;

    internal int Id => _process.Id;
    internal JsonElement Ready { get; private set; }

    internal InternalProcessChild(ITestOutputHelper output, params string[] args) {
        _output = output;
        _args = args;
        string assembly = typeof(InternalProcessHost).Assembly.Location;
        Assert.True(File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")), "Build the test executable before starting internal process tests.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("v4f-internal");
        foreach (string arg in args) { start.ArgumentList.Add(arg); }
        string? evidence = Environment.GetEnvironmentVariable("VERSIONSTORE_PROCESS_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrEmpty(evidence)) {
            Directory.CreateDirectory(evidence);
            _evidenceFile = Path.Combine(evidence, $"internal-{Guid.NewGuid():N}.json");
        }
        _process = Process.Start(start) ?? throw new IOException("Internal process did not start.");
        try {
            _output.WriteLine($"Internal started PID {Id}: {string.Join(' ', args)}");
            _stderr = _process.StandardError.ReadToEndAsync();
            _stdout = Pump();
        }
        catch (Exception initial) {
            try {
                if (!_process.HasExited) { _process.Kill(); }
                if (!_process.WaitForExit((int)Timeout.TotalMilliseconds)) {
                    throw new TimeoutException("Failed to reap internal process after constructor failure.");
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

    private async Task<JsonElement> Receive(string prefix) {
        string line;
        try { line = await _lines.Reader.ReadAsync().AsTask().WaitAsync(Timeout); }
        catch (ChannelClosedException) {
            throw new InvalidOperationException($"PID {Id} exited before {prefix}: {await _stderr.WaitAsync(Timeout)}");
        }
        _output.WriteLine($"Internal PID {Id}: {line}");
        Assert.StartsWith(prefix + " ", line);
        using var json = JsonDocument.Parse(line[(prefix.Length + 1)..]);
        var message = json.RootElement.Clone();
        Assert.Equal(Id, message.GetProperty("pid").GetInt32());
        Assert.Equal(_args[0], message.GetProperty("command").GetString());
        return message;
    }

    internal async Task<JsonElement> ReceiveReadyAsync() {
        Ready = await Receive("READY");
        Assert.False(_process.HasExited, "READY process exited before the parent requested kill.");
        return Ready;
    }

    internal Task<JsonElement> ReceiveResultAsync() => Receive("RESULT");

    internal async Task FinishAsync() {
        await _process.WaitForExitAsync().WaitAsync(Timeout);
        await _stdout.WaitAsync(Timeout);
        string error = await _stderr.WaitAsync(Timeout);
        Assert.True(_process.ExitCode == 0, $"Internal PID {Id} exit {_process.ExitCode}: {error}");
        Assert.Equal("", error);
        Assert.False(_lines.Reader.TryRead(out var extra), $"Unexpected internal process message: {extra}");
    }

    internal async Task KillAsync() {
        Assert.False(_process.HasExited, "Process exited before its requested kill.");
        _process.Kill();
        _killed = true;
        await _process.WaitForExitAsync().WaitAsync(Timeout);
        await _stdout.WaitAsync(Timeout);
        Assert.NotEqual(0, _process.ExitCode);
        Assert.Equal("", await _stderr.WaitAsync(Timeout));
        Assert.False(_lines.Reader.TryRead(out var extra), $"Parked process continued: {extra}");
    }

    internal static async Task<InternalProcessChild> StartParkedAsync(ITestOutputHelper output, string root, params string[] args) {
        var child = new InternalProcessChild(output, WithRoot(root, args));
        try { await child.ReceiveReadyAsync(); return child; }
        catch { await child.DisposeAsync(); throw; }
    }

    internal static async Task<ProcessRun> RunAsync(ITestOutputHelper output, string root, params string[] args) {
        await using var child = new InternalProcessChild(output, WithRoot(root, args));
        JsonElement json = await child.ReceiveResultAsync();
        await child.FinishAsync();
        return new ProcessRun(child._process.ExitCode, string.Join('\n', child._transcript),
            await child._stderr, json);
    }

    private static string[] WithRoot(string root, string[] args) {
        Assert.NotEmpty(args);
        return [args[0], root, .. args[1..]];
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
            _output.WriteLine($"Internal exited PID {Id}: {_process.ExitCode}, killed={_killed}");
            if (_evidenceFile is not null) {
                await File.WriteAllTextAsync(_evidenceFile, JsonSerializer.Serialize(new {
                    identity = "Internal+Process", pid = Id, args = _args, killed = _killed,
                    exitCode = _process.ExitCode, stderr, transcript = _transcript
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { _process.Dispose(); }
    }
}

internal sealed record ProcessRun(int ExitCode, string Stdout, string Stderr, JsonElement Json);
