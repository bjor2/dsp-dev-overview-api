using System.Text.Json.Serialization;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;

namespace Dsp.DevOverview.Api.Flow;

// FlowGraph shapes from HANDOFF-ui-ux-data-contracts.md; node/edge model from ADR-003.

public sealed record FlowPosition(double X, double Y);

public sealed record FlowAppInfo(
    string AppKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CloudRoleName,
    bool Attached);

/// <summary>Live depths; null = unknown (probe failed, topic, auto-forwarding entity, or not in the active SB scope). DLQs are not nodes.</summary>
public sealed record FlowSbInfo(
    long? ActiveCount, long? DeadLetterCount, bool DlqHot, string Owner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxDeliveryCount);

public sealed record FlowNode(
    string Id, string Kind, string Label, string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowAppInfo? App,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowSbInfo? Sb,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowPosition? Position);

public sealed record FlowEdge(
    string Id, string From, string To, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    string Scope, string Confidence);

/// <summary>FlowGraph + <c>warnings</c> (e.g. Service Bus unreachable → depths null); the graph itself never fails on SB.</summary>
public sealed record FlowGraph(
    string ProjectId, string Layout, string Direction, IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<string> Warnings);

public enum FlowScope { Local, All }

/// <summary>Pure registration → FlowGraph mapping (no I/O): depths and stored positions are passed in.</summary>
public static class FlowGraphBuilder
{
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

    /// <summary>Every node id the registration can produce (any scope): the ids PUT /flow/positions accepts.</summary>
    public static IReadOnlySet<string> AllNodeIds(ProjectRegistration r) =>
        Build(r, FlowScope.All, "LR", SbScope.Local, new Dictionary<string, EntityCounts>(), new Dictionary<string, FlowPosition>(), [])
            .Nodes.Select(n => n.Id).ToHashSet();

    public static FlowGraph Build(
        ProjectRegistration r, FlowScope view, string direction, SbScope sbScope,
        IReadOnlyDictionary<string, EntityCounts> depths, IReadOnlyDictionary<string, FlowPosition> positions,
        IReadOnlyList<string> warnings, Func<string, bool>? attached = null)
    {
        var nodes = new List<FlowNode>();
        foreach (var a in r.Apps)
            nodes.Add(new FlowNode(a.Id, "app", a.Local?.Port is { } port ? $"{a.Name} :{port}" : a.Name, "both",
                new FlowAppInfo(a.Kind, a.Local?.Port, a.CloudRoleName, Attached: attached?.Invoke(a.Id) ?? false), null, null)); // overview-managed process running (slice 4)

        foreach (var e in r.Entities)
        {
            var c = depths.GetValueOrDefault(e.Id);
            var maxDelivery = e.Kind == "topic" ? (int?)null : e.MaxDeliveryCountFor(e.ExistsIn(sbScope) ? sbScope : Other(sbScope));
            nodes.Add(new FlowNode(e.Id, e.Kind, Label(e), e.Scope ?? "both", null,
                new FlowSbInfo(c?.Active, c?.DeadLetter, DlqHot: c?.DeadLetter is > 0, e.Owner ?? "project", maxDelivery), null));
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

        var visible = nodes.Where(n => IsVisible(n.Scope, view))
            .Select(n => positions.TryGetValue(n.Id, out var p) ? n with { Position = p } : n)
            .ToList();
        var visibleIds = visible.Select(n => n.Id).ToHashSet();

        // Ids follow registration order over all edges, so an edge keeps its id whether cloud edges are shown or not.
        var edges = r.Edges
            .Select((x, i) => new FlowEdge($"e{i + 1}", x.From, x.To, x.Kind, x.Label, x.Scope ?? "both", x.Confidence ?? "manual"))
            .Where(x => IsVisible(x.Scope, view) && visibleIds.Contains(x.From) && visibleIds.Contains(x.To))
            .ToList();

        var layout = visible.Any(n => n.Position is not null) ? "stored" : "auto";
        return new FlowGraph(r.Id, layout, direction, visible, edges, warnings);
    }

    private static SbScope Other(SbScope s) => s == SbScope.Local ? SbScope.Cloud : SbScope.Local;

    /// <summary>Subscriptions show their own name (ADR-003: "local-inspect"); a cloud-only entity without a local name shows its cloud name.</summary>
    private static string Label(RegisteredEntity e) =>
        e.LocalName is { Length: > 0 } and not "n/a" ? e.LocalName : e.CloudName ?? e.Id;
}
