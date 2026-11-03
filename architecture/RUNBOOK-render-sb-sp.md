# RUNBOOK — Render Overview API → Service Bus via service principal (ADDENDA-005 option a)

Status: **NOT RUN.** Every command below is an Azure/Render write. CoS must get Bjor's explicit OK first.
Scope: namespace `sb-bjoo-local-noe-dahl-shared` (rg `rg-bjoo-local-noe-dahl-shared`), subscription `75c077b1-ce33-40de-a941-76aef97c6c9d`,
tenant Osilion `c3ac4169-9d99-4eb7-b72b-bfa11f13d825`. AAD only, no SAS / connection strings. Never paste the secret in chat, docs, logs or git.

## 1. Create SP + role (Azure CLI, as bjor.byrkjedal@osilion.no)
```bash
export PATH="$HOME/.local/bin:$PATH"
az login --tenant c3ac4169-9d99-4eb7-b72b-bfa11f13d825
az account set --subscription 75c077b1-ce33-40de-a941-76aef97c6c9d

# SP with no role assignment; capture the secret into a shell variable only (never printed).
SP_JSON=$(az ad sp create-for-rbac --name dsp-dev-overview-render-sb --years 1 -o json)
APP_ID=$(jq -r .appId <<<"$SP_JSON")
read -r AZ_SECRET < <(jq -r .password <<<"$SP_JSON"); unset SP_JSON
echo "appId=$APP_ID"   # appId is not secret

NS_ID=$(az servicebus namespace show -g rg-bjoo-local-noe-dahl-shared -n sb-bjoo-local-noe-dahl-shared --query id -o tsv)
az role assignment create --assignee "$APP_ID" --role "Azure Service Bus Data Owner" --scope "$NS_ID"
# Least privilege (no admin runtime counts): Data Sender + Data Receiver instead of Data Owner.
```

## 2. Set Render env vars on the API service (no echo of the secret)
Option A — Render dashboard: dsp-dev-overview-api → Environment → add the vars below; paste the secret directly from the
terminal variable (`printf %s "$AZ_SECRET" | xclip -selection clipboard` or similar), never into a file.

Option B — Render API (`RENDER_API_KEY` already in env, service id `<srv-api-id>`):
```bash
SRV=<srv-api-id>
put() { curl -fsS -X PUT "https://api.render.com/v1/services/$SRV/env-vars/$1" \
  -H "Authorization: Bearer $RENDER_API_KEY" -H "Content-Type: application/json" \
  --data-binary @- >/dev/null; }
printf '{"value":"%s"}' c3ac4169-9d99-4eb7-b72b-bfa11f13d825 | put AZURE_TENANT_ID
printf '{"value":"%s"}' "$APP_ID"                          | put AZURE_CLIENT_ID
printf '{"value":"%s"}' "$AZ_SECRET"                       | put AZURE_CLIENT_SECRET   # stdin only; not in argv/history
printf '{"value":"%s"}' sb-bjoo-local-noe-dahl-shared.servicebus.windows.net | put Overview__ServiceBus__FullyQualifiedNamespace
unset AZ_SECRET
```

| Var | Value |
|---|---|
| `AZURE_TENANT_ID` | `c3ac4169-9d99-4eb7-b72b-bfa11f13d825` |
| `AZURE_CLIENT_ID` | `<appId>` |
| `AZURE_CLIENT_SECRET` | secret (Render secret env only) |
| `Overview__ServiceBus__FullyQualifiedNamespace` | `sb-bjoo-local-noe-dahl-shared.servicebus.windows.net` |
| `Overview__ServiceBus__ConnectionString` | unset / empty |
| `Overview__Hosting` | optional `Render` (Render's own `RENDER` var is auto-detected) |

Note vs ADDENDA-005: the API binds config under `Overview:` so the names are `Overview__ServiceBus__…`, not `ServiceBus__…`.
There is no `HostKind` setting: host kind `developer-local-azure` is derived from the FQNS pattern `sb-{dev}-local-noe-dahl-shared` (ADDENDA-004).
Transport defaults to AmqpWebSockets (443) for Azure namespaces. `DefaultAzureCredential` tries `EnvironmentCredential` first, which
builds a `ClientSecretCredential` from the three `AZURE_*` vars.

## 3. Verify
1. `az role assignment list --assignee "$APP_ID" --scope "$NS_ID" -o table` → Azure Service Bus Data Owner.
2. Wait ~5 min (RBAC propagation); redeploy Render API.
3. `GET /api/projects` → `healthDetail.sbReachable: true`. `GET /api/projects/customerproject.dev/sb/entities` → `sbq-bjoo-local-noe-dahl-shared-*` names.
4. Errors: `SB_NOT_CONFIGURED` = vars missing; `SB_AUTH_FAILED` = secret wrong/expired or role missing; `SB_UNREACHABLE` = network/namespace.
5. Calendar the secret expiry (1 year): `az ad app credential reset --id "$APP_ID" --years 1`, then repeat step 2 for `AZURE_CLIENT_SECRET`.

## Rollback
`az role assignment delete --assignee "$APP_ID" --scope "$NS_ID"`; `az ad sp delete --id "$APP_ID"`; remove the Render vars.
