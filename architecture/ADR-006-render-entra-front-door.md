# ADR-006 — Render front door gated by Entra ID (v1)
Status: **Accepted** (2026-11-02, CoS/Bjor brief). Scope: cloud-hosted DSP Dev Overview. Zero edits to dsp-customerproject or other DSP service repos.

## Decision
- **Host:** one Render **Web Service** (Node 20+, Express) in workspace **CRM_project** (`tea-d7fclcs71suc738lcm0g`), repo `https://github.com/bjor2/dsp-dev-overview-web`.
- **Auth:** **confidential-client OIDC** (auth-code + PKCE) against Entra ID, **single-tenant (Osilion)**, via `openid-client` (or `@azure/msal-node`) on the server. Server-side session; cookie `httpOnly; Secure; SameSite=Lax`.
- **Everything is behind the session:** built SPA assets (`index.html`, JS, CSS) and `/api/*` are served **only** to authenticated sessions. Only `/healthz` and `/auth/*` are public.
- **API:** Node reverse-proxies `/api/*` → `API_UPSTREAM` (overview Minimal API). The overview API is **never** exposed anonymously on Render (private service or not deployed; see Unknowns). Gateway strips client auth headers and adds `X-Overview-User: <email>`.
- **Who can sign in (v1):** Enterprise app **User assignment required = Yes**, assigned **only** `bjor.byrkjedal@osilion.no`. Defense in depth: server checks `ALLOWED_EMAILS` (match on `preferred_username`/`email`, case-insensitive) and `tid == AZURE_AD_TENANT_ID`; mismatch → 403.
- **Scopes:** `openid profile email offline_access`. No Graph in v1.
- **App registration:** **new** app `dsp-dev-overview-web`, platform **Web** (confidential), client secret in Render env (move to certificate later). Not a public SPA client.

## Alternatives rejected
1. **Render Static Site + MSAL SPA-only** — rejected. Static Site serves HTML/JS publicly; MSAL only hides UI after JS runs. Assets (and any baked config) remain downloadable unauthenticated → does not meet "requires Entra login to see the page". Also leaves the API needing its own token validation.
2. **MSAL SPA on Web Service with public assets** — same flaw.
3. **Render-level basic auth / IP allowlist** — not Entra, no identity.
4. **Entra on the .NET API itself (Easy Auth-style)** — viable later, but Render has no Easy Auth; more moving parts than a thin Node gate for v1.

## Session
- Store: signed cookie session (`express-session` + memory store is OK for 1 instance on free/starter; use `cookie-session` with encrypted tokens or Render Key Value if scaling >1 or restarts must not log out).
- Tokens stay server-side; browser only sees session cookie. Idle 8 h; refresh via `offline_access`.
- `app.set('trust proxy', 1)` so Secure cookies work behind Render's TLS.
- CSRF: SameSite=Lax + require `Origin` match on non-GET `/api/*` (inject/retry/purge are mutating).

## Relation to ADR-000
ADR-000 local anonymous stays valid **only** for `127.0.0.1:5090` + Vite dev. Cloud/Render path is always Entra-gated. The gateway can also run locally (`http://localhost:3000`) proxying to `http://127.0.0.1:5090` for bring-up.

## Expansion path (not v1)
Assign an Entra **group** (e.g. `dsp-dev-overview-users`) to the enterprise app and set `ALLOWED_EMAILS` empty → rely on assignment. Whole tenant = assignment required **No** — explicit decision needed; not in v1.

## Unknowns
- ~~Osilion tenant ID~~ **Locked:** Osilion tenant `c3ac4169-9d99-4eb7-b72b-bfa11f13d825` (login authority for `bjor.byrkjedal@osilion.no`). **Do not** use Dahl tenant for login — Dahl (`e339bd4b-…`) is App Insights / Azure resource tenant only.
- Final `*.onrender.com` URL (after service create) → update redirect URIs.
- Where the overview API runs for Render: Render **Private Service** (needs SB/App Insights creds as Render secrets) vs gateway-only to a reachable dev host. Until decided, `/api/*` returns 503 "upstream not configured" — never anonymous.
- Free tier sleeps (cold start ~30–60 s) — acceptable for v1; Starter if annoying.
