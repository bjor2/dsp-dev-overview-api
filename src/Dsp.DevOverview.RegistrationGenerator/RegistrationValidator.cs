using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>A cross-field registration error (ADDENDA-008 §9.3): V-1..V-5 and I-1. <see cref="Code"/> e.g. I1_UNRESOLVED.</summary>
public sealed record RegistrationIssue(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>ADDENDA-008 §3 / §11 id rules, shared by the generator and the v1 → v2 upgrade.</summary>
public static partial class HandlerNaming
{
    /// <summary><c>{project}.fn.{short}</c>, or <c>overrides.handlerIds[function]</c>.</summary>
    public static string HandlerId(string project, string function, JsonObject? overrides) =>
        overrides?["handlerIds"]?[function]?.ToString() ?? $"{project}.fn.{Short(project, function)}";

    /// <summary>Function name minus "QueueHandler", project prefix dropped (case-insensitive), kebab-case.</summary>
    public static string Short(string project, string function)
    {
        var name = function.EndsWith("QueueHandler", StringComparison.Ordinal) ? function[..^"QueueHandler".Length] : function;
        var compact = project.Replace("-", "", StringComparison.Ordinal);
        var stripped = name.StartsWith(compact, StringComparison.OrdinalIgnoreCase) ? name[compact.Length..] : name;
        return Kebab(stripped.Length > 0 ? stripped : name);
    }

    /// <summary><c>{project}.{app}.http</c> for a web app's HTTP surface.</summary>
    public static string HttpHandlerId(string project, string appId) => $"{project}.{AppName(appId)}.http";

    public static string HandlerLabel(string id, string project, string? function, JsonObject? overrides) =>
        overrides?["handlerLabels"]?[id]?.ToString()
        ?? (function is null ? "HTTP controllers" : Short(project, function).Replace('-', ' ').ToUpperInvariant());

    /// <summary>
    /// <c>container:{project}.{appShort}</c>: web → app name (api); func → func-app when it is the only func app, else
    /// {name}-app; func-ingress → {name}-app (ingress-app).
    /// </summary>
    public static string ContainerId(string project, string appId, string kind, IEnumerable<(string Id, string Kind)> apps)
    {
        var name = AppName(appId);
        var shortName = kind switch
        {
            "web" => name,
            "func" when apps.Count(a => a.Kind == "func") == 1 => "func-app",
            _ => $"{name}-app",
        };
        return $"container:{project}.{shortName}";
    }

    public static string ContainerLabel(string name, int? port) => port is { } p ? $"{name} :{p}" : name;

    private static string AppName(string appId) => appId.StartsWith("app:", StringComparison.Ordinal) ? appId[4..] : appId;

    private static string Kebab(string s) => KebabBoundary().Replace(s, "$1-$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex KebabBoundary();
}

/// <summary>
/// ADDENDA-008 cross-field checks on a v2 record (JSON Schema cannot express them):
/// V-1 every containerId exists; V-2 edge endpoints are handlers, entities or ext:* (never container:* / app:*), trigger
/// edges end at a handler; V-3 handler appId ∈ its container's resourceRefs; V-4 invariant I-1; V-5 ≥1 trigger edge per
/// servicebus trigger and per blob trigger with an entityId (competing consumers allowed); timer triggers need no edge.
/// <para>I-1 (§11): ins(E) = sources of publish|forward|inject edges into E, outs(E) = targets of trigger edges from E. For
/// N in both: when every N→E edge is <c>confidence: code</c>, <see cref="Apply"/> moves it to <c>N.selfRequeue</c>
/// (<c>generated: true</c>) and drops the edge; otherwise I1_UNRESOLVED (a mis-attributed app-level edge must never become
/// a false self-requeue). <c>overrides.allowBidirectional[{entity,node}]</c> is the only escape hatch.</para>
/// </summary>
public static class RegistrationValidator
{
    private static readonly string[] Inbound = ["publish", "forward", "inject"];

    /// <summary>Generator mode: rewrites code self-sends into <c>selfRequeue</c>, then returns every remaining issue.</summary>
    public static IReadOnlyList<RegistrationIssue> Apply(JsonObject record) => Run(record, rewrite: true);

    /// <summary>Loader mode: no rewrite; any I-1 pair left is an error.</summary>
    public static IReadOnlyList<RegistrationIssue> Check(JsonObject record) => Run(record, rewrite: false);

    private static List<RegistrationIssue> Run(JsonObject record, bool rewrite)
    {
        var issues = new List<RegistrationIssue>();
        var containers = Items(record, "containers").ToDictionary(c => Str(c, "id"), StringComparer.Ordinal);
        var handlers = Items(record, "handlers").ToDictionary(h => Str(h, "id"), StringComparer.Ordinal);
        var entities = Items(record, "entities").Select(e => Str(e, "id")).ToHashSet(StringComparer.Ordinal);
        var edges = record["edges"] as JsonArray ?? [];

        foreach (var (id, h) in handlers)
        {
            var cid = Str(h, "containerId");
            if (!containers.TryGetValue(cid, out var c)) { issues.Add(new("V1_UNKNOWN_CONTAINER", $"handler {id}: containerId {cid} does not exist")); continue; }
            var refs = (c["resourceRefs"] as JsonArray ?? []).Select(r => r?.ToString()).ToList();
            if (!refs.Contains(Str(h, "appId")))
                issues.Add(new("V3_APP_NOT_IN_CONTAINER", $"handler {id}: appId {Str(h, "appId")} is not in {cid}.resourceRefs [{string.Join(", ", refs)}]"));
        }

        bool Endpoint(string id) => handlers.ContainsKey(id) || entities.Contains(id) || id.StartsWith("ext:", StringComparison.Ordinal);
        foreach (var e in edges.OfType<JsonObject>())
        {
            var (from, to, kind) = (Str(e, "from"), Str(e, "to"), Str(e, "kind"));
            foreach (var end in new[] { from, to })
            {
                var boxOrApp = end.StartsWith("container:", StringComparison.Ordinal) || end.StartsWith("app:", StringComparison.Ordinal);
                // Dangling ids are an error for generator output; the loader keeps v1 tolerance (the Flow skips such edges).
                if (!Endpoint(end) && (boxOrApp || rewrite))
                    issues.Add(new("V2_EDGE_ENDPOINT", $"edge {from} -{kind}-> {to}: {end} is not a handler, entity or ext:* id"
                        + (boxOrApp ? " (edges never target containers or apps)" : "")));
            }
            if (kind == "trigger" && Endpoint(to) && !handlers.ContainsKey(to))
                issues.Add(new("V2_EDGE_ENDPOINT", $"trigger edge {from} -> {to}: a trigger must end at a handler"));
        }

        CheckI1(record, handlers, edges, issues, rewrite);

        foreach (var (id, h) in handlers)
            foreach (var t in (h["triggers"] as JsonArray ?? []).OfType<JsonObject>())
                // servicebus: entity → handler; blob (ADDENDA-009): blobContainer → handler when the trigger names one; timer: no edge.
                if (Str(t, "kind") is "servicebus" or "blob" && t["entityId"]?.ToString() is { } entity
                    && !edges.OfType<JsonObject>().Any(e => Str(e, "kind") == "trigger" && Str(e, "from") == entity && Str(e, "to") == id))
                    issues.Add(new("V5_MISSING_TRIGGER_EDGE", $"handler {id}: no trigger edge {entity} → {id}"));
        return issues;
    }

    private static void CheckI1(JsonObject record, Dictionary<string, JsonObject> handlers, JsonArray edges, List<RegistrationIssue> issues, bool rewrite)
    {
        var allowed = (record["overrides"]?["allowBidirectional"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => (Str(a, "entity"), Str(a, "node"))).ToHashSet();
        var list = edges.OfType<JsonObject>().ToList();
        var targets = list.Where(e => Str(e, "kind") == "trigger").GroupBy(e => Str(e, "from"))
            .ToDictionary(g => g.Key, g => g.Select(e => Str(e, "to")).ToHashSet(StringComparer.Ordinal));
        foreach (var (entity, outs) in targets)
        {
            var inbound = list.Where(e => Str(e, "to") == entity && Inbound.Contains(Str(e, "kind"))).ToList();
            foreach (var node in inbound.Select(e => Str(e, "from")).Distinct().Where(outs.Contains).ToList())
            {
                if (allowed.Contains((entity, node))) continue;
                var pair = inbound.Where(e => Str(e, "from") == node).ToList();
                var ownTrigger = handlers.TryGetValue(node, out var h)
                    && (h["triggers"] as JsonArray ?? []).Any(t => t?["entityId"]?.ToString() == entity);
                if (!ownTrigger)
                {
                    issues.Add(new("I1_VIOLATION", $"{node} both sends to and is triggered by {entity}, but {entity} is not one of its handler triggers"));
                    continue;
                }
                if (pair.Any(e => Str(e, "confidence") != "code"))
                {
                    issues.Add(new("I1_UNRESOLVED", $"{node} → {entity} is {string.Join("/", pair.Select(e => Str(e, "confidence")).Distinct())}, not code-attributed; "
                        + "a handler re-sending to its own trigger must be found in code (file:line) to become a selfRequeue badge "
                        + "(fix the attribution, or list {entity, node} in overrides.allowBidirectional)"));
                    continue;
                }
                if (!rewrite)
                {
                    issues.Add(new("I1_VIOLATION", $"{node} ↔ {entity}: code self-send must be a selfRequeue badge, not an edge (regenerate the registration)"));
                    continue;
                }
                var badges = h!["selfRequeue"] as JsonArray ?? (JsonArray)(h["selfRequeue"] = new JsonArray());
                var badge = badges.OfType<JsonObject>().FirstOrDefault(b => Str(b, "entityId") == entity);
                if (badge is null) badges.Add(badge = new JsonObject { ["entityId"] = entity });
                badge["messages"] = Union(badge["messages"], pair.SelectMany(e => Strings(e["messages"])));
                badge["sources"] = Union(badge["sources"], pair.SelectMany(e => Strings(e["sources"])));
                badge["generated"] = true;
                foreach (var e in pair) edges.Remove(e);
            }
        }
    }

    private static JsonArray Union(JsonNode? existing, IEnumerable<string> more) =>
        new(Strings(existing).Concat(more).Distinct().Order(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray());

    private static IEnumerable<string> Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>();
    private static IEnumerable<JsonObject> Items(JsonObject record, string key) => (record[key] as JsonArray ?? []).OfType<JsonObject>();
    private static string Str(JsonObject o, string key) => o[key]?.ToString() ?? "";
}

/// <summary>
/// v1 (no schemaVersion, app-level nodes) → v2 in memory, for the API loader (ADDENDA-008 §11 "loader accepts v1"). One
/// resource container per app, one handler per app trigger (+ <c>*.http</c> for web apps). Trigger edges move to the
/// handler named by their label; other app edges fan out to the app's handlers (inferred-from-settings when the app has
/// more than one), except to a handler's own trigger entity: an app-level edge cannot show which handler re-sends there,
/// so that pair is left out (noted) rather than becoming a false self-requeue.
/// </summary>
public static class RegistrationUpgrade
{
    public static (JsonObject Record, IReadOnlyList<string> Notes) ToV2(JsonObject v1)
    {
        var notes = new List<string>();
        var r = (JsonObject)v1.DeepClone();
        var project = r["project"]?.ToString() ?? "";
        var overrides = r["overrides"] as JsonObject;
        var apps = (r["apps"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var appKinds = apps.Select(a => (a["id"]?.ToString() ?? "", a["kind"]?.ToString() ?? "")).ToList();

        var containers = new JsonArray();
        var handlers = new JsonArray();
        var byApp = new Dictionary<string, List<(string Id, string? Function, string? Entity)>>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            var appId = app["id"]!.ToString();
            var kind = app["kind"]?.ToString() ?? "";
            var cid = HandlerNaming.ContainerId(project, appId, kind, appKinds);
            containers.Add(new JsonObject
            {
                ["id"] = cid, ["label"] = HandlerNaming.ContainerLabel(app["name"]?.ToString() ?? appId, app["local"]?["port"]?.GetValue<int>()),
                ["kind"] = "resource", ["resourceRefs"] = new JsonArray(appId),
            });
            var list = byApp[appId] = [];
            foreach (var t in (app["triggers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var fn = t["function"]?.ToString() ?? "";
                var id = HandlerNaming.HandlerId(project, fn, overrides);
                var trigger = new JsonObject { ["kind"] = "servicebus" };
                foreach (var key in new[] { "function", "entityId", "setting", "connection", "sourceFile" })
                    if (t[key] is { } v) trigger[key] = v.DeepClone();
                handlers.Add(new JsonObject
                {
                    ["id"] = id, ["label"] = HandlerNaming.HandlerLabel(id, project, fn, overrides), ["appId"] = appId, ["containerId"] = cid,
                    ["triggers"] = new JsonArray(trigger),
                });
                list.Add((id, fn, t["entityId"]?.ToString()));
            }
            if (kind == "web")
            {
                var id = HandlerNaming.HttpHandlerId(project, appId);
                handlers.Add(new JsonObject
                {
                    ["id"] = id, ["label"] = HandlerNaming.HandlerLabel(id, project, null, overrides), ["appId"] = appId, ["containerId"] = cid,
                    ["triggers"] = new JsonArray(new JsonObject { ["kind"] = "http" }),
                });
                list.Add((id, null, null));
            }
            app.Remove("triggers");
        }

        var edges = new JsonArray();
        foreach (var e in (r["edges"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var (from, to, kind) = (e["from"]?.ToString() ?? "", e["to"]?.ToString() ?? "", e["kind"]?.ToString() ?? "");
            if (kind == "trigger" && byApp.TryGetValue(to, out var targets))
            {
                var hits = targets.Where(h => h.Entity == from && (e["label"] is null || h.Function == e["label"]!.ToString())).ToList();
                if (hits.Count == 0) { notes.Add($"trigger edge {from} → {to} matches no trigger of {to}; dropped"); continue; }
                foreach (var h in hits) edges.Add(With(e, "to", h.Id));
                continue;
            }
            if (byApp.TryGetValue(from, out var senders))
            {
                var eligible = senders.Where(h => h.Entity != to).ToList();
                if (eligible.Count < senders.Count)
                    notes.Add($"{from} → {to}: left out for {string.Join(", ", senders.Where(h => h.Entity == to).Select(h => h.Id))} (its own trigger; app-level edge cannot be attributed)");
                foreach (var h in eligible)
                {
                    var copy = With(e, "from", h.Id);
                    if (senders.Count > 1 && copy["confidence"]?.ToString() == "code") copy["confidence"] = "inferred-from-settings";
                    edges.Add(copy);
                }
                if (eligible.Count == 0) notes.Add($"{from} → {to}: no handler left to attribute it to; dropped");
                continue;
            }
            if (byApp.TryGetValue(to, out var receivers))
            {
                foreach (var h in receivers) edges.Add(With(e, "to", h.Id));
                continue;
            }
            if (from.StartsWith("app:", StringComparison.Ordinal) || to.StartsWith("app:", StringComparison.Ordinal))
            {
                notes.Add($"{from} -{kind}-> {to}: app is not registered; dropped");
                continue;
            }
            edges.Add(e.DeepClone());
        }

        var result = new JsonObject();
        foreach (var (key, value) in r)
        {
            if (key == "edges") continue;
            result[key] = value?.DeepClone();
            if (key == "env") result["schemaVersion"] = 2;
            if (key == "apps") { result["containers"] = containers; result["handlers"] = handlers; }
        }
        result["schemaVersion"] ??= 2;
        result["containers"] ??= containers;
        result["handlers"] ??= handlers;
        result["edges"] = edges;
        notes.Insert(0, $"registration {r["id"]} is schema v1 (app nodes); upgraded in memory to v2 ({handlers.Count} handlers, {containers.Count} containers) — regenerate with tools/Generate-Registration for code-attributed edges");
        return (result, notes);
    }

    private static JsonObject With(JsonObject e, string key, string value)
    {
        var copy = (JsonObject)e.DeepClone();
        copy[key] = value;
        return copy;
    }
}
