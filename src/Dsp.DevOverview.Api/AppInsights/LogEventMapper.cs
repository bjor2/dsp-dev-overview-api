using System.Globalization;
using System.Text.Json;

namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>Result row (KqlBuilder projection) → LogEvent. Pure; values are exactly what App Insights returned.</summary>
public static class LogEventMapper
{
    public static LogEvent Map(LogRow r)
    {
        var table = Str(r["SourceTable"]) ?? "traces";
        var name = Str(r["name"]);
        var resultCode = Str(r["resultCode"]);
        var type = Str(r["type"]);

        var message = table switch
        {
            "exceptions" => ExceptionMessage(type, Str(r["outerMessage"]) ?? Str(r["message"])),
            "requests" => Join(name, resultCode),
            "dependencies" => Join(string.Join(" ", new[] { type is null ? null : $"[{type}]", Str(r["target"]), name }.Where(s => s is not null)), resultCode),
            _ => Str(r["message"]),
        } ?? Str(r["message"]) ?? name ?? "";

        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        void Add(string key, object? value) { if (value is not null && value is not "") details[key] = value; }
        if (table is "exceptions" or "dependencies") Add("type", type);
        Add("problemId", Str(r["problemId"]));
        Add("outerType", Str(r["outerType"]));
        Add("url", Str(r["url"]));
        Add("resultCode", resultCode);
        Add("target", table == "dependencies" ? Str(r["target"]) : null);
        Add("data", table == "dependencies" ? Str(r["data"]) : null);
        // Sampling (12.5 % in dev): itemCount > 1 = this row stands for that many telemetry items.
        if (Long(r["itemCount"]) is > 1 and var n) details["itemCount"] = n;
        if (r["customDimensions"] is JsonElement { ValueKind: JsonValueKind.Object } cd && cd.EnumerateObject().Any()) details["customDimensions"] = cd;

        return new LogEvent(
            Timestamp: Timestamp(r["timestamp"]),
            Table: table,
            CloudRoleName: Str(r["cloud_RoleName"]) ?? "",
            Severity: Long(r["severityLevel"]) is { } s and >= 0 and <= 4 ? (int)s : null,
            Message: message,
            OperationId: Str(r["operation_Id"]),
            Name: name,
            DurationMs: Double(r["duration"]),
            Success: Bool(r["success"]),
            Details: details.Count > 0 ? details : null,
            TraceItUrl: null); // TraceIt is link-only (slice 7)
    }

    /// <summary>"Microsoft.Data.SqlClient.SqlException" + "..." → "SqlException: ..." (full type stays in details.type).</summary>
    private static string? ExceptionMessage(string? type, string? message)
    {
        var shortType = type?[(type.LastIndexOf('.') + 1)..];
        return (shortType, message) switch
        {
            (null, null) => null,
            (null, _) => message,
            (_, null) => shortType,
            _ => $"{shortType}: {message}",
        };
    }

    private static string? Join(string? text, string? resultCode) =>
        text is null ? null : resultCode is null ? text : $"{text} → {resultCode}";

    /// <summary>UTC ISO with full precision, so ?since=&lt;last timestamp&gt; doesn't re-return the same row.</summary>
    public static string Timestamp(object? value) => value switch
    {
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
        DateTime d => DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) => Timestamp(d),
        _ => "",
    };

    private static string? Str(object? v) => v switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() is { Length: > 0 } s ? s : null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static long? Long(object? v) => v switch
    {
        long l => l, int i => i, double d => (long)d,
        string s when long.TryParse(s, CultureInfo.InvariantCulture, out var l) => l,
        _ => null,
    };

    private static double? Double(object? v) => v switch
    {
        double d => d, long l => l, int i => i,
        string s when double.TryParse(s, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>Classic requests.success is bool, but some ingestion paths store "True"/"False".</summary>
    private static bool? Bool(object? v) => v switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };
}
