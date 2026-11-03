using System.Collections.Concurrent;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.AppInsights;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;

namespace Dsp.DevOverview.Api.Health;

/// <summary>Slice 7: Overview:Health. Probe results are cached briefly so the project list can be polled.</summary>
public sealed class HealthOptions
{
    /// <summary>Service Bus reachability + DLQ total cache per project. 0 = probe on every request.</summary>
    public int SbProbeCacheSeconds { get; set; } = 5;
}

/// <summary>Slice 7: Overview:TraceIt. v1 is link-only (ADR-004); the registration's observability.traceIt.url wins per project.</summary>
public sealed class TraceItOptions
{
    /// <summary>Default TraceIt link for projects whose registration has none. Empty = no link.</summary>
    public string? Url { get; set; } = "https://traceit-dev.sgdn.no";
}

/// <summary>
/// ProjectSummary.health from healthDetail. Severity order red &gt; amber &gt; unknown &gt; green; the first rule that
/// matches wins:
/// <list type="number">
/// <item>red: Service Bus unreachable (nothing else can be trusted locally).</item>
/// <item>amber: at least one registered app is down (a known local problem, whatever App Insights says).</item>
/// <item>unknown: every app is up and SB answers, but the App Insights probe has not succeeded, so health can't be confirmed.</item>
/// <item>green: every app up, SB reachable, App Insights query ok.</item>
/// </list>
/// </summary>
public static class HealthRules
{
    public const string Green = "green", Amber = "amber", Red = "red", Unknown = "unknown";

    public static string Evaluate(HealthDetail d)
    {
        if (!d.SbReachable) return Red;
        if (d.AppsUp < d.AppsTotal) return Amber;
        if (!d.AiQueryOk) return Unknown;
        return Green;
    }
}

/// <summary>Service Bus side of a project's health: reachable, and the DLQ total over its active entities when reachable.</summary>
/// <summary>DlqTotal is null when unknown (SB unreachable, auth failed, not configured, probe threw); never a fake 0.</summary>
public sealed record SbHealth(bool Reachable, long? DlqTotal);

/// <summary>
/// The one Service Bus read behind GET /sb/entities AND /api/projects dlqTotal: active entities, reachability probe on
/// the first queue, then per-entity counts with the host's entity-name resolution (ServiceBusOptions.EntityName).
/// Throws ApiException SB_UNREACHABLE / SB_AUTH_FAILED.
/// </summary>
public static class SbEntityReader
{
    public static async Task<IReadOnlyList<EntityStatus>> ReadAsync(SbGateway sb, OverviewOptions options, OverviewProject p, CancellationToken ct)
    {
        var entities = OverviewService.ActiveEntities(p, options.ServiceBus.Scope).ToList();
        var probe = entities.FirstOrDefault(e => e.Kind == "queue");
        if (probe is null || !await sb.IsReachableAsync(probe, p.Registration, ct))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "SB_UNREACHABLE", options.ServiceBus.UnreachableMessage, retryable: true);
        var result = new List<EntityStatus>();
        foreach (var e in entities)
        {
            var c = await sb.GetCountsAsync(e, p.Registration, ct);
            result.Add(new EntityStatus(e.Id, e.Kind, options.ServiceBus.EntityName(e), c.Active, c.DeadLetter,
                DlqHot: c.DeadLetter is > 0, Owner: e.Owner ?? "project", ParentEntityId: e.Kind == "subscription" ? e.Topic : null));
        }
        return result;
    }

    /// <summary>Same sum the UI makes from /sb/entities (and Flow): DLQ counts of entities that report one.</summary>
    public static long DlqTotal(IEnumerable<EntityStatus> entities) => entities.Sum(e => e.DeadLetterCount ?? 0);
}

public interface ISbHealthProbe
{
    Task<SbHealth> ProbeAsync(OverviewProject project, CancellationToken ct);
}

