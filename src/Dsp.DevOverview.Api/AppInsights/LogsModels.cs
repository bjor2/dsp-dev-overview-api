using System.Text.Json.Serialization;

namespace Dsp.DevOverview.Api.AppInsights;

// LogEvent from architecture/HANDOFF-ui-ux-data-contracts.md, field for field. Optional fields are omitted when unset.

public sealed record LogEvent(
    string Timestamp,
    string Table,
    string CloudRoleName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Severity,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Details,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceItUrl);

/// <summary>One result row: column name → value (string, DateTimeOffset, double, long, bool, JsonElement or null).</summary>
public sealed class LogRow(IReadOnlyDictionary<string, object?> values)
{
    public object? this[string column] => values.GetValueOrDefault(column);
    public IReadOnlyDictionary<string, object?> Values => values;
}

/// <summary>GET /logs/roles: distinct cloud_RoleName in the window vs the registration (ADR-004 confirmation query).</summary>
public sealed record ObservedRole(string CloudRoleName, long Count, bool Registered, bool MatchesProject);

public sealed record RoleConfirmation(
    string ResourceId, string Query, string Since, IReadOnlyList<ObservedRole> Observed,
    IReadOnlyList<string> RegisteredMissing, IReadOnlyList<string> Unregistered);
