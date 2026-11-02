namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>Slice 5 routes (ADR-004). Read-only; App Insights auth is the developer's own Azure identity.</summary>
public static class LogsEndpoints
{
    public static void MapLogs(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id}/logs", (string id, string? roles, string? since, string? kql, int? max, LogsService s, CancellationToken ct) =>
            s.LogsAsync(id, roles, since, kql, max, ct));

        api.MapGet("/projects/{id}/logs/roles", (string id, string? since, LogsService s, CancellationToken ct) =>
            s.ConfirmRolesAsync(id, since, ct));
    }

    public static IServiceCollection AddAppInsightsLogs(this IServiceCollection services, AppInsightsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(_ => AzureProfile.Load(options.AzureProfilePath));
        services.AddSingleton<ILogsQuery, AzureLogsQuery>();
        services.AddSingleton<LogsService>();
        services.AddSingleton<IAiQueryProbe>(sp => sp.GetRequiredService<LogsService>());
        return services;
    }
}
