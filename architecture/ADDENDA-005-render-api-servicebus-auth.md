# ADDENDA-005 — Render API → Service Bus auth (P0: SB_UNREACHABLE)

Status: **Proposed — CoS must get Bjor's OK before any command below is run.** Nothing has been created.
Date: 2026-11-03. Related: ADR-005, ADR-006, ADDENDA-004. Rule: AAD only, no SAS / emulator connection strings.

## Verified facts (read-only `az ... list`, box az login)
- Box az login tenant: **Osilion `c3ac4169-9d99-4eb7-b72b-bfa11f13d825`**, subscription "Azure subscription 1".
- `sb-bjoo-local-noe-dahl-shared` is visible there: subscription `75c077b1-ce33-40de-a941-76aef97c6c9d`, rg `rg-bjoo-local-noe-dahl-shared`.
  → The namespace lives in the **Osilion tenant**, not Dahl. **No cross-tenant issue**: an SP in Osilion can be granted directly. (Dahl `e339bd4b-…` stays App Insights only.)
- `setup-local-infrastructure.ps1` creates rg `rg-<dev4>-local-noe-dahl-shared` (norwayeast) in whatever account is active (refuses `870Z` accounts) and pulls `RootManageSharedAccessKey` SAS into user-secrets — Overview must **not** reuse that.
- Unknown: whether the namespace has `disableLocalAuth` set; irrelevant for AAD path.

## Options
(a) Dedicated Entra SP (Osilion tenant), RBAC on the single namespace, creds in Render env → `ClientSecretCredential` / `EnvironmentCredential` (via DefaultAzureCredential).
(b) Render API proxies SB calls to the box API running under Bjor's az login.

| | (a) SP on namespace | (b) Proxy to box |
|---|---|---|
| Box uptime | Independent | Render broken whenever box/tunnel is down |
| Security / blast radius | One namespace, data-plane (+ entity mgmt if Owner); secret on Render | Box API must be publicly exposed; acts with **Bjor's full user identity** (all subs) |
| Effort | ~6 commands + env vars; code already uses Azure SDK | Extra proxy layer, tunnel, auth between Render↔box |
| Secret rotation | Client secret, 6–12 mo expiry, `az ad app credential reset` | No SP secret, but needs a shared Render↔box secret anyway |
| Latency | Render → Azure direct | Render → tunnel → box → Azure (+100s ms) |

**Recommendation: (a)**, scoped to the one namespace. Later upgrade: federated credential (workload identity) if Render supports OIDC tokens — unknown, so secret for v1.

## Role choice
- `Azure Service Bus Data Sender` + `Data Receiver`: send, receive/peek, DLQ receive/resubmit. **Not enough** for `ServiceBusAdministrationClient.GetQueueRuntimeProperties` / `GetSubscriptionRuntimeProperties` / list entities (needs `Microsoft.ServiceBus/*/manage`-equivalent data action).
- `Azure Service Bus Data Owner`: includes the above + management ops (runtime counts used by SB tab / Flow `dlqHot`, ADDENDA-003; purge via receive).
- **Pick Data Owner** at namespace scope (personal dev namespace; Overview needs counts). Alternative if counts are dropped: Sender+Receiver only.

## Exact commands (DO NOT RUN until Bjor approves via CoS)
```bash
az login --tenant c3ac4169-9d99-4eb7-b72b-bfa11f13d825
az account set --subscription 75c077b1-ce33-40de-a941-76aef97c6c9d

# SP without role (--skip-assignment is deprecated; omitting --role/--scopes = no assignment)
az ad sp create-for-rbac --name dsp-dev-overview-render-sb --years 1 \
  --query "{appId:appId, tenant:tenant}" -o json
#  -> the command also prints `password`; paste it straight into Render, never into docs/chat.
#  Equivalent alternative: az ad app create --display-name dsp-dev-overview-render-sb
#                          az ad sp create --id <appId>
#                          az ad app credential reset --id <appId> --years 1

NS_ID=$(az servicebus namespace show -g rg-bjoo-local-noe-dahl-shared \
  -n sb-bjoo-local-noe-dahl-shared --query id -o tsv)

az role assignment create --assignee <appId> --role "Azure Service Bus Data Owner" --scope "$NS_ID"
# Least-privilege alternative (no runtime counts):
# az role assignment create --assignee <appId> --role "Azure Service Bus Data Sender"   --scope "$NS_ID"
# az role assignment create --assignee <appId> --role "Azure Service Bus Data Receiver" --scope "$NS_ID"
```

## Render env (names only)
| Var | Value |
|---|---|
| `AZURE_TENANT_ID` | `c3ac4169-9d99-4eb7-b72b-bfa11f13d825` |
| `AZURE_CLIENT_ID` | `<appId>` |
| `AZURE_CLIENT_SECRET` | secret (Render secret env only) |
| `ServiceBus__FullyQualifiedNamespace` | `sb-bjoo-local-noe-dahl-shared.servicebus.windows.net` |
| `ServiceBus__HostKind` | `developer-local-azure` |
| `ServiceBus__ConnectionString` | **unset/empty** (AAD only) |

appsettings keys: `ServiceBus:FullyQualifiedNamespace`, `ServiceBus:HostKind`. Developer: build `ServiceBusClient` + `ServiceBusAdministrationClient` with `new DefaultAzureCredential()` (picks up `EnvironmentCredential`) when FQDN set and ConnectionString empty; surface credential failures as `SB_AUTH_FAILED` distinct from `SB_UNREACHABLE`.

## Verification
1. `az role assignment list --assignee <appId> --scope "$NS_ID" -o table` shows Data Owner.
2. Allow ~5 min RBAC propagation; redeploy Render.
3. `GET /api/projects/customerproject/sb/entities` returns counts for `sbq-bjoo-local-noe-dahl-shared-*` (ADDENDA-004).
4. Inject one `CustomerChanged` sample; peek it; DLQ list works.
5. Logs show no SAS / connection-string path used.
6. Calendar the secret expiry (1 year) → rotate with `az ad app credential reset --id <appId> --years 1`.
