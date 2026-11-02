using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>The only seam to Azure Monitor. Tests replace it; exceptions are Azure SDK exceptions (mapped by AppInsightsErrors).</summary>
public interface ILogsQuery
{
    Task<IReadOnlyList<LogRow>> QueryResourceAsync(string resourceId, string kql, DateTimeOffset from, CancellationToken ct);
}

/// <summary>LogsQueryClient.QueryResourceAsync on the App Insights component with DefaultAzureCredential (no connection strings).</summary>
public sealed class AzureLogsQuery : ILogsQuery
{
    private readonly AppInsightsOptions _options;
    private readonly AzureProfile _profile;
    private readonly Lazy<LogsQueryClient> _client;

    public AzureLogsQuery(AppInsightsOptions options, AzureProfile profile)
    {
        _options = options;
        _profile = profile;
        _client = new Lazy<LogsQueryClient>(Create);
    }

    private LogsQueryClient Create()
    {
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = _options.TenantId is { Length: > 0 } t ? t : _profile.DefaultTenantId,
            ExcludeManagedIdentityCredential = _options.ExcludeManagedIdentity,
            ExcludeInteractiveBrowserCredential = true,
        });
        var clientOptions = new LogsQueryClientOptions();
        clientOptions.Retry.MaxRetries = 1;
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        return new LogsQueryClient(credential, clientOptions);
    }

    public async Task<IReadOnlyList<LogRow>> QueryResourceAsync(string resourceId, string kql, DateTimeOffset from, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds + 5));
        // The KQL carries the exact filter; the time range only bounds the scan (a minute of slack for clock skew).
        var range = new QueryTimeRange(from.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var response = await _client.Value.QueryResourceAsync(new ResourceIdentifier(resourceId), kql, range,
            new LogsQueryOptions { ServerTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) }, cts.Token);

        var table = response.Value.Table;
        var rows = new List<LogRow>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var values = new Dictionary<string, object?>(table.Columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < table.Columns.Count; i++)
                values[table.Columns[i].Name] = Read(row, i, table.Columns[i].Type);
            rows.Add(new LogRow(values));
        }
        return rows;
    }

    private static object? Read(LogsTableRow row, int i, LogsColumnType type)
    {
        if (row[i] is null) return null;
        if (type == LogsColumnType.Datetime) return row.GetDateTimeOffset(i);
        if (type == LogsColumnType.Bool) return row.GetBoolean(i);
        if (type == LogsColumnType.Int) return (long?)row.GetInt32(i);
        if (type == LogsColumnType.Long) return row.GetInt64(i);
        if (type == LogsColumnType.Real) return row.GetDouble(i);
        if (type == LogsColumnType.Dynamic)
            return row.GetDynamic(i) is { } bin && bin.ToMemory().Length > 0 ? JsonDocument.Parse(bin).RootElement.Clone() : null;
        return row.GetString(i);
    }
}
