using System.Net.Sockets;
using Azure;
using Azure.Identity;

namespace Dsp.DevOverview.Api.AppInsights;

/// <summary>
/// Maps Azure SDK failures onto the API error envelope. Never swallowed into an empty result: an empty LogEvent[]
/// always means "the query ran and matched nothing".
/// </summary>
public static class AppInsightsErrors
{
    public const string AuthHint = "Sign in so DefaultAzureCredential finds a token: install the az CLI and run 'az login' " +
        "(tenant e339bd4b-…), or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for a service principal with Reader/Monitoring Reader on the App Insights resource.";

    /// <param name="userKql">True when the request carried a kql fragment, so a 400 from the service is the caller's query.</param>
    public static ApiException Map(Exception ex, bool userKql) => ex switch
    {
        ApiException api => api,
        CredentialUnavailableException => new(StatusCodes.Status503ServiceUnavailable, "AI_AUTH_FAILED",
            $"No Azure credential available. {AuthHint} ({First(ex.Message)})"),
        AuthenticationFailedException => new(StatusCodes.Status503ServiceUnavailable, "AI_AUTH_FAILED",
            $"Azure authentication failed. {AuthHint} ({First(ex.Message)})"),
        RequestFailedException { Status: 401 } => new(StatusCodes.Status502BadGateway, "AI_AUTH_FAILED",
            $"App Insights rejected the token (401). {AuthHint}"),
        RequestFailedException { Status: 403 } => new(StatusCodes.Status502BadGateway, "AI_FORBIDDEN",
            "Signed in, but the identity has no read access to the App Insights resource (403). Needs Reader or Monitoring Reader."),
        RequestFailedException { Status: 404 } => new(StatusCodes.Status502BadGateway, "AI_RESOURCE_NOT_FOUND",
            $"App Insights resource not found (404): check subscription / resource group / name. {First(ex.Message)}"),
        RequestFailedException { Status: 400 } when userKql => new(StatusCodes.Status400BadRequest, "KQL_INVALID",
            $"App Insights rejected the query (check the kql fragment): {First(ex.Message)}"),
        RequestFailedException { Status: 400 } => new(StatusCodes.Status502BadGateway, "AI_QUERY_FAILED",
            $"App Insights rejected the query: {First(ex.Message)}"),
        RequestFailedException { Status: 429 } => new(StatusCodes.Status503ServiceUnavailable, "AI_THROTTLED",
            "App Insights throttled the query (429). Poll less often.", retryable: true),
        RequestFailedException { Status: >= 500 } => new(StatusCodes.Status502BadGateway, "AI_UPSTREAM_ERROR",
            $"App Insights returned {((RequestFailedException)ex).Status}: {First(ex.Message)}", retryable: true),
        RequestFailedException { Status: 0 } or HttpRequestException or SocketException => new(StatusCodes.Status503ServiceUnavailable, "AI_UNREACHABLE",
            $"App Insights unreachable: {First(ex.Message)}", retryable: true),
        TimeoutException or OperationCanceledException => new(StatusCodes.Status503ServiceUnavailable, "AI_UNREACHABLE",
            "App Insights query timed out.", retryable: true),
        RequestFailedException rf => new(StatusCodes.Status502BadGateway, "AI_QUERY_FAILED",
            $"App Insights returned {rf.Status}: {First(ex.Message)}"),
        _ => new(StatusCodes.Status502BadGateway, "AI_QUERY_FAILED", $"{ex.GetType().Name}: {First(ex.Message)}"),
    };

    /// <summary>SDK messages are multi-line (headers, per-credential detail); the first lines are enough for the UI.</summary>
    private static string First(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3);
        var s = string.Join(" ", lines);
        return s.Length <= 400 ? s : s[..400] + "…";
    }
}
