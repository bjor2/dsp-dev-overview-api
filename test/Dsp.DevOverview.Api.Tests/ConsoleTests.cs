using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.Flow;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

public class ConsoleBufferTests
{
    [Fact]
    public void Ring_keeps_the_last_capacity_lines_with_monotonic_seq()
    {
        var b = new ConsoleBuffer("app:api", capacity: 3);
        for (var i = 1; i <= 5; i++) b.Append("stdout", [$"line {i}"], attached: true);

        Assert.Equal([3L, 4L, 5L], b.Since(null).Select(c => c.Seq));
        Assert.Equal(["line 3", "line 4", "line 5"], b.Since(null).Select(c => c.Lines.Single()));
        Assert.Equal([5L], b.Since(4).Select(c => c.Seq));
        Assert.Empty(b.Since(5));
        Assert.Equal((5L, 3), (b.LastSeq, b.Count));
    }

    [Fact]
    public async Task Subscribe_replays_after_last_event_id_then_streams_live()
    {
        var b = new ConsoleBuffer("stub:ticker", 100);
        for (var i = 1; i <= 4; i++) b.Append("stdout", [$"{i}"], true);

        var (replay, live, sub) = b.Subscribe(afterSeq: 2);
        using (sub)
        {
            Assert.Equal([3L, 4L], replay.Select(c => c.Seq));
            b.Append("system", ["Attached"], true);
            var next = await live.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((5L, "system", "Attached"), (next.Seq, next.Stream, next.Lines.Single()));
        }
        Assert.Equal(6, b.Append("stdout", ["after unsubscribe"], true).Seq); // no subscriber left, no throw
    }

    [Theory]
    [InlineData("app:api/stream", "stream", "app:api")]
    [InlineData("app%3Aapi/start", "start", "app:api")]
    [InlineData("docker:dsp-shared-sql-1/restart", "restart", "docker:dsp-shared-sql-1")]
    [InlineData("stub%3Aticker/stop", "stop", "stub:ticker")]
    [InlineData("app:api/nope", "", "app:api/nope")]
    public void Route_split_handles_raw_and_encoded_app_ids(string path, string action, string appId) =>
        Assert.Equal((action, appId), ConsoleEndpoints.Split(path));
}

