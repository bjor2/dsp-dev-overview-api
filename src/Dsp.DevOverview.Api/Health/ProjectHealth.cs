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
public sealed record SbHealth(bool Reachable, long DlqTotal);

public interface ISbHealthProbe
{
    Task<SbHealth> ProbeAsync(OverviewProject project, CancellationToken ct);
}

/// <summary>Probes via <see cref="SbGateway"/> (peek one message on the first queue, then counts), cached per project for a few seconds.</summary>
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

        var result = await ProbeNowAsync(project, ct);
        _cache[project.Id] = (project.Registration, _clock.GetUtcNow(), result);
        return result;
    }

    private async Task<SbHealth> ProbeNowAsync(OverviewProject p, CancellationToken ct)
    {
        var entities = OverviewService.ActiveEntities(p, options.ServiceBus.Scope).ToList();
        var probe = entities.FirstOrDefault(e => e.Kind == "queue");
        try { if (probe is null || !await sb.IsReachableAsync(probe, p.Registration, ct)) return new(false, 0); }
        catch (ApiException ex) when (ex.Code == "SB_AUTH_FAILED") { return new(false, 0); }
        var dlqTotal = 0L;
        try
        {
            foreach (var e in entities)
                dlqTotal += (await sb.GetCountsAsync(e, p.Registration, ct)).DeadLetter ?? 0;
        }
        catch (ApiException ex) when (ex.Code is "SB_UNREACHABLE" or "SB_AUTH_FAILED") { return new(false, 0); } // went away mid-probe
        return new(true, dlqTotal);
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
            (int)Math.Min(sbHealth.DlqTotal, int.MaxValue), r.GeneratedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
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
