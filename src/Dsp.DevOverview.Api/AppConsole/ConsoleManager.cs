using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Dsp.DevOverview.Api.AppConsole;

/// <summary>
/// ADR-004 process manager: one session per (project, app) with a ring buffer, a dated log file and at most one source.
/// Managed sources (pwsh run script, docker logs, stub) are started/stopped by the API; when an app is not managed and
/// {logDir}/&lt;app&gt;.log exists, a tail of that file is attached instead (fallback for runs started elsewhere).
/// </summary>
public sealed partial class ConsoleManager(
    ProjectStore store, ConsoleOptions options, string logDir, bool stubEnabled,
    IConsoleSourceFactory factory, IPortProbe ports, ILogger<ConsoleManager> log) : IConsoleStatusProvider, IAsyncDisposable
{
    public const string StubTickerId = "stub:ticker";
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private TimeSpan StopTimeout => TimeSpan.FromSeconds(Math.Max(1, options.StopTimeoutSeconds));

    /// <summary>"app:api" → "app-api" (file names).</summary>
    public static string SafeName(string appId) => appId.Replace(':', '-').Replace('/', '-');

    public string DatedLogPath(string appId, DateTime localDate) => Path.Combine(logDir, $"{SafeName(appId)}-{localDate:yyyyMMdd}.log");
    public string FallbackLogPath(string appId) => Path.Combine(logDir, $"{SafeName(appId)}.log");

    public IReadOnlyList<ConsoleTarget> Targets(string projectId)
    {
        var p = Project(projectId);
        var list = new List<ConsoleTarget>();
        foreach (var a in p.Registration.Apps)
            if (a.Local?.RunScript is { Length: > 0 } script)
                list.Add(new ConsoleTarget(a.Id, a.Name, "process", a.Local.Port, RunScript: script));
        foreach (var c in options.Containers)
            list.Add(new ConsoleTarget("docker:" + c, c, "docker", Container: c));
        if (stubEnabled) list.Add(new ConsoleTarget(StubTickerId, "Stub ticker", "stub"));
        return list;
    }

    public ConsoleTarget Target(string projectId, string appId)
    {
        var target = Targets(projectId).FirstOrDefault(t => t.AppId == appId);
        if (target is not null) return target;
        // Any shared-stack container can be followed, not only the listed ones.
        if (appId.StartsWith("docker:", StringComparison.Ordinal) && appId["docker:".Length..] is var name
            && name.StartsWith(options.DockerContainerPrefix, StringComparison.Ordinal) && ContainerName().IsMatch(name))
            return new ConsoleTarget(appId, name, "docker", Container: name);
        throw new ApiException(StatusCodes.Status404NotFound, "CONSOLE_APP_NOT_FOUND",
            $"No console app '{appId}' in {projectId}. Known: {string.Join(", ", Targets(projectId).Select(t => t.AppId))}" +
            (appId == StubTickerId ? " (stub:ticker is Development only; set Overview:Console:StubTicker=true)" : ""));
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex ContainerName();

    private OverviewProject Project(string id) => store.Find(id)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "PROJECT_NOT_FOUND", $"No registration with id '{id}'. Known: {string.Join(", ", store.All.Select(p => p.Id))}");

    private Session SessionFor(string projectId, ConsoleTarget target) =>
        _sessions.GetOrAdd($"{projectId}|{target.AppId}", _ => new Session(target, new ConsoleBuffer(target.AppId, options.BufferLines), this));

    // GET /api/projects/{id}/console
    public async Task<IReadOnlyList<ConsoleAppStatus>> StatusAsync(string projectId, CancellationToken ct)
    {
        var list = new List<ConsoleAppStatus>();
        foreach (var target in Targets(projectId))
        {
            var s = SessionFor(projectId, target);
            await EnsureFallbackAsync(s, ct);
            list.Add(s.Status());
        }
        return list;
    }

    public bool IsManagedRunning(string projectId, string appId) =>
        _sessions.TryGetValue($"{projectId}|{appId}", out var s) && s.Managed && s.Alive;

    /// <summary>SSE: replay (seq &gt; afterSeq) + live chunks. Validates before the response starts.</summary>
    public async Task<(IReadOnlyList<ConsoleChunk> Replay, ChannelReader<ConsoleChunk> Live, IDisposable Subscription, Func<bool> Attached)> SubscribeAsync(
        string projectId, string appId, long? afterSeq, CancellationToken ct)
    {
        var s = SessionFor(projectId, Target(projectId, appId));
        await EnsureFallbackAsync(s, ct);
        var (replay, live, sub) = s.Buffer.Subscribe(afterSeq);
        return (replay, live, sub, () => s.Alive);
    }

    // POST …/console/{appId}/start
    public async Task<ConsoleAttachResult> StartAsync(string projectId, string appId, CancellationToken ct)
    {
        var s = SessionFor(projectId, Target(projectId, appId));
        await s.Gate.WaitAsync(ct);
        try { return await StartCoreAsync(s, ct); }
        finally { s.Gate.Release(); }
    }

    // POST …/console/{appId}/stop
    public async Task<ConsoleAttachResult> StopAsync(string projectId, string appId, CancellationToken ct)
    {
        var s = SessionFor(projectId, Target(projectId, appId));
        await s.Gate.WaitAsync(ct);
        try { await StopCoreAsync(s); return new(false); }
        finally { s.Gate.Release(); }
    }

    // POST …/console/{appId}/restart — stop → start under one lock (ADDENDUM-ui-wireframe-q §2).
    public async Task<ConsoleAttachResult> RestartAsync(string projectId, string appId, CancellationToken ct)
    {
        var s = SessionFor(projectId, Target(projectId, appId));
        await s.Gate.WaitAsync(ct);
        try
        {
            s.System("Restarting…");
            await StopCoreAsync(s);
            if (s.Target.Port is { } port)
            {
                // A killed app can hold its port for a moment.
                var deadline = DateTime.UtcNow + StopTimeout;
                while (DateTime.UtcNow < deadline && await ports.IsListeningAsync(port, ct)) await Task.Delay(250, ct);
            }
            return await StartCoreAsync(s, ct);
        }
        finally { s.Gate.Release(); }
    }

    private async Task<ConsoleAttachResult> StartCoreAsync(Session s, CancellationToken ct)
    {
        if (s.Managed && s.Alive) return new(true);
        var t = s.Target;
        if (t.Port is { } port && await ports.IsListeningAsync(port, ct))
            throw new ApiException(StatusCodes.Status409Conflict, "APP_PORT_IN_USE",
                $"{t.Name} port {port} is already listening (started outside the overview?). Stop it first, or run it with " +
                $"`*> {FallbackLogPath(t.AppId)}` so the console can tail it.");
        if (s.Source is { } previous) { await previous.StopAsync(StopTimeout); s.Detach(); } // a tail, or a managed source that exited

        var source = factory.Create(t);
        s.System($"Starting: {source.Command}");
        s.Attach(source, managed: true); // before Start, so the first output line already counts as attached
        try { source.Start(s.Sink); }
        catch (Exception ex)
        {
            s.Detach();
            s.System($"Start failed: {ex.Message}");
            log.LogWarning("Console start {App} failed: {Message}", t.AppId, ex.Message);
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "CONSOLE_START_FAILED", $"{source.Command}: {ex.Message}", retryable: true);
        }
        s.System("Attached");
        _ = source.Completion.ContinueWith(c => s.OnEnded(source, c.IsCompletedSuccessfully ? c.Result : null), TaskScheduler.Default);
        log.LogInformation("Console {App} started: {Command} (pid {Pid})", t.AppId, source.Command, source.Pid);
        return new(true);
    }

    private async Task StopCoreAsync(Session s)
    {
        if (s.Source is not { } source || !s.Alive) { s.Detach(); return; }
        s.System("Stopping…");
        s.Stopping = true;
        await source.StopAsync(StopTimeout);
        s.Detach();
        s.System(source.Completion.IsCompleted
            ? "Stopped"
            : $"Stop timed out after {StopTimeout.TotalSeconds:0}s; process tree (pid {source.Pid}) may still be running");
    }

    /// <summary>Not managed + {app}.log present → tail it (ADR-004 fallback). Only for the app run scripts.</summary>
    private async Task EnsureFallbackAsync(Session s, CancellationToken ct)
    {
        if (s.Target.Kind != "process" || s.Alive || !File.Exists(FallbackLogPath(s.Target.AppId))) return;
        await s.Gate.WaitAsync(ct);
        try
        {
            if (s.Alive) return;
            var path = FallbackLogPath(s.Target.AppId);
            var tail = factory.Tail(path);
            s.Attach(tail, managed: false);
            tail.Start(s.Sink);
            s.System($"Attached (tailing {path}; not started by the overview)");
        }
        finally { s.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
            if (s.Source is { } source)
                try { await source.StopAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) { log.LogWarning("Console shutdown {App}: {Message}", s.Target.AppId, ex.Message); }
    }

    private sealed class Session(ConsoleTarget target, ConsoleBuffer buffer, ConsoleManager owner)
    {
        private readonly object _write = new();
        private StreamWriter? _file;
        private string? _filePath;

        public ConsoleTarget Target => target;
        public ConsoleBuffer Buffer => buffer;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IConsoleSource? Source { get; private set; }
        public bool Managed { get; private set; }
        public bool Stopping { get; set; }
        public DateTimeOffset? StartedAt { get; private set; }
        public int? ExitCode { get; private set; }
        public bool Alive => Source is { } src && !src.Completion.IsCompleted;

        public void Attach(IConsoleSource source, bool managed)
        {
            (Source, Managed, Stopping, StartedAt, ExitCode) = (source, managed, false, DateTimeOffset.UtcNow, null);
        }

        public void Detach() => (Source, Managed) = (null, false);

        public void OnEnded(IConsoleSource source, int? exitCode)
        {
            if (!ReferenceEquals(Source, source)) return;
            ExitCode = exitCode;
            if (!Stopping) System(exitCode is { } c ? $"Exited with code {c}" : "Source ended");
        }

        public void Sink(string stream, string line) => Append(stream, line);

        public void System(string line) => Append("system", line);

        private void Append(string stream, string line)
        {
            lock (_write)
            {
                var chunk = buffer.Append(stream, [line], Alive);
                if (Source is FileTailSource && stream != "system") return; // the tailed file already is the log
                try
                {
                    var path = owner.DatedLogPath(target.AppId, chunk.Timestamp.LocalDateTime);
                    if (path != _filePath)
                    {
                        _file?.Dispose();
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                        _filePath = path;
                    }
                    _file!.WriteLine($"{chunk.Timestamp:O} {stream} {line}");
                }
                catch (IOException) { _filePath = null; } // never lose the in-memory line over a log-file problem
            }
        }

        public ConsoleAppStatus Status()
        {
            var src = Source;
            var kind = src is FileTailSource ? "tail" : target.Kind;
            return new ConsoleAppStatus(target.AppId, target.Name, kind, Alive, Managed && Alive, Alive ? src?.Pid : null, target.Port,
                StartedAt, ExitCode, src?.Command, _filePath, buffer.LastSeq);
        }
    }
}
