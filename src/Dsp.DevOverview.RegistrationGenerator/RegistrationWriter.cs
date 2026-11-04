namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>Writes a generated record: the previous file goes to {historyDir}/{id}-{yyyyMMdd-HHmmss}.json, then an atomic replace.</summary>
public static class RegistrationWriter
{
    /// <returns>The backup path, or null when there was no previous file or nothing changed.</returns>
    public static string? Write(string path, GenerationResult result, string historyDir, DateTimeOffset now) =>
        WriteJson(path, result.ToJson(), result.Record["id"]!.ToString(), historyDir, now);

    /// <summary>Same backup + atomic replace for any serialized record (export-layout).</summary>
    public static string? WriteJson(string path, string json, string id, string historyDir, DateTimeOffset now)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string? backup = null;
        if (File.Exists(full))
        {
            if (File.ReadAllText(full) == json) return null;
            Directory.CreateDirectory(historyDir);
            backup = Path.Combine(historyDir, $"{id}-{now:yyyyMMdd-HHmmss}.json");
            File.Copy(full, backup, overwrite: true);
        }
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, full, overwrite: true);
        return backup;
    }

    /// <summary>
    /// The registration file the API loads for <paramref name="id"/>: the Overview:Registrations entry of
    /// src/Dsp.DevOverview.Api/appsettings.json named {id}.json, else architecture/registrations/{id}.json.
    /// </summary>
    public static string LoadedRegistrationPath(string overviewRoot, string id)
    {
        var appsettings = Path.Combine(overviewRoot, "src", "Dsp.DevOverview.Api", "appsettings.json");
        if (File.Exists(appsettings)
            && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(appsettings))?["Overview"]?["Registrations"] is System.Text.Json.Nodes.JsonArray list)
            foreach (var entry in list)
                if (entry?.ToString() is { } rel && Path.GetFileName(rel) == $"{id}.json")
                    return Path.GetFullPath(Path.Combine(overviewRoot, rel));
        return Path.Combine(overviewRoot, "architecture", "registrations", $"{id}.json");
    }
}
