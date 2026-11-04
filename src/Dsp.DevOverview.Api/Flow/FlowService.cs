using System.Text.Json;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;

namespace Dsp.DevOverview.Api.Flow;

/// <summary>GET /flow and PUT /flow/positions: registration + live depths (slice 1/2 gateway) + stored positions.</summary>
public sealed class FlowService(OverviewService overview, SbGateway sb, FlowPositionStore positions, OverviewOptions options,
    IConsoleStatusProvider console, ILogger<FlowService> log)
{
    private SbScope SbScope => options.ServiceBus.Scope;

    // GET /api/projects/{id}/flow?scope=local|all&direction=LR|TB
    public async Task<FlowGraph> GraphAsync(string id, string? scope, string? direction, CancellationToken ct)
    {
        var p = overview.Project(id);
        var view = FlowGraphBuilder.ParseScope(scope);
        var dir = FlowGraphBuilder.ParseDirection(direction);
        var warnings = new List<string>();

        if (HostMismatch(p, options.ServiceBus) is { } mismatch) warnings.Add(mismatch);
        var depths = await DepthsAsync(p, view, warnings, ct);

        IReadOnlyDictionary<string, FlowPosition> stored;
        try { stored = positions.Load(p.Id, FlowPositionContext.For(p.Registration)); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings.Add($"FLOW_POSITIONS_UNREADABLE: {positions.PathFor(p.Id)} ignored ({ex.Message}); layout is auto.");
            stored = new Dictionary<string, FlowPosition>();
        }

        // Checked-in layoutDefaults (survive Render redeploys) ← runtime positions, per key.
        var (effective, sources) = FlowGraphBuilder.EffectivePositions(p.Registration, stored);
        return FlowGraphBuilder.Build(p.Registration, view, dir, SbScope, depths, effective, warnings,
            appId => console.IsManagedRunning(p.Id, appId), sources);
    }

    /// <summary>
    /// ADDENDA-009 <c>hosts</c>: when the registration names a different namespace for the active host kind, depths come from the
    /// configured namespace (one per API) — say so instead of silently showing another namespace's state.
    /// </summary>
    public static string? HostMismatch(OverviewProject p, ServiceBusOptions sb)
    {
        if (p.Registration.Hosts?.GetValueOrDefault(sb.HostKindLabel) is not { Namespace: { Length: > 0 } expected }) return null;
        var actual = sb.HostKind == SbHostKind.Emulator ? "sbemulatorns" : sb.NormalizedNamespace;
        if (actual is null || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase) || !expected.Contains('.') && sb.HostKind != SbHostKind.Emulator)
            return null; // same namespace, or the registration only has a placeholder (e.g. "870 dev shared namespace (name unconfirmed)")
        return $"SB_HOST_NAMESPACE_MISMATCH: registration hosts.{sb.HostKindLabel}.namespace is {expected}, but this API reads {actual}; depths/missing state refer to {actual}.";
    }

    /// <summary>
    /// GET /api/projects/{id}/flow/positions/export: the effective merged map (defaults ← runtime) in registration
    /// <c>layoutDefaults</c> shape, keys sorted, whole pixels. tools/Generate-Registration export-layout writes it back.
    /// </summary>
    public FlowLayoutExport Export(string id)
    {
        var p = overview.Project(id);
        IReadOnlyDictionary<string, FlowPosition> stored;
        try { stored = positions.Load(p.Id, FlowPositionContext.For(p.Registration)); }
        catch (Exception ex) when (ex is JsonException or IOException) { stored = new Dictionary<string, FlowPosition>(); }
        var (effective, sources) = FlowGraphBuilder.EffectivePositions(p.Registration, stored);
        var map = new SortedDictionary<string, FlowExportPoint>(StringComparer.Ordinal);
        foreach (var (key, pos) in effective)
            map[key] = new FlowExportPoint(Math.Round(pos.X, MidpointRounding.AwayFromZero), Math.Round(pos.Y, MidpointRounding.AwayFromZero));
        return new FlowLayoutExport(p.Id, map, sources.Count(s => s.Value == "stored"), sources.Count(s => s.Value == "default"));
    }

    /// <summary>
    /// Depths for visible entities that exist in the active SB scope. Never throws for Service Bus problems: an unreachable
    /// bus or a failing entity gives null depths plus a warning (HANDOFF: "never fake healthy", but the graph still renders).
    /// </summary>
    private async Task<Dictionary<string, EntityCounts>> DepthsAsync(OverviewProject p, FlowScope view, List<string> warnings, CancellationToken ct)
    {
        var result = new Dictionary<string, EntityCounts>();
        // Topics too: their counts stay null, but a topic missing on the namespace reports state "missing" (ADDENDA-009).
        var entities = overview.ActiveEntities(p).Where(e => FlowGraphBuilder.IsVisible(e.Scope, view)).ToList();
        var probe = entities.FirstOrDefault(e => e.Kind == "queue");
        if (probe is null) return result;
        bool reachable;
        try { reachable = await sb.IsReachableAsync(probe, p.Registration, ct); }
        catch (ApiException ex) when (ex.Code == "SB_AUTH_FAILED")
        {
            warnings.Add($"SB_AUTH_FAILED: {ex.Message} Depths are null.");
            return result;
        }
        if (!reachable)
        {
            warnings.Add($"SB_UNREACHABLE: {options.ServiceBus.UnreachableMessage} Depths are null.");
            return result;
        }
        // Parallel, each read bounded by SbGateway (timeout / missing entity → null depth, never a hang).
        var read = await SbCountReader.ForAllAsync(entities, options.ServiceBus.CountParallelism, async (e, c) =>
        {
            try { return (e, (EntityCounts?)await sb.GetCountsAsync(e, p.Registration, c), (string?)null); }
            catch (Exception ex) when (ex is not OperationCanceledException || !c.IsCancellationRequested)
            {
                log.LogWarning("Flow depth for {Entity} failed: {Message}", e.Id, ex.Message);
                return (e, null, $"SB_DEPTH_FAILED: {e.Id} depth unavailable ({ex.Message}).");
            }
        }, ct);
        foreach (var (e, counts, warning) in read)
        {
            if (counts is not null) result[e.Id] = counts;
            if (warning is not null) warnings.Add(warning);
        }
        return result;
    }

    // PUT /api/projects/{id}/flow/positions  {graphId:{x,y}}  ({} = reset this env). Partial: only the sent keys change.
    // Containers and standalone nodes absolute, handler nodes relative to their container (ADDENDA-008 §7).
    public async Task<IReadOnlyDictionary<string, FlowPosition>> SavePositionsAsync(string id, Dictionary<string, FlowPositionInput>? body, CancellationToken ct)
    {
        var p = overview.Project(id);
        if (body is null)
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_POSITIONS", "Body must be {\"<nodeId>\":{\"x\":<number>,\"y\":<number>}} ({} resets).");

        var known = FlowGraphBuilder.AllNodeIds(p.Registration); // nodes + containers
        var ctx = FlowPositionContext.For(p.Registration);
        var updates = new Dictionary<string, FlowPosition>();
        foreach (var (nodeId, pos) in body)
        {
            if (!known.Contains(nodeId)) continue; // unknown node ids are ignored, not an error
            if (pos?.X is not { } x || pos.Y is not { } y || !double.IsFinite(x) || !double.IsFinite(y))
                throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_POSITIONS", $"{nodeId}: x and y must be finite numbers.");
            updates[nodeId] = new FlowPosition(x, y);
        }
        // A body with only unknown ids is not a reset: only a literal {} clears.
        if (updates.Count == 0 && body.Count > 0)
        {
            try { return positions.Load(p.Id, ctx); }
            catch (JsonException) { return new Dictionary<string, FlowPosition>(); }
        }
        return await positions.MergeAsync(p.Id, updates, ctx, ct);
    }
}

public sealed record FlowExportPoint(double X, double Y);

/// <summary>Export payload: <c>positions</c> is exactly registration <c>layoutDefaults.positions</c>; the rest is informational.</summary>
public sealed record FlowLayoutExport(string ProjectId, IReadOnlyDictionary<string, FlowExportPoint> Positions, int FromStored, int FromDefaults);
