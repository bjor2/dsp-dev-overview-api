using System.Text.RegularExpressions;
using Dsp.DevOverview.Api.Registration;

namespace Dsp.DevOverview.Api.Architecture;

/// <summary>
/// ADDENDA-011 §6 cross-document rules (plus ADDENDA-011.1 V-011 handler roots) that JSON Schema cannot express. Every issue is collected (not first-fail) so one load
/// reports everything; any issue rejects the whole catalog. <paramref name="layers"/> is aligned with catalog.layers.
/// </summary>
public static partial class ArchitectureValidator
{
    private static readonly Dictionary<string, string[]> Scopes = new()
    {
        ["local"] = ["local"], ["cloud"] = ["cloud"], ["both"] = ["local", "cloud"],
    };

    [GeneratedRegex(@"^https://[a-z0-9-]+\.atlassian\.net/wiki/(?:[^?#\s]*/)?pages/[0-9]+(?:/[^?#\s]*)?$")]
    private static partial Regex ConfluencePage();

    [GeneratedRegex(@"^dev-overview/architecture/registrations/[a-z0-9][a-z0-9.-]*\.json$")]
    private static partial Regex RegistrationRef();

    public static IReadOnlyList<string> Check(ArchitectureCatalogDoc catalog, IReadOnlyList<ArchitectureLayerDoc> layers, ProjectRegistration registration)
    {
        var issues = new List<string>();
        var project = catalog.Project;

        // V-002 identity/scope
        if (project != registration.Project) issues.Add($"V-002: catalog project '{project}' is not registration project '{registration.Project}'");
        if (catalog.RegistrationId != registration.Id) issues.Add($"V-002: catalog registrationId '{catalog.RegistrationId}' is not '{registration.Id}'");
        foreach (var dup in catalog.Layers.GroupBy(e => e.Id).Where(g => g.Count() > 1)) issues.Add($"V-002: duplicate catalog layer id {dup.Key}");
        foreach (var dup in catalog.Layers.GroupBy(e => e.Path).Where(g => g.Count() > 1)) issues.Add($"V-002: duplicate catalog path {dup.Key}");

        var byId = new Dictionary<string, ArchitectureLayerDoc>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var entities = registration.Entities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.Id != catalog.Layers[i].Id) issues.Add($"V-002: '{catalog.Layers[i].Path}' holds {layer.Id}, catalog says {catalog.Layers[i].Id}");
            if (!byId.TryAdd(layer.Id, layer)) issues.Add($"V-002: duplicate layer id {layer.Id}");
            if (layer.Project != project) issues.Add($"V-002: {layer.Id} project '{layer.Project}' is not '{project}'");
            if (!InProject(layer.Id, "layer", project)) issues.Add($"V-002: {layer.Id} is outside the layer:{project}. namespace");
            if (layer.ParentLayerId is { } parentRef && !InProject(parentRef, "layer", project))
                issues.Add($"V-002: {layer.Id} parent {parentRef} is cross-project");
            CheckSources(issues, layer.Id, layer.Sources);

