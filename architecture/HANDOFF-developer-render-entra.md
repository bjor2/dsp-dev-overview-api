# Handoff — Developer: Render front door + Entra (ADR-006)
Repo: `https://github.com/bjor2/dsp-dev-overview-web`. Backend stays in `/workspace/dsp/dev-overview`. Do not edit dsp-customerproject.

## 1. Entra app registration (Osilion tenant `c3ac4169-9d99-4eb7-b72b-bfa11f13d825`)

Login authority: `https://login.microsoftonline.com/c3ac4169-9d99-4eb7-b72b-bfa11f13d825/v2.0`. Dahl tenant is **not** the login authority (App Insights only).

| Field | Value |
|---|---|
| Name | `dsp-dev-overview-web` (new, dedicated) |
| Supported account types | Single tenant (Osilion only) |
| Platform | **Web** |
| Redirect URIs | `https://<render-service>.onrender.com/auth/callback`, `http://localhost:3000/auth/callback` |
| Front-channel logout URL | `https://<render-service>.onrender.com/auth/logout` |
| Implicit/hybrid tokens | **Off** (no ID/access token checkboxes) |
| Client secret | create, 6–12 mo expiry → Render env; record expiry |
| API permissions | Microsoft Graph delegated `openid`, `profile`, `email`, `offline_access` (default `User.Read` can be removed) |
| Optional claims (ID token) | `email`, `preferred_username` |
| Enterprise app → Properties | **Assignment required = Yes** |
| Enterprise app → Users and groups | assign **only** `bjor.byrkjedal@osilion.no` |

Authority: `https://login.microsoftonline.com/${AZURE_AD_TENANT_ID}/v2.0` (never `common`/`organizations`).

## 2. Repo layout
```
server/index.js      Express: session, /auth/login|callback|logout, /healthz, requireAuth, /api proxy, static
web/                 SPA build (copy/build of dev-overview Vite app; VITE_API_BASE=/api)
package.json
render.yaml          optional blueprint
```
Libs: `express`, `express-session` (or `cookie-session`), `openid-client` (or `@azure/msal-node`), `http-proxy-middleware`, `helmet`.

Route order: `/healthz` (public, 200 text) → `/auth/*` (public) → `requireAuth` (unauth: HTML → 302 `/auth/login`; `/api` → 401) → `/api/*` proxy → `express.static(web/dist)` → SPA fallback `index.html`. Nothing static is mounted before `requireAuth`.

Callback checks: `state`, `nonce`, PKCE; `tid === AZURE_AD_TENANT_ID`; email in `ALLOWED_EMAILS` (if set) else 403 page. `session.regenerate()` after login. Logout: destroy session → redirect to `https://login.microsoftonline.com/<tid>/oauth2/v2.0/logout?post_logout_redirect_uri=<PUBLIC_BASE_URL>/`.

## 3. Render env vars
| Var | Value |
|---|---|
| `AZURE_AD_TENANT_ID` | **`c3ac4169-9d99-4eb7-b72b-bfa11f13d825`** (Osilion). Never Dahl `e339bd4b-…` — that tenant is App Insights only |
| `AZURE_AD_CLIENT_ID` | app (client) id |
| `AZURE_AD_CLIENT_SECRET` | secret (sync: false) |
| `SESSION_SECRET` | 32+ random bytes (generateValue) |
| `ALLOWED_EMAILS` | `bjor.byrkjedal@osilion.no` (comma list) |
| `API_UPSTREAM` | overview API base URL (Render private service `http://<name>:5090` or blank → 503) |
| `PUBLIC_BASE_URL` | `https://<render-service>.onrender.com` |
| `NODE_ENV` | `production` |

## 4. Render service
- Workspace **CRM_project** `tea-d7fclcs71suc738lcm0g`; type **Web Service** (NOT Static Site); runtime Node; plan free (Starter if cold start hurts); region Frankfurt.
- Build: `npm ci && npm run build` (builds `web/` → `web/dist`). Start: `node server/index.js` (listen on `process.env.PORT`).
- Health check path: `/healthz`. Auto-deploy from `main`.
- After create: put real URL into Entra redirect URIs + `PUBLIC_BASE_URL`.

## 5. Tester must verify
1. Incognito `GET /` → 302 to `login.microsoftonline.com/<osilion tid>/...`.
2. Unauth `GET /assets/<any>.js` and `/index.html` → redirect, not file content; `GET /api/...` → 401.
3. `GET /healthz` → 200 without auth.
4. Login as `bjor.byrkjedal@osilion.no` → SPA loads, `/api/projects` works via proxy.
5. Other Osilion user (not assigned) → Entra AADSTS50105 denied; if assignment toggled off, server 403 via `ALLOWED_EMAILS`.
6. External/other-tenant account → rejected (single-tenant authority).
7. Cookie flags: `HttpOnly; Secure; SameSite=Lax`; no tokens in browser storage.
8. Logout → session cleared, `/` redirects to login again.
9. Overview API not reachable publicly on any Render URL.

## Unknowns
Tenant ID **locked** `c3ac4169-9d99-4eb7-b72b-bfa11f13d825` (Osilion). Still open: final Render URL, API_UPSTREAM hosting (private service vs none) — see ADR-006.
