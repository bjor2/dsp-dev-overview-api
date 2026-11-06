using System.Text.Json;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.AppInsights;
using Dsp.DevOverview.Api.Health;
using Dsp.DevOverview.Api.Registration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>Slice 7: health rules (pure) and the GET /api/projects aggregation over mocked probes.</summary>
public class HealthRulesTests
{
    [Theory]
    // SB unreachable is red whatever else is true.
    [InlineData(3, 3, false, true, "red")]
    [InlineData(0, 3, false, false, "red")]
    [InlineData(0, 0, false, true, "red")]
    // Some apps down: amber, with or without App Insights.
    [InlineData(2, 3, true, true, "amber")]
    [InlineData(0, 3, true, true, "amber")]
    [InlineData(2, 3, true, false, "amber")]
    // Everything local fine but the App Insights probe has not succeeded: unknown, never green.
    [InlineData(3, 3, true, false, "unknown")]
    [InlineData(0, 0, true, false, "unknown")]
    // All signals ok.
    [InlineData(3, 3, true, true, "green")]
    [InlineData(0, 0, true, true, "green")]
    public void Evaluate(int appsUp, int appsTotal, bool sbReachable, bool aiQueryOk, string expected) =>
        Assert.Equal(expected, HealthRules.Evaluate(new HealthDetail(appsUp, appsTotal, sbReachable, aiQueryOk)));
}

public class ProjectHealthServiceTests
{
    private static OverviewProject Project(string id, int[] ports, string? traceIt = null) => new(new ProjectRegistration
    {
        Id = id, Project = id.Split('.')[0], Env = "dev", GeneratedAt = new DateTimeOffset(2026, 11, 2, 12, 45, 0, TimeSpan.FromHours(1)),
        Apps = ports.Select((port, i) => new RegisteredApp { Id = $"app:a{i}", Name = $"a{i}", Kind = "web", Local = new() { Port = port } }).ToList(),
        Entities =
        [
            new() { Id = "q:one", Kind = "queue", LocalName = "one" },
            new() { Id = "q:two", Kind = "queue", LocalName = "two" },
            new() { Id = "q:cloud-only", Kind = "queue", LocalName = "cloud-only", Scope = "cloud" },
            new() { Id = "t:topic", Kind = "topic", LocalName = "topic" },
            new() { Id = "s:topic/sub", Kind = "subscription", LocalName = "sub", Topic = "t:topic" },
        ],
        Observability = traceIt is null ? null : new() { TraceIt = new() { Url = traceIt } },
    }, Catalog: null, CatalogPath: null, CatalogError: "test");

    private sealed class FakeSb(Dictionary<string, SbHealth> results) : ISbHealthProbe
    {
        public Task<SbHealth> ProbeAsync(OverviewProject project, CancellationToken ct) => Task.FromResult(results[project.Id]);
    }

    private sealed class FakeAi(params string[] okProjects) : IAiQueryProbe
    {
        public bool IsQueryOk(OverviewProject project) => okProjects.Contains(project.Id);
    }

    private sealed class FakeConsole(params string[] managed) : IConsoleStatusProvider
    {
        public bool IsManagedRunning(string projectId, string appId) => managed.Contains($"{projectId}|{appId}");
    }

    private sealed class FakePorts(params int[] listening) : IPortProbe
    {
        public Task<bool> IsListeningAsync(int port, CancellationToken ct) => Task.FromResult(listening.Contains(port));
    }

    private static ProjectHealthService Service(OverviewProject[] projects, Dictionary<string, SbHealth> sb, IAiQueryProbe? ai,
        IConsoleStatusProvider? console, IPortProbe ports, OverviewOptions? options = null) =>
        new(new ProjectStore(projects), new FakeSb(sb), ai, console, ports, options ?? new OverviewOptions());

    [Fact]
    public async Task All_signals_ok_is_green_with_counts_and_traceit_link()
    {
        var p = Project("alpha.dev", [7001, 7002]);
        var s = Service([p], new() { ["alpha.dev"] = new(true, 4) }, new FakeAi("alpha.dev"), new FakeConsole(), new FakePorts(7001, 7002));

        var summary = Assert.Single(await s.SummariesAsync(default));
        Assert.Equal("green", summary.Health);
        Assert.Equal(new HealthDetail(2, 2, true, true), summary.HealthDetail);
        Assert.Equal((2, 2, 4), (summary.AppCount, summary.QueueCount, summary.DlqTotal)); // cloud-only queue not counted locally
        Assert.Equal("2026-11-02T12:45:00+01:00", summary.RegistrationGeneratedAt);
        Assert.Equal("https://traceit-dev.sgdn.no", summary.TraceItUrl);
    }