/// <summary>Reads via <see cref="SbEntityReader"/> (same as /sb/entities); successes cached per project for a few seconds, failures never.</summary>
public sealed class CachedSbHealthProbe(SbGateway sb, OverviewOptions options, TimeProvider? clock = null) : ISbHealthProbe
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // Keyed by project id; the registration reference is kept so a hot reload (slice 6) invalidates the entry.
    private readonly ConcurrentDictionary<string, (ProjectRegistration Registration, DateTimeOffset At, SbHealth Result)> _cache = new();

    public async Task<SbHealth> ProbeAsync(OverviewProject project, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.Health.SbProbeCacheSeconds));
        if (_cache.TryGetValue(project.Id, out var hit) && ReferenceEquals(hit.Registration, project.Registration)
            && _clock.GetUtcNow() - hit.At < ttl)
            return hit.Result;

        // Failures are returned as unknown and NOT cached, so a cold-start miss can't pin the list at a stale value.
        if (await ProbeNowAsync(project, ct) is not { } result) return new(false, null);
        _cache[project.Id] = (project.Registration, _clock.GetUtcNow(), result);
        return result;
    }

    /// <summary>Overridable read (tests); defaults to <see cref="SbEntityReader"/>, i.e. exactly what /sb/entities returns.</summary>
    public Func<OverviewProject, CancellationToken, Task<IReadOnlyList<EntityStatus>>>? Reader { get; init; }

    private async Task<SbHealth?> ProbeNowAsync(OverviewProject p, CancellationToken ct)
    {
        if (options.ServiceBus.AuthMode == SbAuthMode.None) return new(false, null);
        try
        {
            var list = await (Reader ?? ((pp, c) => SbEntityReader.ReadAsync(sb, options, pp, c)))(p, ct);
            return new(true, SbEntityReader.DlqTotal(list));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }
}

/// <summary>
/// GET /api/projects: one ProjectSummary per registered project from live signals, never assumed. An app is up when the
/// console process manager runs it (slice 4) or something listens on its registered local port (runs started elsewhere).
/// </summary>
public sealed class ProjectHealthService(
    ProjectStore store, ISbHealthProbe sb, IAiQueryProbe? ai, IConsoleStatusProvider? console, IPortProbe ports, OverviewOptions options)
{
    public async Task<IReadOnlyList<ProjectSummary>> SummariesAsync(CancellationToken ct)
    {
        var projects = store.All.ToList();
        return await Task.WhenAll(projects.Select(p => SummaryAsync(p, ct)));
    }

    public async Task<ProjectSummary> SummaryAsync(OverviewProject p, CancellationToken ct)
    {
        var r = p.Registration;
        var appsUpTask = AppsUpAsync(p, ct);
        var sbTask = sb.ProbeAsync(p, ct);
        var appsUp = await appsUpTask;
        var sbHealth = await sbTask;
        var aiQueryOk = ai?.IsQueryOk(p) ?? false; // cached background probe (slice 5), false until a query succeeds

        var detail = new HealthDetail(appsUp, r.Apps.Count, sbHealth.Reachable, aiQueryOk);
        return new ProjectSummary(r.Id, r.Project, r.Env, HealthRules.Evaluate(detail), detail,
            r.Apps.Count, OverviewService.ActiveEntities(p, options.ServiceBus.Scope).Count(e => e.Kind == "queue"),
            sbHealth.DlqTotal is { } dlq ? (int)Math.Min(dlq, int.MaxValue) : null, r.GeneratedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            TraceItUrl(p, options));
    }

    public async Task<int> AppsUpAsync(OverviewProject p, CancellationToken ct)
    {
        var up = await Task.WhenAll(p.Registration.Apps.Select(async app =>
            console?.IsManagedRunning(p.Id, app.Id) == true
            || app.Local?.Port is { } port && await ports.IsListeningAsync(port, ct)));
        return up.Count(x => x);
    }

    /// <summary>Registration observability.traceIt.url, else Overview:TraceIt:Url; null when neither is set.</summary>
    public static string? TraceItUrl(OverviewProject p, OverviewOptions options) =>
        p.Registration.Observability?.TraceIt?.Url is { Length: > 0 } own ? own
        : options.TraceIt.Url is { Length: > 0 } fallback ? fallback
        : null;
}
