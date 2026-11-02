using System.Text.Json;

namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>
/// Read-only view of the az CLI profile (~/.azure/azureProfile.json): subscription display name → id + tenant. Only
/// used to build the resource id; no tokens are read from ~/.azure.
/// </summary>
public sealed class AzureProfile(IReadOnlyList<AzureProfile.Subscription> subscriptions)
{
    public sealed record Subscription(string Id, string Name, string TenantId, bool IsDefault);

    public IReadOnlyList<Subscription> Subscriptions => subscriptions;

    public string? DefaultTenantId => subscriptions.FirstOrDefault(s => s.IsDefault)?.TenantId ?? subscriptions.FirstOrDefault()?.TenantId;

    /// <summary>A GUID is returned as-is; a display name is looked up (case-insensitive).</summary>
    public Subscription? Find(string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        return subscriptions.FirstOrDefault(s => string.Equals(s.Id, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? subscriptions.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? (Guid.TryParse(nameOrId, out _) ? new Subscription(nameOrId, nameOrId, "", false) : null);
    }

    public static AzureProfile Load(string path)
    {
        if (path.StartsWith("~/")) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        if (!File.Exists(path)) return new AzureProfile([]);
        try
        {
            // az writes a UTF-8 BOM.
            var text = File.ReadAllText(path).TrimStart('﻿');
            using var doc = JsonDocument.Parse(text);
            var list = new List<Subscription>();
            if (doc.RootElement.TryGetProperty("subscriptions", out var subs) && subs.ValueKind == JsonValueKind.Array)
                foreach (var s in subs.EnumerateArray())
                    list.Add(new Subscription(Str(s, "id"), Str(s, "name"), Str(s, "tenantId"),
                        s.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True));
            return new AzureProfile(list);
        }
        catch (JsonException) { return new AzureProfile([]); }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
