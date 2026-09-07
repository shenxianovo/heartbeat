using System.Diagnostics;
using System.Text;

namespace Heartbeat.Verification;

internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly StreamWriter _log;
    private readonly SemaphoreSlim _logGate = new(1);
    private readonly StringBuilder _output = new();
    private readonly string? _serviceState;
    private bool _disposed;
    public string Name { get; }
    public int Id => _process.Id;
    public string Output => _output.ToString();

    private OwnedProcess(string name, ProcessStartInfo start, string logPath, SecretRedactor redactor,
        string? serviceState)
    {
        Name = name;
        _serviceState = serviceState;
        _log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        try { _process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {name}."); }
        catch { _log.Dispose(); throw; }
        _stdout = PumpAsync(_process.StandardOutput, capture: true);
        _stderr = PumpAsync(_process.StandardError, capture: false);

        async Task PumpAsync(StreamReader reader, bool capture)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var safe = redactor.Redact(line);
                if (capture && _output.Length < 1_000_000) _output.AppendLine(safe);
                await _logGate.WaitAsync();
                try { await _log.WriteLineAsync(safe); }
                finally { _logGate.Release(); }
            }
        }
    }

    public static OwnedProcess Start(string name, string executable, IEnumerable<string> args,
        string workingDirectory, string logPath, SecretRedactor redactor,
        IReadOnlyDictionary<string, string?>? environment = null, string? serviceState = null)
    {
        var start = new ProcessStartInfo(serviceState == null ? executable : "dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (serviceState != null)
        {
            start.ArgumentList.Add(typeof(OwnedProcess).Assembly.Location);
            start.ArgumentList.Add("__supervise");
            start.ArgumentList.Add(serviceState);
            start.ArgumentList.Add(executable);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment != null)
            foreach (var (key, value) in environment)
                if (value == null) start.Environment.Remove(key);
                else start.Environment[key] = value;
        return new(name, start, logPath, redactor, serviceState);
    }

    public async Task<string> CompleteAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token);
        await Task.WhenAll(_stdout, _stderr).WaitAsync(token);
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"{Name} exited with code {_process.ExitCode}; see its log.");
        return Output.Trim();
    }

    public async Task WaitForServiceAsync(CancellationToken token)
    {
        while (!File.Exists(_serviceState + ".started"))
        {
            ThrowIfExited();
            await Task.Delay(100, token);
        }
        ThrowIfExited();
    }

    public void ThrowIfExited()
    {
        if (_serviceState != null && File.Exists(_serviceState + ".exit"))
            throw new InvalidOperationException($"{Name} exited unexpectedly (code {File.ReadAllText(_serviceState + ".exit")}); see its log.");
        if (_process.HasExited)
            throw new InvalidOperationException($"{Name} process exited unexpectedly (code {_process.ExitCode}); see its log.");
        if (_stdout.IsFaulted || _stderr.IsFaulted)
            throw new IOException($"Could not capture {Name} logs.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited && _serviceState != null)
            {
                try
                {
                    await _process.StandardInput.WriteLineAsync("stop");
                    await _process.StandardInput.FlushAsync();
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                }
                catch (Exception exception) when (exception is IOException or TimeoutException) { }
            }
            if (!_process.HasExited)
            {
                if (_serviceState != null && File.Exists(_serviceState + ".started") && !OperatingSystem.IsWindows())
                    ServiceSupervisor.SignalGroup(_process.Id, 9);
                else _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await _log.DisposeAsync();
            _process.Dispose();
            _logGate.Dispose();
        }
    }
}
