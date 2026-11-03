# ADDENDA-007 — Lock down the Render API (P1)

Status: **Proposed. No Render changes made** (Render MCP used read-only). Date: 2026-11-03.
Related: ADR-006 (front door), ADDENDA-005 (SB auth).

## Problem
`https://dsp-dev-overview-api.onrender.com` is public, unauthenticated, and its process now holds an SP with **Azure Service Bus Data Owner** on `sb-bjoo-local-noe-dahl-shared`. Anyone with the URL can inject, receive and purge messages.

## Current state (Render MCP `list_services`, read 2026-11-03 ~10:45 Oslo time)
| Service | Id | Type | Plan | Runtime | Region |
|---|---|---|---|---|---|
| dsp-dev-overview-api | srv-dbka903bc2fs73esk78g | web_service | **free** | docker | frankfurt |
| dsp-dev-overview-web | srv-dbk8snfb9u1s73fvqdv0 | web_service | **free** | node | frankfurt |
| dsp-dev-overview-api-keepalive | crn-dbkb8i2jnfac73a4pbfg | cron_job | starter | node, `*/7 * * * *` → public `/healthz` | frankfurt |
| dsp-dev-overview (legacy) | srv-dbk8qcflk1mc73b6upig | static_site | — | — | — |
All are in workspace `tea-d7fclcs71suc738lcm0g`, region frankfurt (same private network). IP allow list = `0.0.0.0/0`. The legacy static site is out of scope; consider deleting it later (needs approval).

## Render facts (sources)
- Private services have no `onrender.com` URL and accept traffic only from services on the same private network, which means the same workspace and region. https://render.com/docs/private-services
- "**Free web services can send private network requests, but they can't receive them.**" Cron jobs and workers can send but not receive. https://render.com/docs/private-network
  → The free **web** service can call a private API. The free **API** web service cannot be reached privately while it stays on the free plan.
- The free tier ($0, 512 MB) covers web services. Paid compute starts at **Starter, $7/month** (512 MB, 0.5 CPU). I could not find a free instance type for private services; the smallest paid tier ($7/mo) is the expected floor. https://render.com/pricing (pricing page scraped; it doesn't spell out free eligibility per service type, so it's marked *likely*)
- Docker runtime works for private services (same build options as web services; private-services docs).
- Private-network port 10000 maps to a web service's primary HTTP port. A private service is reached at `<internal-host>:<its bound port>`. https://render.com/docs/private-network

## Options
| | (1) Private service | (2) Shared header secret | (3) Forwarded Entra token |
|---|---|---|---|
| Removes public exposure | **Yes** (no public URL) | No (URL still public, gated) | No (gated) |
| Cost | ~$7/mo (Starter), plus spin-down stops on free | $0 | $0 |
| Effort | Create a new service (type change = new service), swap env, update upstream | Small middleware + proxy header | App registration with API scope, token acquisition in web, JWT validation in API |
| Failure mode | — | Secret leak = full access until rotated | Misconfig of audience/issuer |
| Keepalive `/healthz` | Cron reaches it via private host (cron can send privately) | Stays public/anonymous | Stays public/anonymous |