            var local = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in layer.Nodes)
            {
                if (!InProject(node.Id, "comp", project)) issues.Add($"V-002: node {node.Id} in {layer.Id} is outside the comp:{project}. namespace");
                if (!nodeIds.Add(node.Id)) issues.Add($"V-002: duplicate node id {node.Id}");
                local.Add(node.Id);
                if (node.FlowEntityId is { } entity && !entities.Contains(entity))
                    issues.Add($"V-006: node {node.Id} flowEntityId {entity} is not a registration entity");
                if (node.OpensLayerId is { } opens && !InProject(opens, "layer", project))
                    issues.Add($"V-005: node {node.Id} opens cross-project layer {opens}");
                CheckSources(issues, node.Id, node.Sources);
            }
            foreach (var edge in layer.Edges)
            {
                if (!InProject(edge.Id, "aedge", project)) issues.Add($"V-002: edge {edge.Id} in {layer.Id} is outside the aedge:{project}. namespace");
                if (!edgeIds.Add(edge.Id)) issues.Add($"V-002: duplicate edge id {edge.Id}");
                if (edge.Kind != "conceptual") issues.Add($"V-007: edge {edge.Id} kind '{edge.Kind}' is not conceptual");
                if (!local.Contains(edge.From) || !local.Contains(edge.To))
                    issues.Add($"V-007: edge {edge.Id} endpoint is not a node of {layer.Id}");
                else if (edge.From == edge.To) issues.Add($"V-007: edge {edge.Id} is a self edge");
                CheckSources(issues, edge.Id, edge.Sources);
            }
            // V-007 Flow v2 invariant: a boundary node never both receives from and sends to the same component
            // (a self-requeue is a badge in the responsibility text, not a back-arrow).
            foreach (var node in layer.Nodes.Where(n => n.Boundary == "outside"))
            {
                var senders = layer.Edges.Where(e => e.To == node.Id).Select(e => e.From).ToHashSet(StringComparer.Ordinal);
                foreach (var peer in layer.Edges.Where(e => e.From == node.Id && e.To != node.Id && senders.Contains(e.To)).Select(e => e.To).Distinct())
                    issues.Add($"V-007: boundary node {node.Id} both receives from and sends to the same component {peer}");
            }
        }

        // V-004 parents/cycles + V-002 descendants cannot widen scope.
        foreach (var layer in byId.Values)
        {
            if (layer.ParentLayerId is { } direct && byId.TryGetValue(direct, out var directParent) && !Within(layer.Scope, directParent.Scope))
                issues.Add($"V-002: {layer.Id} scope '{layer.Scope}' widens parent scope '{directParent.Scope}'");
            var seen = new HashSet<string>(StringComparer.Ordinal) { layer.Id };
            for (var cur = layer; cur.ParentLayerId is { } parentId; )
            {
                if (parentId == cur.Id) { issues.Add($"V-004: {cur.Id} is its own parent"); break; }
                if (!byId.TryGetValue(parentId, out var parent)) { issues.Add($"V-004: {cur.Id} parent {parentId} is not in the catalog"); break; }
                if (!seen.Add(parentId)) { issues.Add($"V-004: parent cycle through {layer.Id}"); break; }
                cur = parent;
            }
        }

        // V-006 root container binding; V-011 (ADDENDA-011.1) every root binds exactly one Flow handler item in that container.
        // No legacy container-only mode: a root without handlerId rejects the catalog. Registration containers/handlers carry no
        // explicit opensLayerId yet (registration schema rejects it), so the V-011 explicit-vs-sidecar agreement check has no input.
        var containers = registration.Containers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var handlers = registration.Handlers.ToDictionary(h => h.Id, StringComparer.Ordinal);
        var rootsByHandler = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var root in byId.Values.Where(l => l.ParentLayerId is null))
        {
            if (root.FlowRef is not { } flow) { issues.Add($"V-006: root {root.Id} has no flowRef"); continue; }
            if (string.IsNullOrWhiteSpace(flow.HandlerId))
                issues.Add($"V-011: root {root.Id} has no flowRef.handlerId (container-only roots are not Explore targets)");
            else
            {
                if (!rootsByHandler.TryGetValue(flow.HandlerId, out var claims)) rootsByHandler[flow.HandlerId] = claims = [];
                claims.Add(root.Id);
            }
            if (!containers.TryGetValue(flow.ContainerId, out var container)) { issues.Add($"V-006: root {root.Id} container {flow.ContainerId} is not in the registration"); continue; }
            if (string.IsNullOrWhiteSpace(flow.HandlerId)) continue;
            if (!handlers.TryGetValue(flow.HandlerId, out var handler)) { issues.Add($"V-011: root {root.Id} handler {flow.HandlerId} is not in the registration"); continue; }
            if (handler.ContainerId != flow.ContainerId)
            {
                issues.Add($"V-011: root {root.Id} handler {flow.HandlerId} belongs to {handler.ContainerId}, not {flow.ContainerId}");
                continue;
            }
            var sourceScope = handler.Scope ?? container.Scope ?? "both";
            if (!Within(root.Scope, sourceScope)) issues.Add($"V-002: root {root.Id} scope '{root.Scope}' widens Flow source scope '{sourceScope}'");
        }
        foreach (var (handlerId, claims) in rootsByHandler.Where(kv => kv.Value.Count > 1))
            issues.Add($"V-011: more than one root for handler {handlerId} ({string.Join(", ", claims.Order(StringComparer.Ordinal))})");

        // V-005 drill ownership: openers are inside components of the direct parent; each non-root opened exactly once.
        var openers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var layer in byId.Values)
            foreach (var node in layer.Nodes.Where(n => n.OpensLayerId is not null))
            {
                var target = node.OpensLayerId!;
                if (!byId.TryGetValue(target, out var child)) issues.Add($"V-005: node {node.Id} opens unknown layer {target}");
                else if (child.ParentLayerId != layer.Id) issues.Add($"V-005: node {node.Id} opens {target}, which is not a direct child of {layer.Id}");
                if (node.Kind != "component" || node.Boundary != "inside") issues.Add($"V-005: node {node.Id} opens a layer but is not an inside component");
                openers[target] = openers.GetValueOrDefault(target) + 1;
            }
        foreach (var layer in byId.Values)
        {
            var count = openers.GetValueOrDefault(layer.Id);
            if (layer.ParentLayerId is null && count > 0) issues.Add($"V-005: root {layer.Id} is opened by an architecture node");
            else if (layer.ParentLayerId is not null && count != 1) issues.Add($"V-005: {layer.Id} has {count} openers, expected exactly 1 in its parent");
        }

        return issues.Distinct().ToList();
    }

    private static bool InProject(string id, string prefix, string project) => id.StartsWith($"{prefix}:{project}.", StringComparison.Ordinal);

    private static bool Within(string scope, string outer) =>
        Scopes.TryGetValue(scope, out var inner) && Scopes.TryGetValue(outer, out var o) && inner.All(o.Contains);

    /// <summary>V-008: non-confirmed evidence carries a rationale; refs are repo-relative, registration-relative or Confluence page URLs.</summary>
    private static void CheckSources(List<string> issues, string owner, IReadOnlyList<ArchitectureSourceDoc> sources)
    {
        if (sources.Count == 0) issues.Add($"V-008: {owner} has no sources");
        foreach (var s in sources)
        {
            if (s.Confidence != "confirmed" && string.IsNullOrWhiteSpace(s.Note)) issues.Add($"V-008: {owner} has {s.Confidence} evidence without a note");
            if (s.Kind == "inferred" && s.Confidence == "confirmed") issues.Add($"V-008: {owner} inferred source cannot be confirmed");
            var ok = s.Kind switch
            {
                "confluence" => ConfluencePage().IsMatch(s.Ref),
                "registration" => RegistrationRef().IsMatch(s.Ref),
                _ => IsRepoRelative(s.Ref),
            };
            if (!ok) issues.Add($"V-008: {owner} has a {s.Kind} ref that is not an allowed reference form");
        }
    }

    /// <summary><c>&lt;repo-name&gt;/&lt;repo-relative-path&gt;</c>: no scheme, no absolute/home/drive path, no dot segments, no backslashes.</summary>
    private static bool IsRepoRelative(string value) =>
        value.Contains('/') && !value.StartsWith('/') && !value.StartsWith('~') && !value.Contains('\\') && !value.Contains(':')
        && value.Split('/').All(seg => seg.Length > 0 && seg != "." && seg != "..");
}
