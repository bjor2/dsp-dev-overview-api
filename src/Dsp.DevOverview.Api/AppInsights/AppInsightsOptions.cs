namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>
/// Overview:AppInsights (ADR-004). The target resource comes from the registration's observability.appInsights block;
/// these settings only override it. Auth is always DefaultAzureCredential (the developer's own identity), never a
/// connection string or key.
/// </summary>
public sealed class AppInsightsOptions
{
    /// <summary>Subscription id (GUID). Wins over the registration's subscription (name or id).</summary>
    public string? SubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
    /// <summary>App Insights component name, e.g. appin-870-dev-euw-dahl-shared.</summary>
    public string? Resource { get; set; }
    /// <summary>Tenant for DefaultAzureCredential. Empty = tenant of the subscription in azureProfile.json, else the credential's default.</summary>
    public string? TenantId { get; set; }
    /// <summary>az CLI profile used to resolve a subscription display name to its id. "~" = $HOME.</summary>
    public string AzureProfilePath { get; set; } = "~/.azure/azureProfile.json";

    /// <summary>Lookback when ?since= is omitted.</summary>
    public int DefaultLookbackMinutes { get; set; } = 30;
    /// <summary>?since= older than this is rejected (cost guard on a shared, 10 GB/day-capped resource).</summary>
    public int MaxLookbackDays { get; set; } = 7;
    public int DefaultMaxRows { get; set; } = 200;
    public int MaxRowsCap { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>ProjectSummary.healthDetail.aiQueryOk: background probe, result cached for ProbeTtlSeconds.</summary>
    public bool ProbeEnabled { get; set; } = true;
    public int ProbeTtlSeconds { get; set; } = 120;

    /// <summary>Skip the IMDS probe on a dev box (no managed identity; it only adds latency to every failed attempt).</summary>
    public bool ExcludeManagedIdentity { get; set; } = true;
}
