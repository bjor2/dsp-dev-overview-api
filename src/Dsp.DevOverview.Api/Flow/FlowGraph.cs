using System.Text.Json.Serialization;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;

namespace Dsp.DevOverview.Api.Flow;

// FlowGraph shapes from HANDOFF-ui-ux-data-contracts.md; node/edge model from ADR-003, handler nodes + containers from
// ADDENDA-008 §6/§11 (contract confirmed against ui/app/src/pages/flow/model.ts, architecture/NOTE-ADDENDA-008-api-contract.md).

public sealed record FlowPosition(double X, double Y);

public sealed record FlowSize(double W, double H);

public sealed record FlowAppInfo(
    string AppKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CloudRoleName,
    bool Attached);

/// <summary>Live depths; null = unknown (probe failed, topic, auto-forwarding entity, or not in the active SB scope). DLQs are not nodes.</summary>
public sealed record FlowSbInfo(
    long? ActiveCount, long? DeadLetterCount, bool DlqHot, string Owner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxDeliveryCount,
    // ADDENDA-009: "missing" = not found on this namespace (counts null; render as not provisioned, not as an error). Absent otherwise.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null);

/// <summary>One trigger of a handler (ADDENDA-009): servicebus | http | timer (schedule/setting → clock badge) | blob (path, from a blobContainer).</summary>
public sealed record FlowTrigger(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Function,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schedule,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Setting,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path);

/// <summary>Self-requeue badge (I-1): the handler re-sends to its own trigger entity; never drawn as an edge (OQ-1).</summary>
public sealed record FlowSelfRequeue(string EntityId, IReadOnlyList<string> Messages, IReadOnlyList<string> Sources);

/// <summary>Handler node detail. <c>function</c> is singular (§11) and omitted for an app's HTTP surface.</summary>
public sealed record FlowHandlerInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Function,
    IReadOnlyList<string> TriggerEntityIds,
    IReadOnlyList<FlowSelfRequeue> SelfRequeue,
    // ADDENDA-009 (additive): every trigger; a grouped timer handler lists all its timer functions here.
    IReadOnlyList<FlowTrigger>? Triggers = null);

