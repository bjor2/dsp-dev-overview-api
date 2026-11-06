using System.Collections.Concurrent;
using Dsp.DevOverview.Api.Flow;
using Dsp.DevOverview.Api.Registration;

namespace Dsp.DevOverview.Api.Architecture;

/// <summary>
/// ADDENDA-011 read-only architecture layers. Optional subsystem: every failure becomes an <see cref="ArchitectureStatus.Invalid"/>
/// state (logged in full, surfaced as ARCHITECTURE_INVALID), never an exception into Flow/activity/SB.
/// </summary>
public sealed class ArchitectureService(OverviewService overview, string layersRoot, string schemaPath, ILogger<ArchitectureService> log)
{
    private readonly Lazy<ArchitectureSchema> _schema = new(() => new ArchitectureSchema(schemaPath));
    // Keyed by registration instance: a hot-reloaded registration (POST /registration/refresh) is re-validated against the catalog.
    private readonly ConcurrentDictionary<string, (ProjectRegistration Registration, ArchitectureState State)> _cache = new();

    public ArchitectureState For(OverviewProject p)
    {
        if (_cache.TryGetValue(p.Id, out var hit) && ReferenceEquals(hit.Registration, p.Registration)) return hit.State;
        var state = Load(p.Registration);
        _cache[p.Id] = (p.Registration, state);
        return state;
    }

    // GET /api/projects/{id}/architecture
    public ArchitectureIndexResponse Index(string id)
    {
        var p = overview.Project(id);
        var state = For(p);
        return new ArchitectureIndexResponse(p.Id, ArchitectureLoader.SchemaVersion, state.Catalog?.Roots ?? [], state.Warnings,
            state.Catalog?.CatalogHash);
    }

    // GET /api/projects/{id}/architecture/layer?layerId=
    public ArchitectureLayerResponse Layer(string id, string? layerId)
    {
        var p = overview.Project(id);
        if (string.IsNullOrWhiteSpace(layerId))
            throw new ApiException(StatusCodes.Status400BadRequest, "LAYER_ID_REQUIRED", "Query parameter layerId is required.");
        var state = For(p);
        if (state.InvalidMessage is { } invalid)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, ArchitectureState.InvalidCode, invalid);
        if (state.Catalog?.Layers.GetValueOrDefault(layerId) is not { } layer)
            throw new ApiException(StatusCodes.Status404NotFound, "ARCHITECTURE_LAYER_NOT_FOUND", $"No architecture layer with that id in project {p.Id}.");
        return new ArchitectureLayerResponse(p.Id, ArchitectureLoader.SchemaVersion, layer.Document, layer.RootFlowRef,
            layer.Breadcrumbs, layer.Children, []);
    }

    public FlowGraph Link(OverviewProject p, FlowGraph graph) => Link(graph, For(p));

    /// <summary>
    /// Additive Flow drill links from validated root flowRefs only: <c>opensLayerId</c> on the container (or on the handler node for a
    /// handler-bound root). Nothing else in the graph changes; invalid architecture adds its warning and no links.
    /// </summary>
    public static FlowGraph Link(FlowGraph graph, ArchitectureState state)
    {
        if (state.Catalog is not { } c)
            return state.Warnings.Count == 0 ? graph : graph with { Warnings = [.. graph.Warnings, .. state.Warnings] };
        if (c.ContainerLinks.Count == 0 && c.HandlerLinks.Count == 0) return graph;
        return graph with
        {
            Containers = graph.Containers.Select(x => c.ContainerLinks.TryGetValue(x.Id, out var l) ? x with { OpensLayerId = l } : x).ToList(),
            Nodes = graph.Nodes.Select(n => n.Kind == "handler" && c.HandlerLinks.TryGetValue(n.Id, out var l) ? n with { OpensLayerId = l } : n).ToList(),
        };
    }

    private ArchitectureState Load(ProjectRegistration registration)
    {
        ArchitectureState state;
        try { state = ArchitectureLoader.Load(layersRoot, registration, () => _schema.Value); }
        catch (Exception ex)
        {
            log.LogError(ex, "Architecture catalog for {Id} failed to load", registration.Id);
            state = ArchitectureState.Invalid(["V-001: architecture catalog could not be loaded"]);
        }
        switch (state.Status)
        {
            case ArchitectureStatus.Valid:
                log.LogInformation("Architecture catalog for {Id}: {Layers} layers, {Roots} roots (validated)", registration.Id,
                    state.Catalog!.Layers.Count, state.Catalog.Roots.Count);
                break;
            case ArchitectureStatus.Invalid:
                log.LogWarning("Architecture catalog for {Id} rejected ({Code}): {Issues}", registration.Id, ArchitectureState.InvalidCode,
                    string.Join(" | ", state.Issues));
                break;
        }
        return state;
    }
}
