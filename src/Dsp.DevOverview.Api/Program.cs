using System.Text.Json.Serialization;
using Dsp.DevOverview.Api;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.AppInsights;
using Dsp.DevOverview.Api.Flow;
using Dsp.DevOverview.Api.Health;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;

// The box runs out of inotify instances (limit 128) and config reload-on-change would crash startup; the overview
// reads its config once anyway.
if (Environment.GetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange") is null)
    Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Overview").Get<OverviewOptions>() ?? new OverviewOptions();
var root = options.ResolveRoot(builder.Environment.ContentRootPath);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.ServiceBus);
builder.Services.AddSingleton(sp => ProjectStore.Load(options, root, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Registration")));
builder.Services.AddSingleton<SbGateway>();
builder.Services.AddSingleton<OverviewService>();
builder.Services.AddSingleton(new FlowPositionStore(Path.Combine(root, options.DataDir, "flow-positions")));
builder.Services.AddSingleton(options.Console);
builder.Services.AddSingleton<IConsoleSourceFactory>(new DefaultConsoleSourceFactory(options.Console));
builder.Services.AddSingleton<IPortProbe, TcpPortProbe>();
builder.Services.AddSingleton(sp => new ConsoleManager(sp.GetRequiredService<ProjectStore>(), options.Console,
    Path.Combine(root, options.DataDir, options.Console.LogDir), options.Console.StubTicker ?? builder.Environment.IsDevelopment(),
    sp.GetRequiredService<IConsoleSourceFactory>(), sp.GetRequiredService<IPortProbe>(), sp.GetRequiredService<ILogger<ConsoleManager>>()));
builder.Services.AddSingleton<IConsoleStatusProvider>(sp => sp.GetRequiredService<ConsoleManager>());
builder.Services.AddSingleton<FlowService>();
builder.Services.AddAppInsightsLogs(options.AppInsights);
// Slice 7: GET /api/projects health from live signals (console process manager + ports, SB probe, AI probe).
builder.Services.AddSingleton<ISbHealthProbe, CachedSbHealthProbe>();
builder.Services.AddSingleton(sp => new ProjectHealthService(sp.GetRequiredService<ProjectStore>(), sp.GetRequiredService<ISbHealthProbe>(),
    sp.GetService<IAiQueryProbe>(), sp.GetRequiredService<IConsoleStatusProvider>(), sp.GetRequiredService<IPortProbe>(), options));
builder.Services.AddSingleton(sp => new RegistrationRefreshService(sp.GetRequiredService<ProjectStore>(), options, root,
    sp.GetRequiredService<OverviewService>(), sp.GetRequiredService<ILogger<RegistrationRefreshService>>()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Fail at startup (not on first request) if a registration is schema-invalid.
app.Services.GetRequiredService<ProjectStore>();

app.UseCors();
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ApiException ex)
    {
        ctx.Response.StatusCode = ex.Status;
        await ctx.Response.WriteAsJsonAsync(new ApiErrorEnvelope(new ApiError(ex.Code, ex.Message, ex.Retryable)));
    }
    catch (BadHttpRequestException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new ApiErrorEnvelope(new ApiError("BAD_REQUEST", ex.Message, false)));
    }
});

app.MapGet("/healthz", () => Results.Text("ok"));

var api = app.MapGroup("/api");

api.MapGet("/projects", (OverviewService s, CancellationToken ct) => s.ProjectsAsync(ct));

api.MapGet("/projects/{id}/flow", (string id, string? scope, string? direction, FlowService s, CancellationToken ct) => s.GraphAsync(id, scope, direction, ct));

api.MapPut("/projects/{id}/flow/positions", (string id, Dictionary<string, FlowPositionInput>? body, FlowService s, CancellationToken ct) => s.SavePositionsAsync(id, body, ct));

api.MapGet("/projects/{id}/samples", (string id, OverviewService s) => s.Samples(id));

api.MapPost("/projects/{id}/samples/send", (string id, SampleSendBody body, OverviewService s, CancellationToken ct) => s.SendAsync(id, body, ct));

api.MapPost("/projects/{id}/sb/purge", (string id, OverviewService s, CancellationToken ct) => s.PurgeAsync(id, ct));

api.MapGet("/projects/{id}/sb/entities", (string id, OverviewService s, CancellationToken ct) => s.EntitiesAsync(id, ct));

// Entity ids contain ':' and, for subscriptions, '/': accept both raw ("s:customerproject-topic/local-inspect/peek")
// and encoded ("s%3Acustomerproject-topic%2Flocal-inspect/peek") forms via a catch-all.
api.MapGet("/projects/{id}/sb/{**path}", async (string id, string path, int? max, bool? dlq, OverviewService s, CancellationToken ct) =>
    SbRoute.Action(path) switch
    {
        ("peek", var entityId) => Results.Ok(await s.PeekAsync(id, entityId, max ?? 20, dlq ?? false, ct)),
        ("dlq", var entityId) => Results.Ok(await s.DeadLettersAsync(id, entityId, max ?? 50, ct)),
        _ => throw SbRoute.NotFound(path),
    });

api.MapPost("/projects/{id}/sb/{**path}", async (string id, string path, DlqActionBody body, OverviewService s, CancellationToken ct) =>
    SbRoute.Action(path) switch
    {
        ("dlq/retry", var entityId) => Results.Ok(await s.RetryDeadLettersAsync(id, entityId, body, ct)),
        ("dlq/discard", var entityId) => Results.Ok(await s.DiscardDeadLettersAsync(id, entityId, body, ct)),
        _ => throw SbRoute.NotFound(path),
    });

// Slice 4: GET /console, GET /console/{appId}/stream (SSE), POST /console/{appId}/start|stop|restart.
api.MapConsole();

// Slice 5: GET /logs (LogEvent[] from App Insights), GET /logs/roles (cloud_RoleName confirmation).
api.MapLogs();

// Slice 6: POST /projects/{id}/registration/refresh?source=generate|file&dryRun= (ADR-002 generator, hot reload).
api.MapRegistration();

app.Run();

public partial class Program;

/// <summary>Splits "{entityId}/{action}" from the sb catch-all; the action is the known suffix, the rest is the entity id.</summary>
internal static class SbRoute
{
    private static readonly string[] Actions = ["dlq/retry", "dlq/discard", "peek", "dlq"];

    public static (string Action, string EntityId) Action(string path)
    {
        foreach (var action in Actions)
            if (path.EndsWith("/" + action, StringComparison.Ordinal))
                return (action, path[..^(action.Length + 1)]);
        return ("", path);
    }

    public static ApiException NotFound(string path) =>
        new(StatusCodes.Status404NotFound, "NOT_FOUND", $"Unknown Service Bus route '{path}'.");
}
