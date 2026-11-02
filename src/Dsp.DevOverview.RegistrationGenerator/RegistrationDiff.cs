using System.Text.Json.Nodes;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>Human-readable summary of what a regeneration changes: per item for apps/entities/edges, per path elsewhere.</summary>
public static class RegistrationDiff
{
    public static IReadOnlyList<string> Describe(JsonObject? before, JsonObject after)
    {
        if (before is null) return [$"new record {after["id"]}: {Count(after, "apps")} apps, {Count(after, "entities")} entities, {Count(after, "edges")} edges"];
        var lines = new List<string>();
        Items(lines, "apps", before, after, o => o["id"]?.ToString() ?? "");
        Items(lines, "entities", before, after, o => o["id"]?.ToString() ?? "");
        Items(lines, "edges", before, after, o => EdgeLabel(o));

        var sections = before.Select(kv => kv.Key).Union(after.Select(kv => kv.Key))
            .Where(k => k is not ("apps" or "entities" or "edges" or "generatedAt"));
        foreach (var key in sections)
        {
            var b = before[key];
            var a = after[key];
            if (JsonNode.DeepEquals(b, a)) continue;
            if (key == "overrides") { lines.Add("! overrides changed (should never happen)"); continue; }
            if (b is null) { lines.Add($"+ {key}"); continue; }
            if (a is null) { lines.Add($"- {key}"); continue; }
            foreach (var path in ChangedPaths(key, b, a)) lines.Add($"~ {path}");
        }
        lines.Add($"  generatedAt {before["generatedAt"]} → {after["generatedAt"]}");
        lines.Add(JsonNode.DeepEquals(before["overrides"], after["overrides"]) ? "  overrides preserved" : "! overrides differ");
        return lines;
    }

    public static string EdgeLabel(JsonObject e) => $"{e["from"]} -{e["kind"]}-> {e["to"]} [{e["scope"]}/{e["confidence"]}]";

    private static int Count(JsonObject o, string key) => (o[key] as JsonArray)?.Count ?? 0;

    private static void Items(List<string> lines, string section, JsonObject before, JsonObject after, Func<JsonObject, string> label)
    {
        Func<JsonObject, string> key = section == "edges" ? o => RegistrationGenerator.EdgeKey(o) : o => o["id"]?.ToString() ?? "";
        var b = (before[section] as JsonArray ?? []).OfType<JsonObject>().GroupBy(key).ToDictionary(g => g.Key, g => g.First());
        var a = (after[section] as JsonArray ?? []).OfType<JsonObject>().GroupBy(key).ToDictionary(g => g.Key, g => g.First());
        int added = 0, removed = 0, changed = 0;
        var detail = new List<string>();
        foreach (var (k, item) in a)
        {
            if (!b.TryGetValue(k, out var old)) { added++; detail.Add($"  + {label(item)}"); continue; }
            if (JsonNode.DeepEquals(old, item)) continue;
            changed++;
            detail.Add($"  ~ {label(item)}: {string.Join(", ", ChangedPaths("", old, item))}");
        }
        foreach (var (k, item) in b)
            if (!a.ContainsKey(k)) { removed++; detail.Add($"  - {label(item)}"); }
        lines.Add($"{section}: {a.Count} (+{added} -{removed} ~{changed})");
        lines.AddRange(detail);
    }

    private static IEnumerable<string> ChangedPaths(string prefix, JsonNode? before, JsonNode? after)
    {
        if (before is JsonObject bo && after is JsonObject ao)
        {
            foreach (var key in bo.Select(kv => kv.Key).Union(ao.Select(kv => kv.Key)))
            {
                var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (JsonNode.DeepEquals(bo[key], ao[key])) continue;
                if (bo[key] is JsonObject && ao[key] is JsonObject)
                    foreach (var p in ChangedPaths(path, bo[key], ao[key])) yield return p;
                else yield return Describe(path, bo.ContainsKey(key) ? bo[key] : null, ao.ContainsKey(key) ? ao[key] : null, bo.ContainsKey(key), ao.ContainsKey(key));
            }
            yield break;
        }
        yield return Describe(prefix, before, after, true, true);
    }

    private static string Describe(string path, JsonNode? before, JsonNode? after, bool hadBefore, bool hasAfter)
    {
        if (!hadBefore) return $"{path} added ({Short(after)})";
        if (!hasAfter) return $"{path} removed (was {Short(before)})";
        if (before is JsonArray ba && after is JsonArray aa) return $"{path} [{ba.Count} → {aa.Count} items]";
        return $"{path} {Short(before)} → {Short(after)}";
    }

    private static string Short(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "null";
        return s.Length > 70 ? s[..67] + "..." : s;
    }
}