## Recommendation (layered)
1. **Now (same day, $0): option (2), the header secret.** Every route except `/healthz` requires `X-Frontdoor-Secret`, and the API drops CORS. This closes the P1 hole immediately.
2. **Next (needs Bjor's OK for ~$7/mo): option (1).** Recreate the API as a **Private Service, Starter, Docker, frankfurt** in the same workspace. Point the web's `API_UPSTREAM` at the private host and delete the public API service. Keep the header-secret check as defence in depth.
3. **Later, optional: option (3).** Validate a forwarded Entra token (tenant `c3ac4169-9d99-4eb7-b72b-bfa11f13d825`, aud = `api://dsp-dev-overview-api`, oid/email = Bjor) if the API ever gets more than one caller.

### Render changes, step 1 (env var NAMES only)
- API service `dsp-dev-overview-api`: add `FRONTDOOR_SHARED_SECRET` and `FRONTDOOR_SHARED_SECRET_NEXT` (optional, for rotation). Remove `Cors__AllowedOrigins` (or whatever the CORS var is named) if set.
- Web service `dsp-dev-overview-web`: add `FRONTDOOR_SHARED_SECRET` (same value) and keep `API_UPSTREAM=https://dsp-dev-overview-api.onrender.com`.
- Generate a value locally with `openssl rand -base64 48` and paste it directly into the Render dashboard. It must never appear in a doc or chat.

### Render changes, step 2
- New service: type **Private Service**, plan **Starter**, runtime Docker, repo `bjor2/dsp-dev-overview-api`, branch main, region **frankfurt**, workspace `tea-d7fclcs71suc738lcm0g`. Copy every env var from the old API, including `AZURE_*`, `ServiceBus__*` and `FRONTDOOR_SHARED_SECRET*`. Bind to `PORT` (e.g. 10000).
- Web: `API_UPSTREAM=http://<private-internal-host>:10000`. Read the host from the dashboard under Connect → Internal. The private network is plain HTTP inside Render.
- Cron keepalive: point it at `http://<private-internal-host>:10000/healthz`. Private services on paid plans don't spin down, so the cron can probably be **deleted** (saves its starter cost).
- After verification, delete the old public `dsp-dev-overview-api` web service (needs approval).

## ASP.NET middleware sketch (API)
```csharp
// Program.cs, before endpoint mapping. No AddCors / UseCors anymore.
var current = builder.Configuration["FRONTDOOR_SHARED_SECRET"];
var next    = builder.Configuration["FRONTDOOR_SHARED_SECRET_NEXT"];
if (string.IsNullOrEmpty(current)) throw new InvalidOperationException("FRONTDOOR_SHARED_SECRET missing"); // fail closed

static bool Eq(string? presented, string? expected) =>
    !string.IsNullOrEmpty(presented) && !string.IsNullOrEmpty(expected) &&
    CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)));   // hashing equalises length

app.Use(async (ctx, nextMw) =>
{
    if (ctx.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)) { await nextMw(); return; }
    var presented = ctx.Request.Headers["X-Frontdoor-Secret"].ToString();
    if (Eq(presented, current) || Eq(presented, next)) { await nextMw(); return; }
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;   // no body detail
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous(); // must not touch SB or leak config
```
Rotation: (a) set `_NEXT` on the API to the new value, (b) switch the web's `FRONTDOOR_SHARED_SECRET` to the new value, (c) move the new value into the API's `FRONTDOOR_SHARED_SECRET` and clear `_NEXT`. Zero downtime.

## Node proxy header injection (web, after OIDC session check)
```js
// server/index.js — /api/* proxy, only reached by authenticated sessions (ADR-006)
app.use('/api', requireSession, async (req, res) => {
  const url = new URL(req.originalUrl.replace(/^\/api/, '/api'), process.env.API_UPSTREAM);
  const headers = { ...pickSafe(req.headers) };   // drop cookie, host, authorization, any inbound x-frontdoor-secret
  delete headers['x-frontdoor-secret'];
  headers['x-frontdoor-secret'] = process.env.FRONTDOOR_SHARED_SECRET;
  headers['x-frontdoor-user'] = req.session.user.email;     // audit only, not trusted for authz
  const r = await fetch(url, { method: req.method, headers, body: ['GET','HEAD'].includes(req.method) ? undefined : req, duplex: 'half' });
  res.status(r.status); r.headers.forEach((v, k) => k !== 'content-encoding' && res.setHeader(k, v));
  Readable.fromWeb(r.body).pipe(res);
});
```
The SPA always calls same-origin `/api/...` and never calls the API host directly, so the API needs **no CORS**. Remove `AddCors`/`UseCors`, and the browser's preflight requests to the API host will then fail, which is intended. If SSE/WebSocket console streaming (ADR-004) goes through the proxy, the header must be injected on those upgrades too.

## Verification
```bash
API=https://dsp-dev-overview-api.onrender.com
curl -s -o /dev/null -w '%{http_code}\n' $API/healthz                                  # 200
curl -s -o /dev/null -w '%{http_code}\n' $API/api/projects                             # 401
curl -s -o /dev/null -w '%{http_code}\n' -H 'X-Frontdoor-Secret: wrong' $API/api/projects  # 401
curl -s -o /dev/null -w '%{http_code}\n' -X POST $API/api/projects/customerproject/samples/send  # 401
curl -s -I -H 'Origin: https://evil.example' -X OPTIONS $API/api/projects | grep -i access-control || echo "no CORS ✔"
# Through the front door: browser login at https://dsp-dev-overview-web.onrender.com → project list loads (200).
# Unauthenticated: curl -s -o /dev/null -w '%{http_code}\n' https://dsp-dev-overview-web.onrender.com/api/projects  # 302/401
```
After step 2: `curl $API/healthz` → DNS/404 (public URL gone). The front door still works, and the cron (if kept) logs 200 on the private host.

## Unknowns
- Whether a private service can use a free instance type. Docs and pricing imply no (Starter $7/mo floor); confirm in the dashboard's create flow.
- The API repo's current CORS and env var names. I didn't read `bjor2/dsp-dev-overview-api`, so the names above are proposals.
- Whether the web proxy currently forwards to `API_UPSTREAM` or the SPA calls the API host directly. If it calls directly, the SPA must switch to same-origin `/api` first.
- Workspace plan (Hobby vs Pro) has no effect on the private network (same-workspace, same-region is enough), but billing approval is needed for the $7/mo.
- Rotating the SP secret from ADDENDA-005 is recommended, since it was exposed behind an open API (there's no evidence of misuse; check SB activity / the API logs).
