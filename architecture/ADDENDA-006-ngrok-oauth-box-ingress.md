# ADDENDA-006 — ngrok OAuth ingress to the box local stack

Status: **Proposed. ngrok not run, nothing configured.** Date: 2026-11-03.

## What actually exposes HTTP (read-only scan of dsp-customerproject)
- `rg "HttpTrigger|EventGridTrigger" dsp-customerproject` → **zero hits.** Functions (:7239) and Ingress.Functions (:7240) are ServiceBus-triggered only (plus timers if any). Nothing in Azure calls them over HTTP; they consume SB (ADDENDA-004 namespace) directly. **Do not expose 7239/7240** (only `/admin` host endpoints there, keyed by local master key).
- **Api (:5011)** is the only HTTP surface. Controllers (`src/Dahl.CustomerProject.Api/V1/Controllers`):
  - `CustomerProjectController` `tenants/{tenant}/CustomerProject`: GET `customer/{customerId}`, `customer/{customerId}/search`, `{projectId}`, `{projectId}/erpaddressid`, `customer/{customerId}/erpaddressid/{erpAddressId}`; POST create, `import`, `import-contacts`; PUT `{projectId}`.
  - `ImageController` `tenants/{tenant}/customerproject/`: GET/POST/DELETE images, POST `{projectId}/images/send-import-command`.
  - `OrderAssociationController`: POST, DELETE `customerproject/{projectId}/order/{orderId}`, POST `.../orders/populate`.
  - `EnvironmentalProfileController`: GET/POST/DELETE.
  - Auth: `AuthenticatedController` + `SetAuthorizedUser` read JWT claims (NameIdentifier, roles, `accounts`, `scp`) — i.e. callers are expected to present a **bearer token** (APIM/front-end/other DSP services), not webhooks.
- Callers from Azure dev: other DSP services / APIM / web front-ends calling the API (machine, bearer token). Human: Bjor via browser/Swagger.

## Design
**Two hostnames on one agent, both → `http://localhost:5011`**, not one-per-port (only one port matters) and not path routing (different auth needs per caller type, not per path):

1. `human` endpoint (e.g. `bjor-cp-api.<reserved-domain>`): OAuth microsoft + email gate.
2. `machine` endpoint (e.g. `bjor-cp-api-m.<reserved-domain>`): **no OAuth** (OAuth redirects break machine callers) → `jwt-validation` of Entra tokens.

### Human policy (syntax per https://ngrok.com/docs/traffic-policy/actions/oauth/)
```yaml
on_http_request:
  - actions:
      - type: oauth
        config:
          provider: microsoft        # managed app ok for start; own app → client_id/secret from ngrok vault
  - expressions:
      - "actions.ngrok.oauth.identity.email != 'bjor.byrkjedal@osilion.no'"
    actions:
      - type: deny
        config:
          status_code: 403
```
Caveat: the API itself still requires a bearer token; ngrok's session cookie does not supply one. For browser use, Swagger "Authorize" with a token is still needed, or use the machine host.

### Machine path — FINAL (Bjor via Team Lead, 2026-11-03): header secret at the ngrok edge
Topology: browser → Render web front door (Node OIDC proxy, ADR-006) → **machine host** `<MACHINE_HOST>` → box API :5011. Humans use the **human host** `<HUMAN_HOST>` (OAuth policy above). **No API code edits**; configuration lives only in the ngrok traffic policy and the web front door.

**Decision: shared header secret, not JWT validation.** The box API validates nothing itself, so an Entra token would only be checked at the edge. That would need a new API app registration, an exposed scope, client-credential token minting in Node, and a `jwt-validation` policy, all to protect a single trusted caller. The header secret is one vault entry plus one env var. JWT validation remains a later option if more callers appear or Render gets workload identity.

Names (placeholders, no values): ngrok vault `dsp-overview`, secrets `frontdoor-key` and `frontdoor-key-next` (rotation); Render web env `BOX_UPSTREAM_SECRET`; header `X-Dsp-Box-Key`.

Syntax sources: `secrets.get('vault','name')` → https://ngrok.com/docs/gateway/traffic-policy/secrets and https://ngrok.com/docs/gateway/traffic-policy/macros. `req.headers` is a lowercase-keyed `map[string][]string` → https://ngrok.com/docs/traffic-policy/variables/req. `deny` → https://ngrok.com/docs/traffic-policy/actions/deny. `remove-headers` → https://ngrok.com/docs/traffic-policy/actions/remove-headers. oauth + email check → https://ngrok.com/docs/traffic-policy/actions/oauth/

`policy-machine.yml`
```yaml
on_http_request:
  # 1. Reject unless X-Dsp-Box-Key matches current or next secret (missing header → deny; guard before [0])
  - expressions:
      - >-
        !('x-dsp-box-key' in req.headers) ||
        !(req.headers['x-dsp-box-key'][0] == secrets.get('dsp-overview', 'frontdoor-key') ||
          req.headers['x-dsp-box-key'][0] == secrets.get('dsp-overview', 'frontdoor-key-next'))
    actions:
      - type: deny
        config:
          status_code: 403
  # 2. Strip the secret before it reaches the API (and its logs)
  - actions:
      - type: remove-headers
        config:
          headers:
            - x-dsp-box-key
```