/// <summary>ConsoleManager against fake sources and a fake port probe: no pwsh, no docker, no real ports.</summary>
public sealed class ConsoleManagerTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "dsp-console-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFactory _factory = new();
    private readonly FakePorts _ports = new();
    private readonly ProjectRegistration _r = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);
    private const string P = "customerproject.dev";

    private ConsoleManager Manager(bool stub = false, ConsoleOptions? options = null) =>
        new(new ProjectStore([new OverviewProject(_r, null, null, null)]), options ?? new ConsoleOptions { StopTimeoutSeconds = 1 }, _logDir, stub,
            _factory, _ports, NullLogger<ConsoleManager>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    private static List<string> Lines(ConsoleBuffer b) => b.Since(null).SelectMany(c => c.Lines).ToList();

    private static async Task<IReadOnlyList<ConsoleChunk>> Replay(ConsoleManager m, string appId, long? after = null)
    {
        var (replay, _, sub, _) = await m.SubscribeAsync(P, appId, after, default);
        sub.Dispose();
        return replay;
    }

    [Fact]
    public async Task Status_lists_apps_docker_containers_and_stub_only_when_enabled()
    {
        var without = await Manager().StatusAsync(P, default);
        Assert.Equal(["app:api", "app:functions", "app:ingress"], without.Where(s => s.Source == "process").Select(s => s.AppId));
        Assert.Contains(without, s => s is { AppId: "docker:dsp-shared-servicebus-1", Source: "docker" });
        Assert.DoesNotContain(without, s => s.AppId == ConsoleManager.StubTickerId);
        Assert.All(without, s => Assert.False(s.Attached));
        Assert.Equal(5011, without.Single(s => s.AppId == "app:api").Port);

        var ex = await Assert.ThrowsAsync<ApiException>(() => Manager().StartAsync(P, "stub:ticker", default));
        Assert.Equal((404, "CONSOLE_APP_NOT_FOUND"), (ex.Status, ex.Code));

        Assert.Contains(await Manager(stub: true).StatusAsync(P, default), s => s is { AppId: "stub:ticker", Source: "stub" });
    }

    [Fact]
    public async Task Start_streams_lines_to_buffer_and_dated_log_file_and_stop_detaches()
    {
        var m = Manager();
        Assert.True((await m.StartAsync(P, "app:functions", default)).Attached);
        var src = _factory.Last;
        src.Emit("stdout", "Executing 'Functions.CustomerProjectQueueHandler'");
        src.Emit("stderr", "warn: something");

        Assert.True(m.IsManagedRunning(P, "app:functions"));
        Assert.True((await m.StartAsync(P, "app:functions", default)).Attached); // idempotent, no second process
        Assert.Single(_factory.Created);

        var chunks = await Replay(m, "app:functions");
        Assert.Equal(["Starting: fake app:functions", "Attached", "Executing 'Functions.CustomerProjectQueueHandler'", "warn: something"],
            chunks.SelectMany(c => c.Lines));
        Assert.Equal(["system", "system", "stdout", "stderr"], chunks.Select(c => c.Stream));
        Assert.All(chunks.Skip(1), c => Assert.True(c.Attached));

        var file = m.DatedLogPath("app:functions", DateTime.Now);
        Assert.EndsWith($"app-functions-{DateTime.Now:yyyyMMdd}.log", file);
        Assert.Contains("stderr warn: something", await File.ReadAllTextAsync(file));

        Assert.False((await m.StopAsync(P, "app:functions", default)).Attached);
        Assert.True(src.Stopped);
        Assert.False(m.IsManagedRunning(P, "app:functions"));
        var last = (await Replay(m, "app:functions")).Last();
        Assert.Equal(("system", "Stopped", false), (last.Stream, last.Lines.Single(), last.Attached));
        Assert.False((await m.StopAsync(P, "app:functions", default)).Attached); // stop when stopped is a no-op
    }

    [Fact]
    public async Task Start_returns_409_APP_PORT_IN_USE_when_the_local_port_already_listens()
    {
        _ports.Listening.Add(5011);
        var m = Manager();
        var ex = await Assert.ThrowsAsync<ApiException>(() => m.StartAsync(P, "app:api", default));
        Assert.Equal((409, "APP_PORT_IN_USE"), (ex.Status, ex.Code));
        Assert.Contains("5011", ex.Message);
        Assert.Empty(_factory.Created);
        Assert.False(m.IsManagedRunning(P, "app:api"));
    }

    [Fact]
    public async Task Restart_emits_Restarting_then_Attached_and_replaces_the_source()
    {
        var m = Manager();
        await m.StartAsync(P, "app:ingress", default);
        var first = _factory.Last;
        var before = (await Replay(m, "app:ingress")).Last().Seq;

        Assert.True((await m.RestartAsync(P, "app:ingress", default)).Attached);

        Assert.True(first.Stopped);
        Assert.NotSame(first, _factory.Last);
        var system = (await Replay(m, "app:ingress", before)).Where(c => c.Stream == "system").Select(c => c.Lines.Single()).ToList();
        Assert.Equal("Restarting…", system.First());
        Assert.Equal("Attached", system.Last());
        Assert.True(m.IsManagedRunning(P, "app:ingress"));
    }

    [Fact]
    public async Task Process_that_exits_on_its_own_is_no_longer_attached()
    {
        var m = Manager();
        await m.StartAsync(P, "app:api", default);
        _factory.Last.Exit(3);
        await Task.Delay(50);
        Assert.False(m.IsManagedRunning(P, "app:api"));
        var last = (await Replay(m, "app:api")).Last();
        Assert.Equal(("Exited with code 3", false), (last.Lines.Single(), last.Attached));
        var status = (await m.StatusAsync(P, default)).Single(s => s.AppId == "app:api");
        Assert.Equal((false, false, 3), (status.Attached, status.Managed, status.ExitCode));
    }

    [Fact]
    public async Task Fallback_tails_app_log_when_not_managed()
    {
        Directory.CreateDirectory(_logDir);
        var log = Path.Combine(_logDir, "app-api.log");
        await File.WriteAllTextAsync(log, "started elsewhere\n");
        var m = Manager();

        var status = (await m.StatusAsync(P, default)).Single(s => s.AppId == "app:api");
        Assert.Equal(("tail", true, false), (status.Source, status.Attached, status.Managed));
        Assert.False(m.IsManagedRunning(P, "app:api")); // Flow attached = overview-managed only

        await File.AppendAllTextAsync(log, "Now listening on: https://localhost:5011\n");
        var lines = await Eventually(async () => (await Replay(m, "app:api")).SelectMany(c => c.Lines).ToList(),
            l => l.Contains("Now listening on: https://localhost:5011"));
        Assert.Contains("started elsewhere", lines);
        Assert.False(File.Exists(m.DatedLogPath("app:api", DateTime.Now)) && File.ReadAllText(m.DatedLogPath("app:api", DateTime.Now)).Contains("started elsewhere"));
    }

    [Fact]
    public async Task Docker_source_accepts_any_shared_stack_container_only()
    {
        var m = Manager();
        Assert.True((await m.StartAsync(P, "docker:dsp-shared-whatever-1", default)).Attached);
        Assert.Equal("dsp-shared-whatever-1", _factory.Last.Target.Container);
        var ex = await Assert.ThrowsAsync<ApiException>(() => m.StartAsync(P, "docker:postgres", default));
        Assert.Equal(404, ex.Status);
        ex = await Assert.ThrowsAsync<ApiException>(() => m.StartAsync(P, "docker:dsp-shared-x;rm", default));
        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task Flow_app_attached_follows_managed_processes()
    {
        var m = Manager();
        await m.StartAsync(P, "app:functions", default);
        var g = FlowGraphBuilder.Build(_r, FlowScope.Local, "LR", SbScope.Local, new Dictionary<string, EntityCounts>(),
            new Dictionary<string, FlowPosition>(), [], id => m.IsManagedRunning(P, id));
        // ADDENDA-008: apps are containers (resources), not nodes; attached lives on the container header.
        Assert.True(g.Containers.Single(c => c.Id == "container:customerproject.func-app").App!.Attached);
        Assert.False(g.Containers.Single(c => c.Id == "container:customerproject.api").App!.Attached);
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "app");
    }

    [Fact]
    public async Task Stub_ticker_emits_lines()
    {
        var real = new ConsoleManager(new ProjectStore([new OverviewProject(_r, null, null, null)]),
            new ConsoleOptions { StubTickerIntervalMs = 20 }, _logDir, true,
            new DefaultConsoleSourceFactory(new ConsoleOptions { StubTickerIntervalMs = 20 }), _ports, NullLogger<ConsoleManager>.Instance);
        await real.StartAsync(P, "stub:ticker", default);
        var ticks = await Eventually(async () => (await Replay(real, "stub:ticker")).Count(c => c.Lines.Single().StartsWith("tick ")), n => n >= 3);
        Assert.True(ticks >= 3);
        await real.DisposeAsync();
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> get, Func<T, bool> ok)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var v = await get();
            if (ok(v) || sw.Elapsed > TimeSpan.FromSeconds(5)) return v;
            await Task.Delay(25);
        }
    }

    private sealed class FakePorts : IPortProbe
    {
        public HashSet<int> Listening { get; } = [];
        public Task<bool> IsListeningAsync(int port, CancellationToken ct) => Task.FromResult(Listening.Contains(port));
    }

    private sealed class FakeFactory : IConsoleSourceFactory
    {
        public List<FakeSource> Created { get; } = [];
        public FakeSource Last => Created[^1];
        public IConsoleSource Create(ConsoleTarget target) { var s = new FakeSource(target); Created.Add(s); return s; }
        public IConsoleSource Tail(string path) => new FileTailSource(path, TimeSpan.FromMilliseconds(20));
    }

    private sealed class FakeSource(ConsoleTarget target) : IConsoleSource
    {
        private readonly TaskCompletionSource<int?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConsoleSink? _sink;
        public ConsoleTarget Target => target;
        public bool Stopped { get; private set; }
        public string Command => $"fake {target.AppId}";
        public int? Pid => 4242;
        public Task<int?> Completion => _done.Task;
        public void Start(ConsoleSink sink) => _sink = sink;
        public void Emit(string stream, string line) => _sink!(stream, line);
        public void Exit(int code) => _done.TrySetResult(code);
        public Task StopAsync(TimeSpan timeout) { Stopped = true; _done.TrySetResult(137); return Task.CompletedTask; }
    }
}

