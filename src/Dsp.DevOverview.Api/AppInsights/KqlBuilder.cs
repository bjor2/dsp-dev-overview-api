using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>
/// Builds the KQL for GET /logs. Pure (no I/O) so it is unit-tested directly. Everything user-supplied is either
/// validated into a literal (roles, since) or passed through <see cref="SanitizeFragment"/> (kql).
/// Columns are the classic App Insights schema (resource-centric query), not the workspace App* tables.
/// KQL gotcha (ADR-004): never name a column <c>last</c>.
/// </summary>
public static partial class KqlBuilder
{
    public static readonly string[] Tables = ["requests", "traces", "exceptions", "dependencies"];

    public const int MaxFragmentLength = 1000;

    /// <summary>Azure resource names: letters, digits, '-', '_', '.'. Anything else never reaches the query.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex RoleName();

    /// <summary>"15m", "2h", "1d" (relative since). Anything else must be an ISO date-time.</summary>
    [GeneratedRegex(@"^(\d{1,5})([smhd])$")]
    private static partial Regex Relative();

    /// <summary>A '.' starting a word = control command (.set, .drop, .show, …); member access (a.b) and numbers (0.5) don't match.</summary>
    [GeneratedRegex(@"(^|[\s|(,])\.[A-Za-z]")]
    private static partial Regex ControlCommand();

    /// <summary>Operators/functions that would pull rows from outside the role filter or call out of the resource.</summary>
    /// Words match anywhere (a literal "join" in a string is rejected too; use has_cs/contains on a substring instead).
    [GeneratedRegex(@"\b(union|join|lookup|externaldata|evaluate|materialize|invoke|fork|facet)\b|\b(workspace|app|resource|cluster|database|external_table)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    // Output columns, typed so a fragment that drops one still yields the same schema.
    private static readonly (string Column, string NullOfType)[] Projection =
    [
        ("severityLevel", "int(null)"), ("message", "''"), ("name", "''"), ("operation_Id", "''"),
        ("duration", "real(null)"), ("success", "bool(null)"), ("resultCode", "''"), ("url", "''"),
        ("type", "''"), ("target", "''"), ("data", "''"), ("outerType", "''"), ("outerMessage", "''"),
        ("problemId", "''"), ("itemCount", "int(null)"), ("customDimensions", "dynamic(null)"),
    ];

    public static IReadOnlyList<string> ParseRoles(string? roles, IReadOnlyCollection<string> registered)
    {
        if (string.IsNullOrWhiteSpace(roles)) return registered.ToList();
        var list = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return registered.ToList();
        foreach (var role in list)
        {
            if (!RoleName().IsMatch(role))
                throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_ROLE", $"'{Clip(role)}' is not a valid cloud_RoleName (letters, digits, '.', '_', '-').");
            if (!registered.Contains(role, StringComparer.Ordinal))
                throw new ApiException(StatusCodes.Status400BadRequest, "UNKNOWN_ROLE", $"'{role}' is not a registered cloudRoleName. Known: {string.Join(", ", registered)}");
        }
        return list;
    }

    /// <summary>ISO date-time (offset or Z; no offset = UTC) or relative "30m"/"2h"/"1d". Result is UTC.</summary>
    public static DateTimeOffset ParseSince(string? since, DateTimeOffset now, AppInsightsOptions o)
    {
        DateTimeOffset value;
        if (string.IsNullOrWhiteSpace(since)) return now.AddMinutes(-o.DefaultLookbackMinutes);
        if (Relative().Match(since.Trim()) is { Success: true } m)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            value = now - m.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n),
            };
        }
        else if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_SINCE", $"since '{Clip(since)}' is not an ISO date-time (2026-11-02T11:41:00Z) or a relative span (30m, 2h, 1d).");

        value = value.ToUniversalTime();
        if (value < now.AddDays(-o.MaxLookbackDays))
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_SINCE", $"since is older than {o.MaxLookbackDays} days (shared resource, 10 GB/day cap).");
        if (value > now.AddMinutes(5))
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_SINCE", "since is in the future.");
        return value;
    }

    /// <summary>
    /// Single-line pipe fragment, appended as <c>| fragment</c> after the role/time filter. Rejected: newlines/control
    /// chars, ';' (let/multi-statement), control commands (.set/.drop/.show …), triple-backtick strings, and operators
    /// that can reach rows outside the role filter or outside the resource (union, join, workspace(), externaldata …).
    /// </summary>
    public static string? SanitizeFragment(string? kql)
    {
        if (string.IsNullOrWhiteSpace(kql)) return null;
        var f = kql.Trim();
        if (f.StartsWith('|')) f = f[1..].TrimStart();
        if (f.Length == 0) return null;
        if (f.Length > MaxFragmentLength) throw Rejected($"longer than {MaxFragmentLength} characters");
        if (f.Any(c => c is '\r' or '\n' || char.IsControl(c))) throw Rejected("newlines and control characters are not allowed (single line only)");
        if (f.Contains(';')) throw Rejected("';' is not allowed (no let / multiple statements)");
        if (f.Contains("```")) throw Rejected("multi-line string literals are not allowed");
        if (ControlCommand().IsMatch(f)) throw Rejected("control commands (.set, .drop, .show, …) are not allowed");
        if (Forbidden().Match(f) is { Success: true } m) throw Rejected($"'{m.Value}' is not allowed (could read outside the project's roles or resource)");
        return f;
    }

    public static string BuildLogsQuery(IReadOnlyList<string> roles, DateTimeOffset since, string? fragment, int maxRows)
    {
        if (roles.Count == 0) throw new ApiException(StatusCodes.Status400BadRequest, "NO_ROLES", "The registration has no cloudRoleNames to filter on.");
        var sb = new StringBuilder();
        sb.Append("union withsource=SourceTable ").AppendJoin(", ", Tables).Append('\n');
        sb.Append("| where timestamp > ").Append(DateTimeLiteral(since)).Append('\n');
        sb.Append("| where cloud_RoleName in (").AppendJoin(", ", roles.Select(Quote)).Append(")\n");
        if (fragment is not null) sb.Append("| ").Append(fragment).Append('\n');
        sb.Append("| top ").Append(maxRows.ToString(CultureInfo.InvariantCulture)).Append(" by timestamp desc\n");
        sb.Append("| project timestamp, SourceTable, cloud_RoleName");
        foreach (var (column, nullOfType) in Projection)
            sb.Append(", ").Append(column).Append(" = column_ifexists(\"").Append(column).Append("\", ").Append(nullOfType).Append(')');
        sb.Append('\n');
        sb.Append("| order by timestamp asc");
        return sb.ToString();
    }

    /// <summary>The ADR-004 confirmation query (distinct cloud_RoleName in the window, with counts).</summary>
    public static string BuildRolesQuery(DateTimeOffset since) =>
        $"union {string.Join(", ", Tables)}\n| where timestamp > {DateTimeLiteral(since)}\n| summarize itemCount = count() by cloud_RoleName\n| order by cloud_RoleName asc";

    /// <summary>Cheapest query that still proves token + RBAC + resource id.</summary>
    public const string ProbeQuery = "print aiProbe = 1";

    public static string DateTimeLiteral(DateTimeOffset t) =>
        "datetime(" + t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture) + ")";

    private static string Quote(string validatedRole) => "\"" + validatedRole + "\"";

    private static ApiException Rejected(string why) =>
        new(StatusCodes.Status400BadRequest, "KQL_REJECTED", $"kql fragment rejected: {why}.");

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
