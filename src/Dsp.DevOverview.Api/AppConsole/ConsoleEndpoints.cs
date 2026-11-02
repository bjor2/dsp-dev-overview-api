using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Dsp.DevOverview.Api.AppConsole;

/// <summary>Slice 4 routes. App ids contain ':' (app:api, docker:dsp-shared-sql-1): raw and %3A-encoded both work (catch-all, like sb).</summary>
public static class ConsoleEndpoints
{
    private static readonly string[] Actions = ["stream", "start", "stop", "restart"];

    public static void MapConsole(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id}/console", (string id, ConsoleManager m, CancellationToken ct) => m.StatusAsync(id, ct));

        api.MapGet("/projects/{id}/console/{**path}", async (string id, string path, HttpContext ctx, ConsoleManager m,
            IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ConsoleOptions options) =>
        {
            var (action, appId) = Split(path);
            if (action != "stream") throw NotFound(path);
            await StreamAsync(ctx, id, appId, m, json.Value.SerializerOptions, TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatSeconds)));
        });

        api.MapPost("/projects/{id}/console/{**path}", async (string id, string path, ConsoleManager m, CancellationToken ct) =>
            Split(path) switch
            {
                ("start", var appId) => Results.Ok(await m.StartAsync(id, appId, ct)),
                ("stop", var appId) => Results.Ok(await m.StopAsync(id, appId, ct)),
                ("restart", var appId) => Results.Ok(await m.RestartAsync(id, appId, ct)),
                _ => throw NotFound(path),
            });
    }

    /// <summary>"app:api/start" or "app%3Aapi/start" → ("start", "app:api").</summary>
    public static (string Action, string AppId) Split(string path)
    {
        foreach (var action in Actions)
            if (path.EndsWith("/" + action, StringComparison.Ordinal))
                return (action, Uri.UnescapeDataString(path[..^(action.Length + 1)]));
        return ("", path);
    }

    private static ApiException NotFound(string path) =>
        new(StatusCodes.Status404NotFound, "NOT_FOUND", $"Unknown console route '{path}' (expected <appId>/stream|start|stop|restart).");

    /// <summary>Last-Event-ID header (EventSource reconnect) or ?lastEventId= (first connect from a client that resumes).</summary>
    public static long? LastEventId(HttpRequest request)
    {
        var raw = request.Headers["Last-Event-ID"].FirstOrDefault() ?? request.Query["lastEventId"].FirstOrDefault();
        return long.TryParse(raw, out var seq) && seq >= 0 ? seq : null;
    }

    private static async Task StreamAsync(HttpContext ctx, string projectId, string appId, ConsoleManager m, JsonSerializerOptions json, TimeSpan heartbeat)
    {
        var ct = ctx.RequestAborted;
        // Throws ApiException (404) before the response starts, so the error envelope still applies.
        var (replay, live, subscription, attached) = await m.SubscribeAsync(projectId, appId, LastEventId(ctx.Request), ct);
        using var _ = subscription;

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var body = ctx.Response.Body;
        async Task Write(string text) => await body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        async Task Chunk(ConsoleChunk c) => await Write($"id: {c.Seq}\ndata: {JsonSerializer.Serialize(c, json)}\n\n");

        try
        {
            await Write("retry: 2000\n\n");
            foreach (var c in replay) await Chunk(c);
            await Heartbeat();
            await body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(heartbeat);
                try
                {
                    if (!await live.WaitToReadAsync(wait.Token)) break;
                    while (live.TryRead(out var c)) await Chunk(c);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Heartbeat();
                }
                await body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // client went away
        catch (IOException) when (ct.IsCancellationRequested) { }

        // Named event, so EventSource.onmessage only ever sees ConsoleChunks.
        Task Heartbeat() => Write($"event: heartbeat\ndata: {JsonSerializer.Serialize(new { appId, attached = attached(), timestamp = DateTimeOffset.UtcNow }, json)}\n\n");
    }
}
