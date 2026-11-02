# ADR-004 — Observability without code changes
Status: Accepted.

## App Insights
- Resource `appin-870-dev-euw-dahl-shared` (shared by all dev DSP services). The overview queries it with `LogsQueryClient` using `DefaultAzureCredential` (az login of the developer). No connection strings are stored.
- Filter is `cloud_RoleName in (...)` from the registration `apps[].cloudRoleName`. Expected dev values are the Azure resource names `web-870-dev-euw-dahl-customerproject`, `func-870-dev-euw-dahl-customerproject`, `func-870-dev-euw-dahl-customerproject-ingress` (Ingress name is built inside the external `dahl-app` module and is **unconfirmed**; earlier draft guessed `func-ingress-…`). Confirm with one-off `union requests, traces | where timestamp > ago(1d) | distinct cloud_RoleName | where cloud_RoleName has 'customerproject'`.
- Location (ADDENDA-001): RG `rg-870-dev-euw-dahl-shared` (derived from `unprovisioned_shared-misc` naming), subscription `870Z-DAHL-DigitalServicePlatform-West-Europe-DEV` (pattern-inferred; TF only has `var.azure_subscription_id`). Workspace-based AI, **sampling 12.5 %**, 10 GB/day cap — the UI must say telemetry is sampled.
- Tables: requests, traces, exceptions, dependencies. "Live-ish" tail = poll every 5 s with `timestamp > lastSeen`. KQL gotcha: never name a column `last`.
- Local runs do not send to AI unless `APPLICATIONINSIGHTS_CONNECTION_STRING` is set in the overview-owned launch env (it's not by default). Locally, the console is the primary log source.

## Console per app (no service edits)
**Decision: the overview is the process manager for local runs.** The API launches the existing scripts (`local-dev/customerproject/run-api.linux.ps1`, `run-functions.linux.ps1`, `run-ingress.linux.ps1`) via `pwsh` as child processes with redirected stdout/stderr. Each line goes into a ring buffer (10k lines per app) and to `data/console/<app>-<date>.log`, and is streamed to the UI over SSE. This works because the scripts already wrap `dotnet run` / `func start`.
- Fallback "attach" for processes started elsewhere: tail a log file if the dev ran `run-*.ps1 *> data/console/<app>.log`. The overview doesn't try ptrace/pipe hijacking. The UI shows "not attached" otherwise.
- Docker containers (SB emulator, Azurite, SQL): `docker logs -f <container>` streamed the same way.

## TraceIt
External `https://traceit-dev.sgdn.no`. v1 = link only. **Unknowns:** auth (likely SSO), whether it has a query API, correlation key (operation_Id?), and whether it ingests local runs. Owner to confirm before integration.

## Auth for the overview API
v1 = **local anonymous**, bound to `127.0.0.1` only. Azure calls use the developer's own `az login` identity. Cloud-hosted later = **Entra ID** (app registration, `DSP-Overview.Read` / `.Write` roles; write = inject/retry/purge).
