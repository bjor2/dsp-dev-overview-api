using System.Text.Json.Nodes;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>
/// <c>layoutDefaults</c> (option C, Render's disk is ephemeral): a checked-in default Flow layout in the registration,
/// <c>{"positions":{graphId:{x,y}}}</c> with containers absolute, handler nodes relative to their container and entities/ext
/// absolute. Runtime positions override it per key. The generator never owns it, so regeneration keeps it as is.
/// </summary>
public static class LayoutDefaults
{
    /// <summary>Graph ids a v2 record can show: handlers, containers, entities and ext:* edge endpoints.</summary>
    public static IReadOnlySet<string> GraphIds(JsonObject record)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "handlers", "containers", "entities" })
            foreach (var o in (record[key] as JsonArray ?? []).OfType<JsonObject>())
                if (o["id"]?.ToString() is { } id) ids.Add(id);
        foreach (var e in (record["edges"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var end in new[] { e["from"]?.ToString(), e["to"]?.ToString() })
                if (end is not null && end.StartsWith("ext:", StringComparison.Ordinal)) ids.Add(end);
        return ids;
    }

    /// <summary>Unknown ids in layoutDefaults.positions: warnings, never fatal (the Flow ignores them).</summary>
    public static IReadOnlyList<string> Warnings(JsonObject record)
    {
        if (record["layoutDefaults"]?["positions"] is not JsonObject positions) return [];
        var known = GraphIds(record);
        return positions.Select(kv => kv.Key).Where(id => !known.Contains(id))
            .Select(id => $"layoutDefaults.positions: {id} is not a handler, container, entity or ext:* id of this registration — ignored").ToList();
    }

    /// <summary>
    /// Writes <paramref name="positions"/> into <c>record.layoutDefaults.positions</c>, keys in ordinal order, values as
    /// <c>{x,y}</c> rounded to whole pixels; any <c>note</c> is kept. Returns the updated copy (key order of the record kept,
    /// layoutDefaults placed right after edges when new).
    /// </summary>
    public static JsonObject Apply(JsonObject record, IEnumerable<KeyValuePair<string, (double X, double Y)>> positions)
    {
        var sorted = new JsonObject();
        foreach (var (id, p) in positions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sorted[id] = new JsonObject { ["x"] = Round(p.X), ["y"] = Round(p.Y) };
        var defaults = record["layoutDefaults"] is JsonObject current ? (JsonObject)current.DeepClone() : new JsonObject();
        defaults["positions"] = sorted;

        var result = new JsonObject();
        var placed = false;
        foreach (var (key, value) in record)
        {
            if (key == "layoutDefaults") { result[key] = defaults; placed = true; continue; }
            result[key] = value?.DeepClone();
            if (key == "edges" && record["layoutDefaults"] is null) { result["layoutDefaults"] = defaults; placed = true; }
        }
        if (!placed) result["layoutDefaults"] = defaults;
        return result;
    }

    /// <summary>Whole pixels, as an integer JSON number (stable text, idempotent export).</summary>
    public static JsonNode Round(double v) => JsonValue.Create((long)Math.Round(v, MidpointRounding.AwayFromZero))!;
}
