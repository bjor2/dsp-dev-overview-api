# Handoff → Developer: build slices
## Stack (LOCKED, ADR-000)
- Backend: ASP.NET Core Minimal API (.NET 10) `/workspace/dsp/dev-overview/src/Dsp.DevOverview.Api`, port 5090, 127.0.0.1, anonymous. Packages: Azure.Messaging.ServiceBus, Azure.Monitor.Query, Azure.Identity.
- Frontend: Vite + React `/workspace/dsp/dev-overview/ui/app` (UI/UX owns it). It calls only `/api`.
- Local SB: the shared-stack emulator (`/workspace/dsp/shared-stack`). Sample catalog mirrored from seed-messages into `dev-overview/catalog/`. The service repo is **never** read at runtime and never edited.
- Contracts: `HANDOFF-ui-ux-data-contracts.md`. Registration: `registration.schema.json`, `registrations/customerproject.dev.json`.

## Slices (in order; each ships with tests + a curl demo)
1. **Registration + SB browse/inject (FIRST).** Load `registrations/customerproject.dev.json` (validated against the schema). Expose `GET /api/projects`, `GET /sb/entities`, `GET /sb/{id}/peek`. Build `catalog/customerproject.samples.json` by diffing Program.cs **verbatim** (bodies, Subject, app properties, the rename/stale/duplicate sequence, the Optimera command properties). Expose `POST /samples/send` for modes `all|ingress|commands|one <Event> [DataVersion] [projectIndex]|purge` (purge emulator only). Done when the payloads `seed-messages` sends and those the overview sends are byte-equal (compare via peek), and the Functions/Ingress consoles show the handlers firing.
2. **DLQ list/retry/discard** per ADR-005. Test by forcing a DLQ (inject a malformed body, MaxDelivery 10).
3. **Flow API**: `GET /flow` built from the registration + live depths, plus `PUT /flow/positions` → `data/flow-positions/`.
4. **Console**: a process manager running `pwsh run-{api,functions,ingress}.linux.ps1`, ring buffer, SSE stream, start/stop, log-file tail fallback, `docker logs -f` for emulator containers.
5. **App Insights query**: LogsQueryClient on `appin-870-dev-euw-dahl-shared` filtered by `cloud_RoleName`. First confirm the role names (`union * | where timestamp > ago(1d) | distinct cloud_RoleName`). Poll tail, no column named `last`.
6. **Registration generator** `tools/Generate-Registration`: parse TF `locals-*.tf` (app_settings ServiceBus*), `unprovisioned_shared-message/locals-sb-*.tf`, `[ServiceBusTrigger("%X%")]` regex over `src/**/*.cs`, and Config.json. Emit the record and preserve `overrides`. `POST /registration/refresh`. Then extend it to other `unprovisioned_app-*` stacks.
7. **Health + project list** aggregation, and a TraceIt link (`traceit-dev.sgdn.no`, link only).
8. (Later) Entra auth, cloud dev SB read-only peek behind a flag. Cloud writes only with Bjor's OK.

## Guardrails
No edits under `dsp-customerproject*`, `local-dev`, `shared-stack`, or terraform. No git commit/push without Bjor's OK.