/// <summary>
/// kind: handler | queue | topic | subscription | blobContainer (ADDENDA-009, no <c>sb</c>) | external. <c>position</c> is RELATIVE to the container origin when
/// <c>containerId</c> is set (handlers), else absolute.
/// </summary>
public sealed record FlowNode(
    string Id, string Kind, string Label, string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowAppInfo? App,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowSbInfo? Sb,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowPosition? Position,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContainerId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowHandlerInfo? Handler = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PositionSource = null,
    // ADDENDA-011 (additive): root architecture layer bound to this handler; omitted when none.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OpensLayerId = null);

/// <summary>Labelled box around handler nodes (layout/grouping only; edges never target it). Position absolute.
/// <see cref="AppIds"/> (ADDENDA-010 §9 #26): apps the container covers, from the registration container's appIds, else its
/// resourceRefs; always serialized, may be empty. <see cref="AppId"/>: deprecated alias kept for one version, appIds[0] when
/// there is exactly one, else null; always serialized.</summary>
public sealed record FlowContainer(
    string Id, string Label, string Kind, IReadOnlyList<string> ResourceRefs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowAppInfo? App,
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowPosition? Position,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowSize? Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PositionSource = null,
    IReadOnlyList<string>? AppIds = null)
{
    public IReadOnlyList<string> AppIds { get; init; } = AppIds ?? [];

    [Obsolete("Deprecated alias (ADDENDA-010 §9 #26); use AppIds.")]
    public string? AppId => AppIds.Count == 1 ? AppIds[0] : null;

    /// <summary>ADDENDA-011 (additive): root architecture layer from a validated catalog flowRef; omitted when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpensLayerId { get; init; }
}

public sealed record FlowEdge(
    string Id, string From, string To, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    string Scope, string Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Sources = null);

public sealed record FlowLayoutHints(int ContainerPadding, string InnerDirection);

/// <summary>
/// FlowGraph v2 + <c>warnings</c> (e.g. Service Bus unreachable → depths null); the graph itself never fails on SB.
/// layout: auto (no position) | partial (some nodes/containers positioned: pin those, auto-lay the rest) | stored (all), counting
/// both layers: registration layoutDefaults (base) and runtime positions (override). <c>positionSource</c> = "default" | "stored".
/// </summary>
public sealed record FlowGraph(
    string ProjectId, int SchemaVersion, string Layout, string Direction, FlowLayoutHints LayoutHints,
    IReadOnlyList<FlowContainer> Containers, IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<string> Warnings);

public enum FlowScope { Local, All }

/// <summary>Pure registration → FlowGraph mapping (no I/O): depths and stored positions are passed in.</summary>
public static class FlowGraphBuilder
{
    public const int ContainerPadding = 16;

    /// <summary>Labels for external sources that only appear as edge endpoints (ADR-003 node table).</summary>
    private static readonly Dictionary<string, string> ExternalLabels = new() { ["ext:seed-messages"] = "seed-messages kit" };

    public static FlowScope ParseScope(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "local" => FlowScope.Local,
        "all" => FlowScope.All,
        _ => throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_SCOPE", $"scope '{value}' is not local|all."),
    };

    public static string ParseDirection(string? value) => value?.ToUpperInvariant() switch
    {
        null or "" or "LR" => "LR",
        "TB" => "TB",
        _ => throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_DIRECTION", $"direction '{value}' is not LR|TB."),
    };

    /// <summary>scope=local hides scope:"cloud"; scope=all shows everything (cloud edges render dashed).</summary>
    public static bool IsVisible(string? scope, FlowScope view) => view == FlowScope.All || scope is not "cloud";

    /// <summary>Every node and container id the registration can produce (any scope): the ids PUT /flow/positions accepts.</summary>
    public static IReadOnlySet<string> AllNodeIds(ProjectRegistration r)
    {
        var g = Build(r, FlowScope.All, "LR", SbScope.Local, new Dictionary<string, EntityCounts>(), new Dictionary<string, FlowPosition>(), []);
        return g.Nodes.Select(n => n.Id).Concat(g.Containers.Select(c => c.Id)).ToHashSet();
    }

    /// <summary>
    /// Effective positions: registration <c>layoutDefaults</c> (base, unknown ids dropped) ← runtime store (override), per key,
    /// with the source of each ("default" | "stored").
    /// </summary>
    public static (Dictionary<string, FlowPosition> Positions, Dictionary<string, string> Sources) EffectivePositions(
        ProjectRegistration r, IReadOnlyDictionary<string, FlowPosition> stored)
    {
        var known = AllNodeIds(r);
        var positions = new Dictionary<string, FlowPosition>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, p) in r.LayoutDefaults?.Positions ?? [])
            if (known.Contains(id) && double.IsFinite(p.X) && double.IsFinite(p.Y)) { positions[id] = new FlowPosition(p.X, p.Y); sources[id] = "default"; }
        foreach (var (id, p) in stored)
            if (known.Contains(id)) { positions[id] = p; sources[id] = "stored"; }
        return (positions, sources);
    }

    /// <summary>Handler id → container id: inner positions are relative to that container (FlowPositionStore keeps the parent).</summary>
    public static IReadOnlyDictionary<string, string> Parents(ProjectRegistration r) =>
        r.Handlers.ToDictionary(h => h.Id, h => h.ContainerId);

    public static FlowGraph Build(
        ProjectRegistration r, FlowScope view, string direction, SbScope sbScope,
        IReadOnlyDictionary<string, EntityCounts> depths, IReadOnlyDictionary<string, FlowPosition> positions,
        IReadOnlyList<string> warnings, Func<string, bool>? attached = null, IReadOnlyDictionary<string, string>? positionSources = null)
    {
        string? Source(string id) => positionSources?.GetValueOrDefault(id);
        var containers = new List<FlowContainer>();
        foreach (var c in r.Containers)
        {
            var app = c.Kind == "resource" && c.ResourceRefs.Count == 1 ? r.Apps.FirstOrDefault(a => a.Id == c.ResourceRefs[0]) : null;
            containers.Add(new FlowContainer(c.Id, c.Label, c.Kind, c.ResourceRefs,
                app is null ? null : new FlowAppInfo(app.Kind, app.Local?.Port, app.CloudRoleName, Attached: attached?.Invoke(app.Id) ?? false), // overview-managed process running (slice 4)
                c.Scope ?? "both", null, AppIds: c.AppIds ?? c.ResourceRefs));
        }
        var containerScope = containers.ToDictionary(c => c.Id, c => c.Scope);

        var nodes = new List<FlowNode>();
        foreach (var h in r.Handlers)
        {
            var info = new FlowHandlerInfo(
                h.Triggers.Select(t => t.Function).FirstOrDefault(f => f is not null),
                h.Triggers.Select(t => t.EntityId).OfType<string>().ToList(),
                h.SelfRequeue.Select(s => new FlowSelfRequeue(s.EntityId, s.Messages, s.Sources)).ToList(),
                h.Triggers.Select(t => new FlowTrigger(t.Kind, t.Function, t.EntityId, t.Schedule, t.Setting, t.Path)).ToList());
            nodes.Add(new FlowNode(h.Id, "handler", h.Label, h.Scope ?? containerScope.GetValueOrDefault(h.ContainerId, "both"), null, null, null,
                h.ContainerId, info));
        }

        foreach (var e in r.Entities)
        {
            if (!e.IsServiceBus) // blobContainer: a plain source node, never Service Bus
            {
                nodes.Add(new FlowNode(e.Id, e.Kind, Label(e), e.Scope ?? "both", null, null, null));
                continue;
            }
            var c = depths.GetValueOrDefault(e.Id);
            var maxDelivery = e.Kind == "topic" ? (int?)null : e.MaxDeliveryCountFor(e.ExistsIn(sbScope) ? sbScope : Other(sbScope));
            nodes.Add(new FlowNode(e.Id, e.Kind, Label(e), e.Scope ?? "both", null,
                new FlowSbInfo(c?.Active, c?.DeadLetter, DlqHot: c?.DeadLetter is > 0, e.Owner ?? "project", maxDelivery,
                    c?.Source == "missing" ? "missing" : null), null));
        }

        // External sources (ext:*) exist only as edge endpoints; their scope is the union of their edges' scopes.
        var known = nodes.Select(n => n.Id).ToHashSet();
        foreach (var ext in r.Edges.SelectMany(x => new[] { x.From, x.To }).Where(id => id.StartsWith("ext:", StringComparison.Ordinal) && !known.Contains(id)).Distinct())
        {
            var scopes = r.Edges.Where(x => x.From == ext || x.To == ext).Select(x => x.Scope ?? "both").Distinct().ToList();
            nodes.Add(new FlowNode(ext, "external", ExternalLabels.GetValueOrDefault(ext, ext["ext:".Length..]),
                scopes.Count == 1 ? scopes[0] : "both", null, null, null));
            known.Add(ext);
        }

        var visibleContainers = containers.Where(c => IsVisible(c.Scope, view))
            .Select(c => positions.TryGetValue(c.Id, out var p) ? c with { Position = p, PositionSource = Source(c.Id) } : c)
            .ToList();
        var shownContainers = visibleContainers.Select(c => c.Id).ToHashSet();
        var visible = nodes.Where(n => IsVisible(n.Scope, view))
            .Select(n => n.ContainerId is { } cid && !shownContainers.Contains(cid) ? n with { ContainerId = null } : n)
            .Select(n => positions.TryGetValue(n.Id, out var p) ? n with { Position = p, PositionSource = Source(n.Id) } : n)
            .ToList();
        var visibleIds = visible.Select(n => n.Id).ToHashSet();

        // Ids follow registration order over all edges, so an edge keeps its id whether cloud edges are shown or not.
        var edges = r.Edges
            .Select((x, i) => new FlowEdge($"e{i + 1}", x.From, x.To, x.Kind, x.Label, x.Scope ?? "both", x.Confidence ?? "manual",
                x.Sources is { Count: > 0 } s ? s : null))
            .Where(x => IsVisible(x.Scope, view) && visibleIds.Contains(x.From) && visibleIds.Contains(x.To))
            .ToList();

        var placed = visible.Count(n => n.Position is not null) + visibleContainers.Count(c => c.Position is not null);
        var total = visible.Count + visibleContainers.Count;
        var layout = placed == 0 ? "auto" : placed == total ? "stored" : "partial";
        var notes = r.UpgradeNotes.Count == 0 && r.LayoutWarnings.Count == 0 ? warnings
            : [.. warnings, .. r.UpgradeNotes.Select(n => $"REGISTRATION_V1_UPGRADED: {n}"), .. r.LayoutWarnings.Select(n => $"LAYOUT_DEFAULTS_UNKNOWN_ID: {n}")];
        return new FlowGraph(r.Id, 2, layout, direction, new FlowLayoutHints(ContainerPadding, direction), visibleContainers, visible, edges, notes);
    }

    private static SbScope Other(SbScope s) => s == SbScope.Local ? SbScope.Cloud : SbScope.Local;

    /// <summary>Subscriptions show their own name (ADR-003: "local-inspect"); a cloud-only entity without a local name shows its cloud name.</summary>
    private static string Label(RegisteredEntity e) =>
        e.LocalName is { Length: > 0 } and not "n/a" ? e.LocalName : e.CloudName ?? e.Id;
}
