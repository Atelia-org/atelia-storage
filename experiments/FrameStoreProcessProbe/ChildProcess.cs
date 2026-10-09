using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Atelia.FrameStoreProcessProbe;

internal sealed class ProcessEvidence {
    public required string Name { get; init; }
    public required string Token { get; init; }
    public required int Pid { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required string StdoutLog { get; init; }
    public required string StderrLog { get; init; }
    public required string StdinLog { get; init; }
    public bool KillIssued { get; set; }
    public int? ExitCode { get; set; }
}

internal sealed class ChildProcess : IAsyncDisposable {
    private readonly Process _process;
    private readonly TimeSpan _timeout;
    private readonly Request _request;
    private readonly Channel<Message> _messages = Channel.CreateUnbounded<Message>();
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly StreamWriter _stdinLog;
    internal ProcessEvidence Evidence { get; }

    private ChildProcess(Process process, Request request, string logs, string name, TimeSpan timeout) {
        _process = process;
        _request = request;
        _timeout = timeout;
        string stem = Path.Combine(logs, name);
        Evidence = new() {
            Name = name, Token = request.Token, Pid = process.Id, StartedUtc = DateTime.UtcNow,
            StdoutLog = stem + ".stdout.jsonl", StderrLog = stem + ".stderr.log", StdinLog = stem + ".stdin.jsonl"
        };
        _stdinLog = new(Evidence.StdinLog, false, new UTF8Encoding(false)) { AutoFlush = true };
        _stdout = PumpOutput();
        _stderr = PumpError();
    }

    internal static async Task<ChildProcess> Start(Request request, string logs, string name, TimeSpan timeout,
        List<ProcessEvidence> evidence) {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("child");
        var process = Process.Start(start) ?? throw new IOException("Child did not start.");
        ChildProcess? child = null;
        try {
            child = new ChildProcess(process, request, logs, name, timeout);
            evidence.Add(child.Evidence);
            await child.Send(request);
            _ = await child.Receive("boot");
            return child;
        }
        catch {
            if (child is not null) { await child.DisposeAsync(); }
            else {
                // Log construction can fail after Start. Retain ownership even before a session exists.
                try {
                    if (!process.HasExited) {
                        process.Kill();
                        await process.WaitForExitAsync().WaitAsync(timeout);
                    }
                }
                finally { process.Dispose(); }
            }
            throw;
        }
    }

    private async Task PumpOutput() {
        await using var log = new StreamWriter(Evidence.StdoutLog, false, new UTF8Encoding(false)) { AutoFlush = true };
        try {
            while (await _process.StandardOutput.ReadLineAsync() is { } line) {
                await log.WriteLineAsync(line);
                var message = JsonSerializer.Deserialize<Message>(line, Protocol.Json)
                    ?? throw new InvalidDataException("Child emitted null JSON.");
                await _messages.Writer.WriteAsync(message);
            }
            _messages.Writer.TryComplete();
        }
        catch (Exception error) { _messages.Writer.TryComplete(error); throw; }
    }

    private async Task PumpError() {
        await using var log = new StreamWriter(Evidence.StderrLog, false, new UTF8Encoding(false)) { AutoFlush = true };
        while (await _process.StandardError.ReadLineAsync() is { } line) { await log.WriteLineAsync(line); }
    }

    private async Task Send(Request request) {
        string line = JsonSerializer.Serialize(request, Protocol.Json);
        await _stdinLog.WriteLineAsync(line);
        await _process.StandardInput.WriteLineAsync(line).WaitAsync(_timeout);
        await _process.StandardInput.FlushAsync().WaitAsync(_timeout);
    }

    internal async Task<Message> Receive(string kind) {
        var message = await _messages.Reader.ReadAsync().AsTask().WaitAsync(_timeout);
        if (message.Pid != Evidence.Pid || message.Token != _request.Token) {
            throw new InvalidDataException("Child PID/token does not match the Process created by this parent.");
        }
        if (message.Kind != kind) {
            throw new InvalidDataException($"Expected {kind}; received {JsonSerializer.Serialize(message, Protocol.Json)}");
        }
        return message;
    }

    internal async Task Finish() {
        await _process.WaitForExitAsync().WaitAsync(_timeout);
        Evidence.ExitCode = _process.ExitCode;
        await Task.WhenAll(_stdout, _stderr).WaitAsync(_timeout);
        if (_process.ExitCode != 0) { throw new IOException($"Child {Evidence.Name} exited with {_process.ExitCode}."); }
        if (_messages.Reader.TryRead(out var extra)) { throw new InvalidDataException($"Unexpected child message: {extra.Kind}"); }
    }

    internal async Task DisposeOwner() {
        await Send(_request with { Operation = "dispose" });
        _ = await Receive("disposed");
        await Finish();
    }

    internal async Task Kill() {
        if (_process.HasExited) { throw new InvalidOperationException("The ready holder exited before requested kill."); }
        Evidence.KillIssued = true;
        // Only this retained Process object is terminated; no PID lookup, process-name selection, or tree kill.
        _process.Kill();
        await _process.WaitForExitAsync().WaitAsync(_timeout);
        Evidence.ExitCode = _process.ExitCode;
        await Task.WhenAll(_stdout, _stderr).WaitAsync(_timeout);
    }

    public async ValueTask DisposeAsync() {
        try {
            if (!_process.HasExited) { await Kill(); }
            else {
                Evidence.ExitCode = _process.ExitCode;
                await Task.WhenAll(_stdout, _stderr).WaitAsync(_timeout);
            }
        }
        finally { _stdinLog.Dispose(); _process.Dispose(); }
    }
}
