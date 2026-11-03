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

### Machine path — recommended: `jwt-validation`
Validate Entra tokens at the edge (issuer = tenant that issues the DSP dev tokens, audience = the customerproject API app id URI — **unknown, read from Api appsettings/APIM when wiring**), and pass the bearer through so the Api's own claim logic works unchanged. Callers with managed identity / client credentials work without shared secrets. Check current `jwt-validation` schema at https://ngrok.com/docs/traffic-policy/actions/jwt-validation/ before writing it (not verified here).

Fallback (simpler, weaker): header secret
```yaml
on_http_request:
  - expressions:
      - "!('x-dsp-tunnel-key' in req.headers) || req.headers['x-dsp-tunnel-key'][0] != secrets.get('dsp-overview', 'tunnel-key')"
    actions:
      - type: deny
        config: { status_code: 403 }
```
Secret lives in ngrok vault `dsp-overview/tunnel-key` (name only). Requires callers to add a header → Azure services usually can't without config changes (APIM can via policy). Verify `secrets.get` syntax against ngrok vault docs.
Optional add-on: `restrict-ips` to Azure dev egress ranges (unknown).

**Recommendation:** expose only Api :5011; human host = OAuth+email; machine host = jwt-validation (fallback header secret via APIM). Never expose 7239/7240.

## Risks
- Machine host is a public door into a dev API with real dev SB/DB writes — keep tunnel off when not testing.
- Calls hitting the box reroute dev traffic away from real dev services only if a caller is repointed (APIM/app setting change in dev = outside our "no-edit" scope; needs Bjor/owner).
- Managed ngrok Microsoft app: personal and work accounts both accepted; the email check is the real gate.

## Unknowns
- ngrok plan: Traffic Policy oauth/jwt-validation, reserved/custom domains, multiple endpoints, vault — plan tier needed (likely Pay-as-you-go/paid); unconfirmed.
- Which dev Azure component actually needs to call the box (none found in code).
- Token issuer/audience for the Api in dev.
- Timer triggers not exhaustively listed (irrelevant to ingress).
