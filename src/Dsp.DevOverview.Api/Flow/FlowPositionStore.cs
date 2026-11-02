using System.Text.Json;

namespace Dsp.DevOverview.Api.Flow;

/// <summary>Body value of PUT /flow/positions; nullable so a missing x/y is a 400, not a silent 0.</summary>
public sealed class FlowPositionInput
{
    public double? X { get; set; }
    public double? Y { get; set; }
}

/// <summary>
/// User-dragged node positions, one JSON file per project (<c>data/flow-positions/{projectId}.json</c>, ADR-003 §Layout).
/// Never written into service repos. Writes go to a temp file and are moved over the target (atomic on the same volume).
/// </summary>
public sealed class FlowPositionStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Directory => directory;
    public string PathFor(string projectId) => Path.Combine(directory, projectId + ".json");

    /// <summary>Stored positions, or empty when nothing is saved. Throws <see cref="JsonException"/> on a corrupt file.</summary>
    public IReadOnlyDictionary<string, FlowPosition> Load(string projectId)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path)) return new Dictionary<string, FlowPosition>();
        return JsonSerializer.Deserialize<Dictionary<string, FlowPosition>>(File.ReadAllText(path), Json) ?? new Dictionary<string, FlowPosition>();
    }

    /// <summary>
    /// Merges <paramref name="updates"/> over the stored map (per node, last write wins) and drops ids not in
    /// <paramref name="knownNodeIds"/>. An empty update clears everything (Reset layout → auto). Returns the saved map.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FlowPosition>> MergeAsync(
        string projectId, IReadOnlyDictionary<string, FlowPosition> updates, IReadOnlySet<string> knownNodeIds, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var path = PathFor(projectId);
            if (updates.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path); // File.Delete throws if the directory is missing
                return new SortedDictionary<string, FlowPosition>(StringComparer.Ordinal);
            }

            IReadOnlyDictionary<string, FlowPosition> existing;
            try { existing = Load(projectId); }
            catch (JsonException) { existing = new Dictionary<string, FlowPosition>(); } // corrupt file: overwrite it

            var merged = new SortedDictionary<string, FlowPosition>(StringComparer.Ordinal);
            foreach (var (id, p) in existing.Concat(updates))
                if (knownNodeIds.Contains(id)) merged[id] = p;

            System.IO.Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".{projectId}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(merged, Json), ct);
                File.Move(temp, path, overwrite: true);
            }
            finally { File.Delete(temp); } // no-op after a successful move
            return merged;
        }
        finally { _lock.Release(); }
    }
}
