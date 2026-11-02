using System.Collections.Concurrent;

namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>ProjectSummary.healthDetail.aiQueryOk without making GET /api/projects wait on Azure.</summary>
public interface IAiQueryProbe
{
    /// <summary>Last cached probe result (false until one succeeds); starts a background refresh when stale.</summary>
    bool IsQueryOk(OverviewProject project);
}

/// <summary>Slice 5: GET /logs and /logs/roles against the project's App Insights (ADR-004).</summary>
public sealed class LogsService(ProjectStore store, ILogsQuery query, AppInsightsOptions options, AzureProfile profile,
    ILogger<LogsService> log, TimeProvider? clock = null) : IAiQueryProbe
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ProbeState> _probes = new();

    private sealed class ProbeState
    {
        public bool Ok;
        public DateTimeOffset CheckedAt = DateTimeOffset.MinValue;
        public int Running;
    }

    public OverviewProject Project(string id) => store.Find(id)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "PROJECT_NOT_FOUND", $"No registration with id '{id}'. Known: {string.Join(", ", store.All.Select(p => p.Id))}");

    /// <summary>Registration apps[].cloudRoleName first, then observability.appInsights.cloudRoleNames.</summary>
    public static IReadOnlyList<string> RegisteredRoles(OverviewProject p) =>
        p.Registration.Apps.Select(a => a.CloudRoleName)
            .Concat(p.Registration.Observability?.AppInsights?.CloudRoleNames ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>/subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Insights/components/{name}; config wins over the registration.</summary>
    public string ResourceId(OverviewProject p)
    {
        var ai = p.Registration.Observability?.AppInsights;
        var subscription = options.SubscriptionId is { Length: > 0 } s ? s : ai?.Subscription;
        var rg = options.ResourceGroup is { Length: > 0 } g ? g : ai?.ResourceGroup;
        var name = options.Resource is { Length: > 0 } n ? n : ai?.Resource;
        if (string.IsNullOrWhiteSpace(rg) || string.IsNullOrWhiteSpace(name))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "AI_NOT_CONFIGURED",
                $"{p.Id}: no App Insights resource/resourceGroup (registration observability.appInsights or Overview:AppInsights).");
        var sub = profile.Find(subscription)
            ?? throw new ApiException(StatusCodes.Status503ServiceUnavailable, "AI_NOT_CONFIGURED",
                $"{p.Id}: subscription '{subscription}' is not a GUID and not in {options.AzureProfilePath}; set Overview:AppInsights:SubscriptionId.");
        return $"/subscriptions/{sub.Id}/resourceGroups/{rg}/providers/Microsoft.Insights/components/{name}";
    }

    // GET /api/projects/{id}/logs?roles=&since=&kql=&max=
    public async Task<IReadOnlyList<LogEvent>> LogsAsync(string id, string? roles, string? since, string? kql, int? max, CancellationToken ct)
    {
        var p = Project(id);
        var roleList = KqlBuilder.ParseRoles(roles, RegisteredRoles(p));
        var from = KqlBuilder.ParseSince(since, _clock.GetUtcNow(), options);
        var fragment = KqlBuilder.SanitizeFragment(kql);
        var rows = max ?? options.DefaultMaxRows;
        if (rows < 1 || rows > options.MaxRowsCap)
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_MAX", $"max must be 1..{options.MaxRowsCap}.");
        var resourceId = ResourceId(p);
        var text = KqlBuilder.BuildLogsQuery(roleList, from, fragment, rows);

        var result = await RunAsync(resourceId, text, from, userKql: fragment is not null, ct);
        Record(p, ok: true);
        return result.Select(LogEventMapper.Map).ToList();
    }

    // GET /api/projects/{id}/logs/roles?since=1d — the ADR-004 confirmation query
    public async Task<RoleConfirmation> ConfirmRolesAsync(string id, string? since, CancellationToken ct)
    {
        var p = Project(id);
        var from = KqlBuilder.ParseSince(since ?? "1d", _clock.GetUtcNow(), options);
        var resourceId = ResourceId(p);
        var text = KqlBuilder.BuildRolesQuery(from);
        var rows = await RunAsync(resourceId, text, from, userKql: false, ct);
        Record(p, ok: true);

        var registered = RegisteredRoles(p);
        var observed = rows
            .Select(r => (Role: r["cloud_RoleName"] as string ?? "", Count: r["itemCount"] is long c ? c : 0))
            .Select(x => new ObservedRole(x.Role, x.Count, registered.Contains(x.Role, StringComparer.Ordinal),
                x.Role.Contains(p.Registration.Project, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new RoleConfirmation(resourceId, text, KqlBuilder.DateTimeLiteral(from), observed,
            RegisteredMissing: registered.Where(r => observed.All(o => o.CloudRoleName != r)).ToList(),
            Unregistered: observed.Where(o => o.MatchesProject && !o.Registered).Select(o => o.CloudRoleName).ToList());
    }

    private async Task<IReadOnlyList<LogRow>> RunAsync(string resourceId, string kql, DateTimeOffset from, bool userKql, CancellationToken ct)
    {
        try
        {
            return await query.QueryResourceAsync(resourceId, kql, from, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var mapped = AppInsightsErrors.Map(ex, userKql);
            log.LogWarning("App Insights query failed: {Code} {Message}", mapped.Code, mapped.Message);
            throw mapped;
        }
    }

    public bool IsQueryOk(OverviewProject project)
    {
        if (!options.ProbeEnabled) return false;
        var state = _probes.GetOrAdd(project.Id, _ => new ProbeState());
        if (_clock.GetUtcNow() - state.CheckedAt > TimeSpan.FromSeconds(options.ProbeTtlSeconds)
            && Interlocked.CompareExchange(ref state.Running, 1, 0) == 0)
            _ = Task.Run(() => ProbeAsync(project, state));
        return state.Ok;
    }

    /// <summary>Runs the probe now (tests, and the background refresh).</summary>
    public async Task<bool> ProbeNowAsync(OverviewProject project, CancellationToken ct = default)
    {
        try
        {
            await query.QueryResourceAsync(ResourceId(project), KqlBuilder.ProbeQuery, _clock.GetUtcNow().AddMinutes(-5), ct);
            Record(project, ok: true);
            return true;
        }
        catch (Exception ex)
        {
            var mapped = AppInsightsErrors.Map(ex, userKql: false);
            log.LogInformation("App Insights probe for {Project}: {Code}", project.Id, mapped.Code);
            Record(project, ok: false);
            return false;
        }
    }

    private async Task ProbeAsync(OverviewProject project, ProbeState state)
    {
        try { await ProbeNowAsync(project); }
        finally { Volatile.Write(ref state.Running, 0); }
    }

    private void Record(OverviewProject p, bool ok)
    {
        var state = _probes.GetOrAdd(p.Id, _ => new ProbeState());
        state.Ok = ok;
        state.CheckedAt = _clock.GetUtcNow();
    }
}
