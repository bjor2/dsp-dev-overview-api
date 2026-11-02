using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Dsp.DevOverview.Api.AppConsole;

/// <summary>Receives one line of output. stream = stdout | stderr | system.</summary>
public delegate void ConsoleSink(string stream, string line);

/// <summary>What GET /console lists and start can launch. Kind: process | docker | stub.</summary>
public sealed record ConsoleTarget(string AppId, string Name, string Kind, int? Port = null, string? RunScript = null, string? Container = null);

internal static class NotStarted
{
    /// <summary>Completion before Start: pending, so a source attached just before Start already counts as alive.</summary>
    public static Task<int?> Task => new TaskCompletionSource<int?>().Task;
}

/// <summary>A running line producer: a child process tree, a ticker, or a tailed file.</summary>
public interface IConsoleSource
{
    string Command { get; }
    int? Pid { get; }
    /// <summary>Completes with the exit code (null when it had none) once the source has ended and its output is drained.</summary>
    Task<int?> Completion { get; }
    /// <summary>Throws when the source cannot start (missing executable, ...).</summary>
    void Start(ConsoleSink sink);
    Task StopAsync(TimeSpan timeout);
}

public interface IConsoleSourceFactory
{
    IConsoleSource Create(ConsoleTarget target);
    IConsoleSource Tail(string path);
}

public interface IPortProbe
{
    Task<bool> IsListeningAsync(int port, CancellationToken ct);
}

public sealed class TcpPortProbe : IPortProbe
{
    public async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        foreach (var host in new[] { "127.0.0.1", "::1" })
        {
            using var client = new TcpClient(host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
            try { await client.ConnectAsync(host, port, timeout.Token); return true; }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return false;
    }
}

/// <summary>pwsh run scripts and docker logs: a child process with redirected stdout/stderr. Stop kills the whole tree.</summary>
public sealed class ProcessConsoleSource(ProcessStartInfo psi) : IConsoleSource
{
    private Process? _process;
    private Task<int?> _completion = NotStarted.Task;

    public string Command => string.Join(' ', new[] { psi.FileName }.Concat(psi.ArgumentList));
    public int? Pid => _process?.Id;
    public Task<int?> Completion => _completion;

    public void Start(ConsoleSink sink)
    {
        psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sink("stdout", e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) sink("stderr", e.Data); };
        p.Start(); // Win32Exception when the executable is missing
        p.StandardInput.Close(); // no interactive prompts: scripts see EOF
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        _completion = WaitAsync(p);
    }

    private static async Task<int?> WaitAsync(Process p)
    {
        await p.WaitForExitAsync(); // also waits for redirected output to drain
        return p.ExitCode;
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is not { } p) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // exited meanwhile
        await Task.WhenAny(_completion, Task.Delay(timeout));
    }
}

/// <summary>stub:ticker — one line per interval, for demos and UI work without the real apps.</summary>
public sealed class StubTickerSource(TimeSpan interval) : IConsoleSource
{
    private readonly CancellationTokenSource _cts = new();
    private Task<int?> _completion = NotStarted.Task;

    public string Command => $"stub:ticker every {interval.TotalMilliseconds:0} ms";
    public int? Pid => null;
    public Task<int?> Completion => _completion;

    public void Start(ConsoleSink sink) => _completion = RunAsync(sink, _cts.Token);

    private async Task<int?> RunAsync(ConsoleSink sink, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        var n = 0;
        try
        {
            do sink("stdout", $"tick {++n} {DateTimeOffset.UtcNow:O}");
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        return 0;
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        await _cts.CancelAsync();
        await Task.WhenAny(_completion, Task.Delay(timeout));
    }
}

/// <summary>
/// Attach fallback (ADR-004): tail a file the dev redirected a run script into. Polls instead of FileSystemWatcher (the box
/// runs out of inotify instances). Starts at most 256 KB before the end; a truncated file is re-read from the top.
/// </summary>
public sealed class FileTailSource(string path, TimeSpan? pollInterval = null) : IConsoleSource
{
    private const long Backlog = 256 * 1024;
    private readonly CancellationTokenSource _cts = new();
    private Task<int?> _completion = NotStarted.Task;

    public string Command => $"tail {path}";
    public int? Pid => null;
    public Task<int?> Completion => _completion;

    public void Start(ConsoleSink sink) => _completion = RunAsync(sink, _cts.Token);

    private async Task<int?> RunAsync(ConsoleSink sink, CancellationToken ct)
    {
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(500);
        long position = -1;
        var partial = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!File.Exists(path))
                {
                    if (position >= 0) return null; // the file was removed: detach
                }
                else
                {
                    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (position < 0) position = Math.Max(0, fs.Length - Backlog);
                    if (fs.Length < position) { position = 0; partial.Clear(); }
                    if (fs.Length > position)
                    {
                        fs.Seek(position, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                        partial.Append(await reader.ReadToEndAsync(ct));
                        position = fs.Length;
                        var text = partial.ToString();
                        var lastNewline = text.LastIndexOf('\n');
                        if (lastNewline >= 0)
                        {
                            foreach (var line in text[..lastNewline].Split('\n')) sink("stdout", line.TrimEnd('\r'));
                            partial.Clear().Append(text[(lastNewline + 1)..]);
                        }
                    }
                }
                await Task.Delay(poll, ct);
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        await _cts.CancelAsync();
        await Task.WhenAny(_completion, Task.Delay(timeout));
    }
}

/// <summary>Real sources: <c>pwsh -NoProfile -File &lt;runScript&gt;</c>, the docker logs command, the stub ticker.</summary>
public sealed class DefaultConsoleSourceFactory(ConsoleOptions options) : IConsoleSourceFactory
{
    /// <summary>Set by `dotnet run --launch-profile` for the overview itself; a child app inheriting them would bind :5090.</summary>
    private static readonly string[] StrippedEnvironment =
        ["ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "DOTNET_LAUNCH_PROFILE"];

    public IConsoleSource Create(ConsoleTarget target) => target.Kind switch
    {
        "process" => new ProcessConsoleSource(PwshStartInfo(target.RunScript!)),
        "docker" => new ProcessConsoleSource(DockerStartInfo(target.Container!)),
        "stub" => new StubTickerSource(TimeSpan.FromMilliseconds(Math.Max(10, options.StubTickerIntervalMs))),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "unknown console source kind"),
    };

    public IConsoleSource Tail(string path) => new FileTailSource(path);

    public ProcessStartInfo PwshStartInfo(string runScript)
    {
        if (!File.Exists(runScript))
            throw new ApiException(StatusCodes.Status404NotFound, "RUN_SCRIPT_NOT_FOUND", $"Run script not found: {runScript}");
        var psi = new ProcessStartInfo(options.PwshPath) { WorkingDirectory = Path.GetDirectoryName(runScript) ?? "/" };
        foreach (var a in new[] { "-NoProfile", "-File", runScript }) psi.ArgumentList.Add(a);
        foreach (var key in StrippedEnvironment) psi.Environment.Remove(key);
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("CUSTOMERPROJECT_REPO")))
            psi.Environment["CUSTOMERPROJECT_REPO"] = options.CustomerProjectRepo;
        psi.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
        psi.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";
        foreach (var (k, v) in options.Environment) psi.Environment[k] = v;
        return psi;
    }

    public ProcessStartInfo DockerStartInfo(string container)
    {
        var parts = options.DockerLogsCommand.Replace("{name}", container, StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var psi = new ProcessStartInfo(parts[0]);
        foreach (var a in parts[1..]) psi.ArgumentList.Add(a);
        return psi;
    }
}
