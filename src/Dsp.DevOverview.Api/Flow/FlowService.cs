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

        var depths = await DepthsAsync(p, view, warnings, ct);

        IReadOnlyDictionary<string, FlowPosition> stored;
        try { stored = positions.Load(p.Id); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings.Add($"FLOW_POSITIONS_UNREADABLE: {positions.PathFor(p.Id)} ignored ({ex.Message}); layout is auto.");
            stored = new Dictionary<string, FlowPosition>();
        }

        return FlowGraphBuilder.Build(p.Registration, view, dir, SbScope, depths, stored, warnings,
            appId => console.IsManagedRunning(p.Id, appId));
    }

    /// <summary>
    /// Depths for visible entities that exist in the active SB scope. Never throws for Service Bus problems: an unreachable
    /// bus or a failing entity gives null depths plus a warning (HANDOFF: "never fake healthy", but the graph still renders).
    /// </summary>
    private async Task<Dictionary<string, EntityCounts>> DepthsAsync(OverviewProject p, FlowScope view, List<string> warnings, CancellationToken ct)
    {
        var result = new Dictionary<string, EntityCounts>();
        var entities = overview.ActiveEntities(p).Where(e => FlowGraphBuilder.IsVisible(e.Scope, view) && e.Kind != "topic").ToList();
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
        foreach (var e in entities)
        {
            try { result[e.Id] = await sb.GetCountsAsync(e, p.Registration, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("Flow depth for {Entity} failed: {Message}", e.Id, ex.Message);
                warnings.Add($"SB_DEPTH_FAILED: {e.Id} depth unavailable ({ex.Message}).");
            }
        }
        return result;
    }

    // PUT /api/projects/{id}/flow/positions  {nodeId:{x,y}}  ({} = reset)
    public async Task<IReadOnlyDictionary<string, FlowPosition>> SavePositionsAsync(string id, Dictionary<string, FlowPositionInput>? body, CancellationToken ct)
    {
        var p = overview.Project(id);
        if (body is null)
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_POSITIONS", "Body must be {\"<nodeId>\":{\"x\":<number>,\"y\":<number>}} ({} resets).");

        var known = FlowGraphBuilder.AllNodeIds(p.Registration);
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
            try { return positions.Load(p.Id); }
            catch (JsonException) { return new Dictionary<string, FlowPosition>(); }
        }
        return await positions.MergeAsync(p.Id, updates, known, ct);
    }
}