public class ConsoleSourceTests
{
    [Fact]
    public async Task Tcp_port_probe_sees_a_listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { Assert.True(await new TcpPortProbe().IsListeningAsync(port, default)); }
        finally { listener.Stop(); }
        Assert.False(await new TcpPortProbe().IsListeningAsync(port, default));
    }

    [Fact]
    public async Task Process_source_captures_stdout_stderr_and_stop_kills_the_tree()
    {
        var pidFile = Path.GetTempFileName();
        var psi = new ProcessStartInfo("sh");
        foreach (var a in new[] { "-c", $"echo out; echo err >&2; sleep 60 & echo $! > {pidFile}; wait" }) psi.ArgumentList.Add(a);
        var source = new ProcessConsoleSource(psi);
        var lines = new List<(string, string)>();
        source.Start((stream, line) => { lock (lines) lines.Add((stream, line)); });

        var sw = Stopwatch.StartNew();
        while ((new FileInfo(pidFile).Length == 0 || lines.Count < 2) && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        var child = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());

        await source.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(source.Completion.IsCompleted);
        lock (lines) Assert.Equal([("stdout", "out"), ("stderr", "err")], lines.OrderBy(l => l.Item1 == "stderr"));
        await Task.Delay(100);
        Assert.False(Directory.Exists($"/proc/{child}") && !File.ReadAllText($"/proc/{child}/stat").Contains(") Z "), "grandchild sleep survived stop");
        File.Delete(pidFile);
    }

    [Fact]
    public void Pwsh_start_info_sets_launch_env_and_drops_the_overview_urls()
    {
        var script = Path.GetTempFileName();
        try
        {
            var psi = new DefaultConsoleSourceFactory(new ConsoleOptions()).PwshStartInfo(script);
            Assert.Equal("pwsh", psi.FileName);
            Assert.Equal(["-NoProfile", "-File", script], psi.ArgumentList);
            Assert.Equal("1", psi.Environment["DOTNET_USE_POLLING_FILE_WATCHER"]);
            Assert.Equal("false", psi.Environment["DOTNET_hostBuilder__reloadConfigOnChange"]);
            Assert.False(psi.Environment.ContainsKey("ASPNETCORE_URLS"));
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUSTOMERPROJECT_REPO")))
                Assert.Equal("/workspace/dsp/dsp-customerproject-localrun", psi.Environment["CUSTOMERPROJECT_REPO"]);
        }
        finally { File.Delete(script); }

        var ex = Assert.Throws<ApiException>(() => new DefaultConsoleSourceFactory(new ConsoleOptions()).PwshStartInfo("/nope/run-api.linux.ps1"));
        Assert.Equal("RUN_SCRIPT_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void Docker_command_is_configurable()
    {
        var psi = new DefaultConsoleSourceFactory(new ConsoleOptions()).DockerStartInfo("dsp-shared-sql-1");
        Assert.Equal(("sudo", "-n docker logs -f --tail 200 dsp-shared-sql-1"), (psi.FileName, string.Join(' ', psi.ArgumentList)));
        psi = new DefaultConsoleSourceFactory(new ConsoleOptions { DockerLogsCommand = "podman logs -f {name}" }).DockerStartInfo("dsp-shared-sql-1");
        Assert.Equal(("podman", "logs -f dsp-shared-sql-1"), (psi.FileName, string.Join(' ', psi.ArgumentList)));
    }
}

/// <summary>HTTP + SSE with the stub ticker (fast interval): status, start, Last-Event-ID replay, restart, stop, flow attached.</summary>
public sealed class ConsoleApiTests : IClassFixture<ConsoleApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string DataDir = Path.Combine(Path.GetTempPath(), "dsp-console-api-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); // stub is Development-only by default; enabled explicitly here
            builder.UseSetting("Overview:DataDir", DataDir);
            builder.UseSetting("Overview:Console:StubTicker", "true");
            builder.UseSetting("Overview:Console:StubTickerIntervalMs", "50");
            builder.UseSetting("Overview:Console:HeartbeatSeconds", "1");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
        }
    }

    private const string P = "/api/projects/customerproject.dev";
    private readonly HttpClient _http;

    public ConsoleApiTests(Factory factory) => _http = factory.CreateClient();

    /// <summary>Reads SSE until <paramref name="enough"/> holds or the timeout; returns (id, chunk) pairs.</summary>
    private async Task<List<(long Id, JsonElement Chunk)>> ReadSse(string url, long? lastEventId, Func<List<(long Id, JsonElement Chunk)>, bool> enough)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (lastEventId is { } id) req.Headers.Add("Last-Event-ID", id.ToString());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        var events = new List<(long, JsonElement)>();
        long? currentId = null;
        string? eventName = null;
        while (!enough(events) && await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("id: ")) currentId = long.Parse(line[4..]);
            else if (line.StartsWith("event: ")) eventName = line[7..];
            else if (line.StartsWith("data: ") && eventName is null) events.Add((currentId!.Value, JsonDocument.Parse(line[6..]).RootElement.Clone()));
            else if (line == "") (currentId, eventName) = (null, null);
        }
        return events;
    }

    [Fact]
    public async Task Stub_start_stream_replay_restart_stop()
    {
        var status = await _http.GetFromJsonAsync<JsonElement>($"{P}/console");
        Assert.Contains(status.EnumerateArray(), a => a.GetProperty("appId").GetString() == "stub:ticker");

        var start = await _http.PostAsync($"{P}/console/stub:ticker/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.True((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attached").GetBoolean());

        // Encoded app id, first connect: replay from the start and live ticks.
        var first = await ReadSse($"{P}/console/stub%3Aticker/stream", null, e => e.Count >= 5);
        Assert.Equal(1, first[0].Id);
        Assert.Equal("stub:ticker", first[0].Chunk.GetProperty("appId").GetString());
        Assert.Equal(first.Select(e => e.Id), first.Select(e => e.Chunk.GetProperty("seq").GetInt64()));

        // Reconnect with Last-Event-ID: nothing at or before it is replayed.
        var resumeFrom = first[^1].Id;
        var resumed = await ReadSse($"{P}/console/stub:ticker/stream", resumeFrom, e => e.Count >= 2);
        Assert.Equal(resumeFrom + 1, resumed[0].Id);

        var restart = await _http.PostAsync($"{P}/console/stub:ticker/restart", null);
        Assert.True((await restart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attached").GetBoolean());
        var after = await ReadSse($"{P}/console/stub:ticker/stream", resumed[^1].Id,
            e => e.Any(x => x.Chunk.GetProperty("lines")[0].GetString() == "Attached"));
        var system = after.Where(e => e.Chunk.GetProperty("stream").GetString() == "system").Select(e => e.Chunk.GetProperty("lines")[0].GetString()).ToList();
        Assert.Contains("Restarting…", system);
        Assert.True(system.IndexOf("Restarting…") < system.LastIndexOf("Attached"));

        var stop = await _http.PostAsync($"{P}/console/stub:ticker/stop", null);
        Assert.False((await stop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attached").GetBoolean());
    }

    [Fact]
    public async Task Unknown_app_and_route_are_404_envelopes()
    {
        var r = await _http.PostAsync($"{P}/console/app:nope/start", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("CONSOLE_APP_NOT_FOUND", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        r = await _http.GetAsync($"{P}/console/app:api/bogus");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        r = await _http.GetAsync("/api/projects/nope/console");
        Assert.Equal("PROJECT_NOT_FOUND", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Flow_app_containers_are_not_attached_without_managed_processes()
    {
        var g = await _http.GetFromJsonAsync<JsonElement>($"{P}/flow");
        var containers = g.GetProperty("containers").EnumerateArray().ToList();
        Assert.Equal(3, containers.Count);
        Assert.All(containers, c => Assert.False(c.GetProperty("app").GetProperty("attached").GetBoolean()));
    }
}