    [Fact]
    public async Task App_counts_as_up_when_the_console_manages_it_even_without_a_listening_port()
    {
        var p = Project("alpha.dev", [7001, 7002, 7003]);
        var s = Service([p], new() { ["alpha.dev"] = new(true, 0) }, new FakeAi("alpha.dev"),
            new FakeConsole("alpha.dev|app:a0"), new FakePorts(7002));

        var summary = Assert.Single(await s.SummariesAsync(default));
        Assert.Equal(new HealthDetail(2, 3, true, true), summary.HealthDetail);
        Assert.Equal("amber", summary.Health);
    }

    [Fact]
    public async Task Sb_unreachable_is_red_with_unknown_dlq_total()
    {
        var p = Project("alpha.dev", [7001]);
        var s = Service([p], new() { ["alpha.dev"] = new(false, null) }, new FakeAi("alpha.dev"), null, new FakePorts(7001));

        var summary = Assert.Single(await s.SummariesAsync(default));
        Assert.Equal("red", summary.Health);
        Assert.False(summary.HealthDetail.SbReachable);
        Assert.Null(summary.DlqTotal); // unknown, never a fake 0
    }

    [Fact]
    public async Task Missing_ai_probe_is_never_reported_as_ok()
    {
        var p = Project("alpha.dev", [7001]);
        var s = Service([p], new() { ["alpha.dev"] = new(true, 0) }, ai: null, null, new FakePorts(7001));

        var summary = Assert.Single(await s.SummariesAsync(default));
        Assert.False(summary.HealthDetail.AiQueryOk);
        Assert.Equal("unknown", summary.Health);
    }

    [Fact]
    public async Task Projects_are_evaluated_independently()
    {
        var alpha = Project("alpha.dev", [7001]);
        var beta = Project("beta.dev", [8001, 8002], traceIt: "https://traceit.example/beta");
        var gamma = Project("gamma.dev", [9001]);
        var s = Service([alpha, beta, gamma],
            new() { ["alpha.dev"] = new(true, 1), ["beta.dev"] = new(true, 7), ["gamma.dev"] = new(false, 0) },
            new FakeAi("alpha.dev", "beta.dev", "gamma.dev"),
            new FakeConsole("alpha.dev|app:a1", "beta.dev|app:a0"), // alpha has no app:a1; beta's a0 runs managed
            new FakePorts(7001, 9001));

        var byId = (await s.SummariesAsync(default)).ToDictionary(x => x.Id);
        Assert.Equal(("green", 1, 1, 1), (byId["alpha.dev"].Health, byId["alpha.dev"].HealthDetail.AppsUp, byId["alpha.dev"].HealthDetail.AppsTotal, byId["alpha.dev"].DlqTotal));
        Assert.Equal(("amber", 1, 2, 7), (byId["beta.dev"].Health, byId["beta.dev"].HealthDetail.AppsUp, byId["beta.dev"].HealthDetail.AppsTotal, byId["beta.dev"].DlqTotal));
        Assert.Equal("red", byId["gamma.dev"].Health);
        Assert.Equal("https://traceit.example/beta", byId["beta.dev"].TraceItUrl);
        Assert.Equal("https://traceit-dev.sgdn.no", byId["alpha.dev"].TraceItUrl);
    }

    [Fact]
    public void TraceIt_link_registration_wins_then_config_then_none()
    {
        var options = new OverviewOptions { TraceIt = new() { Url = "https://configured.example" } };
        Assert.Equal("https://own.example", ProjectHealthService.TraceItUrl(Project("a.dev", [], "https://own.example"), options));
        Assert.Equal("https://configured.example", ProjectHealthService.TraceItUrl(Project("a.dev", []), options));
        Assert.Null(ProjectHealthService.TraceItUrl(Project("a.dev", []), new OverviewOptions { TraceIt = new() { Url = "" } }));
    }
}

/// <summary>GET /api/projects over HTTP: no Service Bus configured (Production) must give red, never a faked green.</summary>
public sealed class ProjectsApiHealthTests : IClassFixture<ProjectsApiHealthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); // appsettings.json: empty SB connection string → no network
            builder.UseSetting("Overview:AppInsights:ProbeEnabled", "false");
            builder.UseSetting("Overview:DataDir", Directory.CreateTempSubdirectory("dsp-health-").FullName);
        }
    }

    private readonly HttpClient _http;

    public ProjectsApiHealthTests(Factory factory) => _http = factory.CreateClient();

    [Fact]
    public async Task Summary_is_truthful_and_carries_the_traceit_link()
    {
        var root = JsonDocument.Parse(await _http.GetStringAsync("/api/projects")).RootElement;
        var p = root.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "customerproject.dev");
        var detail = p.GetProperty("healthDetail");

        Assert.Equal("red", p.GetProperty("health").GetString());
        Assert.False(detail.GetProperty("sbReachable").GetBoolean());
        Assert.False(detail.GetProperty("aiQueryOk").GetBoolean());
        Assert.Equal(p.GetProperty("appCount").GetInt32(), detail.GetProperty("appsTotal").GetInt32());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("dlqTotal").ValueKind); // no SB: unknown, not 0
        Assert.True(p.GetProperty("queueCount").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(p.GetProperty("registrationGeneratedAt").GetString()));
        Assert.Equal("https://traceit-dev.sgdn.no", p.GetProperty("traceItUrl").GetString());
    }
}