`policy-human.yml`
```yaml
on_http_request:
  - actions:
      - type: oauth
        config:
          provider: microsoft
  - expressions:
      - "actions.ngrok.oauth.identity.email != 'bjor.byrkjedal@osilion.no'"
    actions:
      - type: deny
        config:
          status_code: 403
```
Seed `frontdoor-key-next` with the same value as `frontdoor-key` (or a random placeholder) so `secrets.get` never resolves to a missing secret. This behaviour is unverified; see Unknowns.

Agent: two endpoints in `ngrok.yml` (agent config v3), started with `ngrok start --all`:
```yaml
version: 3
agent:
  authtoken: ${NGROK_AUTHTOKEN}      # env, not committed
endpoints:
  - name: dsp-box-api-machine
    url: https://<MACHINE_HOST>
    upstream: { url: 5011 }
    traffic_policy_file: ./policy-machine.yml   # or inline traffic_policy:
  - name: dsp-box-api-human
    url: https://<HUMAN_HOST>
    upstream: { url: 5011 }
    traffic_policy_file: ./policy-human.yml
```
One-off equivalent: `ngrok http 5011 --url https://<MACHINE_HOST> --traffic-policy-file policy-machine.yml` (plus a second process for the human host). Check the `traffic_policy_file` key name against the agent config docs; inline `traffic_policy:` is the safe fallback.

**Web front door upstream selection (config only):**
- Env: `API_UPSTREAM_MODE=box|render` (default `box`), `BOX_API_UPSTREAM=https://<MACHINE_HOST>`, `RENDER_API_UPSTREAM=https://dsp-dev-overview-api.onrender.com`, `BOX_UPSTREAM_SECRET`, optional `API_UPSTREAM_FALLBACK=read-only|off` (default `off`).
- `X-Dsp-Box-Key` is injected **only** on requests to the box upstream, **only after** the OIDC session check, and is never sent to Render. Any inbound `x-dsp-box-key` from the browser is stripped first.
- Box health: probe `GET <BOX>/healthz` (with header), cache the result for 30 s. Treat it as down on connect error, 5xx, or the ngrok offline error (`ERR_NGROK_3200`, detectable via the `ngrok-error-code` response header).
- No silent fallback for mutating calls (POST/PUT/DELETE: inject, retry DLQ, purge). If the box is down, return 503 `{code:"BOX_OFFLINE"}`. GET fallback to Render happens only if `API_UPSTREAM_FALLBACK=read-only`.
- Every proxied response carries `X-Dsp-Upstream: box|render` (plus `X-Dsp-Upstream-Fallback: 1` when a fallback was used). The UI shows this as an upstream badge.

**Rotation:** (1) set the vault secret `frontdoor-key-next` = new value, (2) set Render `BOX_UPSTREAM_SECRET` = new value and redeploy, (3) set `frontdoor-key` = new value, (4) re-randomise `frontdoor-key-next`. Vault updates apply to policies without an agent restart (per the secrets docs).

**Verification (run by whoever brings the tunnel up):**
```bash
M=https://<MACHINE_HOST>; H=https://<HUMAN_HOST>
curl -s -o /dev/null -w '%{http_code}\n' $M/healthz                                 # 403
curl -s -o /dev/null -w '%{http_code}\n' -H 'X-Dsp-Box-Key: wrong' $M/healthz       # 403
curl -s -o /dev/null -w '%{http_code}\n' -H "X-Dsp-Box-Key: $BOX_UPSTREAM_SECRET" $M/healthz  # 200 (value from env, never pasted)
curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' $H/healthz                 # 302 → login.microsoftonline.com (via idp.ngrok.com)
# Front door: response header X-Dsp-Upstream: box ; stop ngrok → mutating call returns 503 BOX_OFFLINE
```
Note: on the machine host `/healthz` also needs the header. The Render keepalive cron targets the Render API, not the box.

## Risks
- Machine host is a public door into a dev API with real dev SB/DB writes — keep tunnel off when not testing.
- Calls hitting the box reroute dev traffic away from real dev services only if a caller is repointed (APIM/app setting change in dev = outside our "no-edit" scope; needs Bjor/owner).
- Managed ngrok Microsoft app: personal and work accounts both accepted; the email check is the real gate.

## Unknowns
- `secrets.get` behaviour when a secret is missing; ngrok Traffic Inspector full-capture can show interpolated secrets (keep full capture off).
- Exact `<MACHINE_HOST>`/`<HUMAN_HOST>` (reserved domains).
- ngrok plan: Traffic Policy oauth/jwt-validation, reserved/custom domains, multiple endpoints, vault — plan tier needed (likely Pay-as-you-go/paid); unconfirmed.
- Which dev Azure component actually needs to call the box (none found in code).
- Token issuer/audience for the Api in dev.
- Timer triggers not exhaustively listed (irrelevant to ingress).
