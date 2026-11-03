using System.Security.Cryptography;
using System.Text;

namespace Dsp.DevOverview.Api;

/// ADDENDA-007 layer 1: every route except /healthz needs X-Frontdoor-Secret matching FRONTDOOR_SHARED_SECRET or
/// FRONTDOOR_SHARED_SECRET_NEXT (rotation). Fail closed: neither set → 503 on everything but /healthz. No dev bypass.
public static class FrontdoorSecret
{
    public const string Header = "X-Frontdoor-Secret";
    public const string CurrentVar = "FRONTDOOR_SHARED_SECRET";
    public const string NextVar = "FRONTDOOR_SHARED_SECRET_NEXT";

    public static bool Matches(string? presented, string? expected) =>
        !string.IsNullOrEmpty(presented) && !string.IsNullOrEmpty(expected) &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected))); // hashing equalises length

    public static WebApplication UseFrontdoorSecret(this WebApplication app)
    {
        var current = app.Configuration[CurrentVar];
        var next = app.Configuration[NextVar];
        var configured = !string.IsNullOrEmpty(current) || !string.IsNullOrEmpty(next);
        if (!configured)
            app.Logger.LogError("{Current}/{Next} not set: every route except /healthz returns 503 (fail closed).", CurrentVar, NextVar);

        app.Use(async (ctx, nextMw) =>
        {
            if (ctx.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)) { await nextMw(); return; }
            if (!configured)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new ApiErrorEnvelope(new ApiError("FRONTDOOR_NOT_CONFIGURED", "Front-door secret not configured", false)));
                return;
            }
            var presented = ctx.Request.Headers[Header].ToString();
            if (Matches(presented, current) || Matches(presented, next)) { await nextMw(); return; }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; // no body detail
        });
        return app;
    }
}