/// <summary>/api/projects dlqTotal comes from the same entities read as /sb/entities (SMOKE-render-session-20261103-1120: list 0 vs Flow 7+1).</summary>
public class CachedSbHealthProbeTests
{
    private static readonly OverviewProject P = new(new ProjectRegistration
    {
        Id = "cp.dev", Project = "cp", Env = "dev", GeneratedAt = DateTimeOffset.UnixEpoch,
        Entities = [new() { Id = "q:a", Kind = "queue", LocalName = "a" }],
    }, Catalog: null, CatalogPath: null, CatalogError: "test");

    private static readonly IReadOnlyList<EntityStatus> Live =
    [
        new("q:customerproject", "queue", "sbq-x", 0, 7, true, "project", null),
        new("q:customerproject-domain", "queue", "sbq-y", 0, 1, true, "project", null),
        new("q:customerproject-topic-inspect", "queue", "sbq-z", null, null, false, "project", null), // not found → null, skipped
        new("t:customerproject-topic", "topic", "sbt", null, null, false, "project", null),
    ];

    private static CachedSbHealthProbe Probe(Func<OverviewProject, CancellationToken, Task<IReadOnlyList<EntityStatus>>> reader)
    {
        var options = new OverviewOptions();
        options.ServiceBus.ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==";
        return new(new SbGateway(options.ServiceBus, NullLogger<SbGateway>.Instance), options) { Reader = reader };
    }

    [Fact]
    public async Task Dlq_total_is_the_sum_of_the_entities_read() =>
        Assert.Equal(new SbHealth(true, 8, Missing: 0, Checked: 2), await Probe((_, _) => Task.FromResult(Live)).ProbeAsync(P, default));

    [Theory]
    [InlineData("SB_UNREACHABLE")]
    [InlineData("SB_AUTH_FAILED")]
    [InlineData("OTHER")]
    public async Task Failure_is_unknown_not_zero_and_is_not_cached(string code)
    {
        var calls = 0;
        var probe = Probe((_, _) => ++calls == 1
            ? (code == "OTHER" ? throw new InvalidOperationException("cold AMQP link") : throw new ApiException(503, code, "x"))
            : Task.FromResult(Live));
        Assert.Equal(new SbHealth(false, null), await probe.ProbeAsync(P, default)); // cold start miss
        Assert.Equal(new SbHealth(true, 8, 0, 2), await probe.ProbeAsync(P, default));     // next poll: real value, not a cached 0
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Summary_serializes_unknown_dlq_as_null()
    {
        var json = JsonSerializer.Serialize(new ProjectSummary("a", "a", "dev", "red", new HealthDetail(0, 0, false, false), 0, 0, null, "t"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"dlqTotal\":null", json);
    }
}

public class SbEntityReaderLoggingTests
{
    private sealed class CaptureLog : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, Microsoft.Extensions.Logging.EventId id, TState s, Exception? e, Func<TState, Exception?, string> f) => Entries.Add((l, f(s, e)));
    }

    private static readonly OverviewProject P = new(new ProjectRegistration
    {
        Id = "cp.dev", Project = "cp", Env = "dev", GeneratedAt = DateTimeOffset.UnixEpoch,
        Entities = [new() { Id = "q:a", Kind = "queue", LocalName = "qa" }, new() { Id = "q:b", Kind = "queue", LocalName = "qb" }],
    }, Catalog: null, CatalogPath: null, CatalogError: "test");

    [Fact]
    public async Task Count_failure_logs_warning_with_type_entity_and_short_message_then_rethrows()
    {
        var log = new CaptureLog();
        await Assert.ThrowsAsync<TimeoutException>(() => SbEntityReader.ReadAsync(
            (_, _, _) => Task.FromResult(true),
            (e, _, _) => e.Id == "q:b" ? throw new TimeoutException("link timed out\nstack junk") : Task.FromResult(new EntityCounts(0, 0, "admin")),
            log, new OverviewOptions(), P, default));
        var (level, text) = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, level);
        Assert.Contains("TimeoutException", text);
        Assert.Contains("qb", text);
        Assert.Contains("link timed out", text);
        Assert.DoesNotContain("stack junk", text);
    }

    [Fact]
    public async Task Unreachable_probe_logs_warning_with_code_and_probe_entity()
    {
        var log = new CaptureLog();
        var ex = await Assert.ThrowsAsync<ApiException>(() => SbEntityReader.ReadAsync(
            (_, _, _) => Task.FromResult(false), (_, _, _) => Task.FromResult(new EntityCounts(0, 0, "admin")),
            log, new OverviewOptions(), P, default));
        Assert.Equal("SB_UNREACHABLE", ex.Code);
        var (level, text) = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, level);
        Assert.Contains("ApiException SB_UNREACHABLE", text);
        Assert.Contains("qa", text);
    }
}
