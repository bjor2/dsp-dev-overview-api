using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsp.DevOverview.Api.Health;

namespace Dsp.DevOverview.Api.ServiceBus;

/// <summary>
/// Option (b) for the Render API: forward Service Bus endpoints to another Overview API (Overview:ServiceBus:ProxyUpstream,
/// e.g. the box API that runs under Bjor's Osilion login). Off unless ProxyUpstream is set. Status code, content type and body
/// are passed through unchanged; upstream failures become 502/504 SB_PROXY_* envelopes.
/// </summary>
public static class SbProxy
{
    public const string HttpClientName = "sb-proxy";

    // entities, peek, dlq list/retry/discard (catch-all under /sb/), purge, samples/send. Flow/positions/samples GET stay local.
    private static readonly Regex SbPath = new(@"^/api/projects/[^/]+/(sb/.+|samples/send)$", RegexOptions.IgnoreCase);

    public static bool IsProxied(PathString path) => path.HasValue && SbPath.IsMatch(path.Value!);

    public static Uri Target(string upstream, HttpRequest request) =>
        new(upstream.TrimEnd('/') + request.PathBase + request.Path.ToUriComponent() + request.QueryString.ToUriComponent());

    public static void UseSbProxy(this WebApplication app, ServiceBusOptions options)
    {
        if (!options.ProxyEnabled) return;
        app.Use(async (ctx, next) =>
        {
            if (!IsProxied(ctx.Request.Path)) { await next(); return; }
            var factory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
            await ForwardAsync(ctx, factory.CreateClient(HttpClientName), options);
        });
    }

    public static async Task ForwardAsync(HttpContext ctx, HttpClient client, ServiceBusOptions options)
    {
        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), Target(options.ProxyUpstream!, ctx.Request));
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            body.Position = 0;
            request.Content = new StreamContent(body);
            if (ctx.Request.ContentType is { } ct) request.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }
        request.Headers.Accept.ParseAdd("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ProxyTimeoutSeconds)));
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await Error(ctx, StatusCodes.Status504GatewayTimeout, "SB_PROXY_TIMEOUT",
                $"Service Bus proxy upstream did not answer within {options.ProxyTimeoutSeconds}s (Overview__ServiceBus__ProxyUpstream).");
            return;
        }
        catch (HttpRequestException ex)
        {
            await Error(ctx, StatusCodes.Status502BadGateway, "SB_PROXY_UNREACHABLE",
                $"Service Bus proxy upstream unreachable (Overview__ServiceBus__ProxyUpstream): {ex.Message.Split('\n')[0]}");
            return;
        }
        using (response)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }

    private static Task Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ApiErrorEnvelope(new ApiError(code, message, true)));
    }
}

/// <summary>Health sbReachable + dlqTotal from the upstream's GET /api/projects (healthDetail.sbReachable), cached like the local probe.</summary>
public sealed class UpstreamSbHealthProbe(IHttpClientFactory factory, OverviewOptions options, ILogger<UpstreamSbHealthProbe> log,
    TimeProvider? clock = null) : ISbHealthProbe
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private (DateTimeOffset At, Dictionary<string, SbHealth> Results)? _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SbHealth> ProbeAsync(OverviewProject project, CancellationToken ct)
    {
        var all = await AllAsync(ct);
        return all.GetValueOrDefault(project.Id, new SbHealth(false, 0));
    }

    private async Task<Dictionary<string, SbHealth>> AllAsync(CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.Health.SbProbeCacheSeconds));
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c && _clock.GetUtcNow() - c.At < ttl) return c.Results;
            var results = await FetchAsync(ct);
            _cache = (_clock.GetUtcNow(), results);
            return results;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, SbHealth>> FetchAsync(CancellationToken ct)
    {
        var sb = options.ServiceBus;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, sb.ProxyTimeoutSeconds)));
        try
        {
            var client = factory.CreateClient(SbProxy.HttpClientName);
            using var response = await client.GetAsync(sb.ProxyUpstream!.TrimEnd('/') + "/api/projects", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("SB proxy health: upstream /api/projects returned {Status}", (int)response.StatusCode);
                return [];
            }
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("SB proxy health: upstream probe failed: {Message}", ex.Message.Split('\n')[0]);
            return [];
        }
    }

    public static Dictionary<string, SbHealth> Parse(JsonElement projects)
    {
        var result = new Dictionary<string, SbHealth>();
        if (projects.ValueKind != JsonValueKind.Array) return result;
        foreach (var p in projects.EnumerateArray())
        {
            if (!p.TryGetProperty("id", out var id) || id.GetString() is not { } key) continue;
            var reachable = p.TryGetProperty("healthDetail", out var d) && d.TryGetProperty("sbReachable", out var r) && r.ValueKind == JsonValueKind.True;
            var dlq = p.TryGetProperty("dlqTotal", out var t) && t.TryGetInt64(out var n) ? n : 0;
            result[key] = new SbHealth(reachable, reachable ? dlq : 0);
        }
        return result;
    }
}
