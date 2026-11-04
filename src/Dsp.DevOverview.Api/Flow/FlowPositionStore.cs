using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.Api.Registration;

namespace Dsp.DevOverview.Api.Flow;

/// <summary>Body value of PUT /flow/positions; nullable so a missing x/y is a 400, not a silent 0.</summary>
public sealed class FlowPositionInput
{
    public double? X { get; set; }
    public double? Y { get; set; }
}

/// <summary>
/// What the store needs from the registration: key prefix <c>pos:{project}:{env}:</c>, app → container (v1 migration) and
/// handler → container (inner positions are relative to it).
/// </summary>
public sealed record FlowPositionContext(string Project, string Env, IReadOnlyDictionary<string, string> AppContainers, IReadOnlyDictionary<string, string> Parents)
{
    public string Prefix => $"pos:{Project}:{Env}:";

    /// <summary>Graph id → file key. Containers already carry the <c>container:</c> segment (§7: pos:{p}:{env}:container:{id}).</summary>
    public string Key(string graphId) => Prefix + graphId;

    public static FlowPositionContext For(ProjectRegistration r) => new(r.Project, r.Env,
        r.Containers.Where(c => c.Kind == "resource").SelectMany(c => c.ResourceRefs.Select(a => (a, c.Id)))
            .GroupBy(x => x.a).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal),
        FlowGraphBuilder.Parents(r));
}

/// <summary>
/// User-dragged positions, one JSON file per registration (<c>data/flow-positions/{registrationId}.json</c>, ADR-003 §Layout,
/// ADDENDA-008 §7/§11). v2 file: <c>{"version":2,"positions":{"pos:{project}:{env}:{graphId}":{x,y[,parent]}}}</c>.
/// <list type="bullet">
/// <item>Wire format stays <c>{graphId:{x,y}}</c>: containers and standalone nodes absolute, handler nodes relative to their
/// container. A handler's value records its <c>parent</c>; when the handler moves to another container the old value is
/// ignored (auto-layout inside the new parent) but kept in the file.</item>
/// <item>A v1 file (flat map, no <c>version</c>) is migrated on load, in memory, and written as v2 on the next PUT:
/// <c>app:X</c> → the container whose resourceRefs hold it; other ids → <c>pos:</c> keys. Idempotent.</item>
/// <item>Keys the store doesn't understand (other envs, stale ids, unmatched v1 keys) are kept verbatim, never dropped.</item>
/// <item>Reset (empty PUT) removes only this env's <c>pos:{project}:{env}:*</c> keys and rewrites the file.</item>
/// </list>
/// Never written into service repos. Writes go to a temp file and are moved over the target (atomic on the same volume).
/// </summary>
public sealed class FlowPositionStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Directory => directory;
    public string PathFor(string registrationId) => Path.Combine(directory, registrationId + ".json");

    /// <summary>This env's positions by graph id (stale-parent handler positions left out). Throws <see cref="JsonException"/> on a corrupt file.</summary>
    public IReadOnlyDictionary<string, FlowPosition> Load(string registrationId, FlowPositionContext ctx) => View(ReadPositions(registrationId, ctx), ctx);

    /// <summary>The whole file content after migration (all envs, unknown keys included). For diagnostics and tests.</summary>
    public JsonObject LoadDocument(string registrationId, FlowPositionContext ctx) =>
        new() { ["version"] = 2, ["positions"] = ReadPositions(registrationId, ctx) };

    /// <summary>
    /// Merges <paramref name="updates"/> (graph ids, already filtered to known ids) into the file, per key, last write
    /// wins; everything else in the file is kept. An empty update clears this env's keys. Returns this env's view.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FlowPosition>> MergeAsync(
        string registrationId, IReadOnlyDictionary<string, FlowPosition> updates, FlowPositionContext ctx, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            JsonObject positions;
            try { positions = ReadPositions(registrationId, ctx); }
            catch (JsonException) { positions = new JsonObject(); } // corrupt file: overwrite it

            if (updates.Count == 0)
                foreach (var key in positions.Select(kv => kv.Key).Where(k => k.StartsWith(ctx.Prefix, StringComparison.Ordinal)).ToList())
                    positions.Remove(key);
            foreach (var (id, p) in updates)
            {
                var value = new JsonObject { ["x"] = p.X, ["y"] = p.Y };
                if (ctx.Parents.TryGetValue(id, out var parent)) value["parent"] = parent;
                positions[ctx.Key(id)] = value;
            }

            var sorted = new JsonObject();
            foreach (var (key, value) in positions.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[key] = value?.DeepClone();
            System.IO.Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".{registrationId}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temp, new JsonObject { ["version"] = 2, ["positions"] = sorted }.ToJsonString(Json), ct);
                File.Move(temp, PathFor(registrationId), overwrite: true);
            }
            finally { File.Delete(temp); } // no-op after a successful move
            return View(sorted, ctx);
        }
        finally { _lock.Release(); }
    }

    private JsonObject ReadPositions(string registrationId, FlowPositionContext ctx)
    {
        var path = PathFor(registrationId);
        if (!File.Exists(path)) return new JsonObject();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException($"{path} is not a JSON object");
        return Migrate(root, ctx);
    }

    /// <summary>v1 flat map → v2 <c>positions</c>; a v2 document is returned as is (idempotent). Unknown keys survive verbatim.</summary>
    public static JsonObject Migrate(JsonObject root, FlowPositionContext ctx)
    {
        if (root["version"] is not null)
            return root["positions"] is JsonObject v2 ? (JsonObject)v2.DeepClone() : throw new JsonException("v2 positions file without a positions object");
        var result = new JsonObject();
        foreach (var (key, value) in root)
        {
            var target = key.StartsWith("pos:", StringComparison.Ordinal) ? key
                : key.StartsWith("app:", StringComparison.Ordinal) ? (ctx.AppContainers.TryGetValue(key, out var cid) ? ctx.Key(cid) : key)
                : ctx.Key(key);
            result[target] = value?.DeepClone();
        }
        return result;
    }

    private static Dictionary<string, FlowPosition> View(JsonObject positions, FlowPositionContext ctx)
    {
        var result = new Dictionary<string, FlowPosition>(StringComparer.Ordinal);
        foreach (var (key, value) in positions)
        {
            if (!key.StartsWith(ctx.Prefix, StringComparison.Ordinal) || value is not JsonObject o) continue;
            if (o["x"] is not JsonValue xv || o["y"] is not JsonValue yv || !xv.TryGetValue<double>(out var x) || !yv.TryGetValue<double>(out var y)) continue;
            var id = key[ctx.Prefix.Length..];
            // A handler moved into another container: its old relative position means nothing there (ignored, not deleted).
            if (ctx.Parents.TryGetValue(id, out var parent) && o["parent"]?.ToString() is { } stored && stored != parent) continue;
            result[id] = new FlowPosition(x, y);
        }
        return result;
    }
}
