# DSP Dev Overview — Slices 1–7 (registration + Service Bus browse/inject, DLQ list/retry/discard, Flow, Console, App Insights logs, registration generator, health + TraceIt link)

ASP.NET Core Minimal API (.NET 10) on `http://127.0.0.1:5090`, behind the front-door secret (ADDENDA-007, see below), against the shared-stack Service Bus emulator.
Design docs: `architecture/` (ADR-000, ADR-003, ADR-004, ADR-005, HANDOFF-*, ADDENDA-001, ADDENDUM-ui-wireframe-q). UI lives in `ui/app` (UI/UX).

## Layout

| Path | What |
|---|---|
| `Dsp.DevOverview.sln` | Solution (API, catalog library, tests, generator) |
| `src/Dsp.DevOverview.Api` | Minimal API: registration loading, SB entities/peek/send/purge, Flow graph (`Flow/`), Console process manager (`AppConsole/`), App Insights query (`AppInsights/`), project health (`Health/`) |
| `src/Dsp.DevOverview.Catalog` | Sample catalog model + `SampleMessageBuilder` (net8.0 + net10.0) |
| `catalog/customerproject.samples.json` | Mirror of `local-dev/customerproject/seed-messages/Program.cs` (generated, reviewed) |
| `tools/CatalogGenerator` | Regenerates and byte-verifies the catalog with the real `Dahl.M3.Event.*` contracts (net8.0, like the kit) |
| `tools/DlqForcer` | Test helper: forces messages into a DLQ on the emulator (PeekLock receive + `DeadLetterMessageAsync`), emulator only |
| `test/Dsp.DevOverview.Api.Tests` | xUnit tests (emulator tests opt-in, see below) |
| `src/Dsp.DevOverview.RegistrationGenerator` | Slice 6 generator library (HCL/TF reader, trigger scan, local-dev readers, merge, diff, schema check); used by the CLI and the API |
| `tools/Generate-Registration` | Slice 6 CLI: regenerate `architecture/registrations/{id}.json` from TF + code + local-dev (ADR-002) |
| `test/Dsp.DevOverview.RegistrationGenerator.Tests`, `test/fixtures/registration-generator/` | Generator unit tests and their small TF / repo / local-dev fixtures (project `demo`) |
| `data/registrations/history/{id}-yyyyMMdd-HHmmss.json` | Previous registration, saved before each generator/refresh write |
| `scripts/run-api.sh`, `scripts/demo.sh`, `scripts/demo-dlq.sh`, `scripts/demo-flow.sh`, `scripts/demo-console.sh`, `scripts/demo-logs.sh`, `scripts/demo-registration.sh`, `scripts/demo-health.sh` | Run the API / curl demos (slice 1 inject, slice 2 DLQ, slice 3 Flow, slice 4 Console, slice 5 Logs, slice 6 registration, slice 7 health) |
| `data/flow-positions/{projectId}.json` | User-dragged Flow node positions (written by the API; created on first save) |
| `data/console/<app>-yyyyMMdd.log`, `data/console/<app>.log` | Console log per app/day (written by the API); optional fallback file the console tails |

## Run

```bash
pwsh /workspace/dsp/shared-stack/Test-DspStack.ps1      # must say STATUS: HEALTHY (Start-DspStack.ps1 if not)
export NUGET_CERT_REVOCATION_MODE=offline                # box quirk (shared-stack README)
dotnet build Dsp.DevOverview.sln
dotnet test  test/Dsp.DevOverview.Api.Tests
dotnet test  test/Dsp.DevOverview.RegistrationGenerator.Tests
scripts/run-api.sh                                       # http://127.0.0.1:5090 (Development config)
scripts/demo.sh                                          # or: scripts/demo.sh --start (starts/stops the API itself)
scripts/demo-dlq.sh                                      # slice 2: force DLQ, list, retry one, discard one (takes test.lock itself)
scripts/demo-flow.sh                                     # slice 3: flow local/all, save a position, stored layout, reset (SB read-only)
scripts/demo-console.sh                                  # slice 4: console status, start stub:ticker, SSE + Last-Event-ID, restart, stop
scripts/demo-logs.sh                                     # slice 5: role confirmation, logs, kql fragment, sanitizer (503 AI_AUTH_FAILED without Azure auth)
scripts/demo-registration.sh                             # slice 6: generator dry run + emit, refresh dry run, refresh (hot reload), restore
scripts/demo-health.sh                                   # slice 7: /api/projects health, healthDetail, dlqTotal, traceItUrl (read-only)

# emulator integration test (writes to the shared emulator, so it is skipped unless enabled; take the box lock)
flock /workspace/dsp/test.lock env DSP_EMULATOR_TESTS=1 dotnet test test/Dsp.DevOverview.Api.Tests --filter Category=Emulator
```

The emulator connection string lives in `src/Dsp.DevOverview.Api/appsettings.Development.json`
(`Overview:ServiceBus:ConnectionString`). `appsettings.json` has it empty, so nothing outside Development touches a bus.


### Emulator test lock (Tester tip)

`scripts/demo-dlq.sh` takes `/workspace/dsp/test.lock` itself. If you already hold that lock (e.g. wrapping several smokes), set **`DSP_TEST_LOCK_HELD=1`** so the script skips its inner `flock` and avoids a nested-lock deadlock:

```bash
flock /workspace/dsp/test.lock env DSP_TEST_LOCK_HELD=1 scripts/demo-dlq.sh
```

## Project id

Registered project id is **`customerproject.dev`** (`{project}.{env}`), not bare `customerproject`.
Examples: `GET /api/projects/customerproject.dev/samples`. Plain `customerproject` returns `404 PROJECT_NOT_FOUND`.

## API (slices 1–7)

| Method | Path | Returns |
|---|---|---|
| GET | `/api/projects` | `ProjectSummary[]` (+ `traceItUrl`, slice 7); health rules below |
| POST | `/api/projects/{id}/registration/refresh[?source=generate\|file&dryRun=true&includeExternal=true]` | `ProjectSummary` + `refresh{source,dryRun,written,path,backup,generatedAt,apps,entities,edges,diff[],gaps[]}` (slice 6) |
| GET | `/api/projects/{id}/flow?scope=local\|all[&direction=LR\|TB]` | `FlowGraph` (+ `warnings[]`, `sb.maxDeliveryCount`); defaults `scope=local`, `direction=LR` |
| PUT | `/api/projects/{id}/flow/positions` | `{nodeId:{x,y}}` → the saved map after merge; `{}` = reset (layout back to `auto`) |
| GET | `/api/projects/{id}/samples` | `SampleMessageCatalog` (+ `seq`, `contentType`, `projectIndex`, `variant`, `runtimeApplicationProperties`, `referenceUtc`, `commandPropertyProfile`) |
| POST | `/api/projects/{id}/samples/send` | `{mode, event?, dataVersion?, projectIndex?, bodyOverride?, applicationPropertiesOverride?, targetEntityId?}` → `{sent, entity, entities[], commandPropertyProfile}` |
| POST | `/api/projects/{id}/sb/purge` | `{purged, entities[]}`; 403 `PURGE_CLOUD_FORBIDDEN` unless emulator + scope local |
| GET | `/api/projects/{id}/sb/entities` | `EntityStatus[]` exactly per ADDENDUM §1 (queues, topic, subscription with `parentEntityId`) |
| GET | `/api/projects/{id}/sb/{entityId}/peek?max=20[&dlq=true]` | `QueuePeek` (+ `scope`, `maxDeliveryCount`) |
| GET | `/api/projects/{id}/sb/{entityId}/dlq?max=50` | `DlqMessage[]` = `PeekedMessage` + `deadLetterReason` / `deadLetterErrorDescription` / `deadLetterSource` (each only when set). max 1–250 |
| POST | `/api/projects/{id}/sb/{entityId}/dlq/retry` | `{sequenceNumbers:[...] \| "all"}` → `{retried, entityId, targetEntityId, sequenceNumbers[], notFound[]}`; emulator only (403 `CLOUD_WRITE_FORBIDDEN`) |
| POST | `/api/projects/{id}/sb/{entityId}/dlq/discard` | `{sequenceNumbers:[...]}` → `{discarded, entityId, sequenceNumbers[], notFound[]}`; emulator only (403 `CLOUD_WRITE_FORBIDDEN`) |

Entity ids work raw (`/sb/q:customerproject/peek`) or encoded (`/sb/s%3Acustomerproject-topic%2Flocal-inspect/peek`).
Errors are always `{"error":{"code","message","retryable"}}`: `SB_UNREACHABLE` (503), `PROJECT_NOT_FOUND`, `UNKNOWN_ENTITY`,
`ENTITY_NOT_PEEKABLE`, `UNKNOWN_EVENT`, `INVALID_MODE`, `INVALID_DATA_VERSION`, `INVALID_PROJECT_INDEX`,
`BODY_OVERRIDE_REQUIRES_SINGLE`, `PURGE_CLOUD_FORBIDDEN`, `CLOUD_WRITE_FORBIDDEN`, `ENTITY_NOT_IN_SCOPE`, `INVALID_MAX`,
`INVALID_SEQUENCE_NUMBERS`, `ENTITY_HAS_NO_DLQ`, `MESSAGE_TOO_LARGE`, `NOT_FOUND` (unknown `/sb/…` action),
`INVALID_SCOPE`, `INVALID_DIRECTION`, `INVALID_POSITIONS` (400, flow), `INVALID_SOURCE` (400), `REGISTRATION_INVALID` /
`REGISTRATION_SOURCES_MISSING` (422, refresh: nothing written, the loaded registration stays). Logs errors (slice 5): see "App Insights logs" below.

### Flow (ADR-003, slice 3)

- **Graph** = registration apps + entities + edges, plus external sources that only appear as edge endpoints
  (`ext:seed-messages`, label "seed-messages kit"). DLQs are **not** nodes: queue/subscription nodes carry `sb.activeCount`,
  `sb.deadLetterCount`, `sb.dlqHot` (= `deadLetterCount > 0`, same rule as `EntityStatus`) and `sb.maxDeliveryCount`
  (scope-aware: local emulator 10, cloud 3; a cloud-only entity shows its cloud value). Topics carry `sb` with null depths.
  App nodes: `app.appKind`, `port`, `cloudRoleName`, `attached` (true while an overview-managed console process runs, slice 4).
- **Scope:** `scope=local` (default) hides everything with `scope:"cloud"`, both nodes and edges (and edges whose endpoint is hidden).
  `scope=all` adds them (customerproject: 12 nodes / 14 edges local, 13 / 16 all; the 2 extra edges are the cloud
  `customerproject-to-customerproject` subscription + forward). Edge ids (`e1`…) follow the registration order over **all**
  edges, so an edge keeps its id in both scopes (local skips `e13`/`e14`).
- **Depths** come from the slice 1/2 gateway (peek counts on the emulator), only for entities that exist in the active SB scope.
  **SB failure never fails the graph:** unreachable bus → all depths `null` + `warnings: ["SB_UNREACHABLE: …"]`; one entity failing →
  its depths `null` + `SB_DEPTH_FAILED: …`. `null` means unknown, not 0. `local-inspect` `activeCount` is `null` (auto-forwarding, see below).
- **Direction:** `direction=LR|TB` (case-insensitive, default `LR`) is echoed in `FlowGraph.direction` for the UI's auto-layout; the API doesn't lay out.
- **Positions / layout:** `PUT …/flow/positions` **merges** per node into `data/flow-positions/{projectId}.json` (last write wins per node;
  nodes not in the body keep their saved position). Unknown node ids are ignored (silently dropped, still 200). Any registration
  node id counts as known, including cloud-only ones. A body with only unknown ids changes nothing. Each value needs finite `x` and `y`
  (else 400 `INVALID_POSITIONS`). Writes are serialized and atomic (temp file + rename), and the directory is created on first save.
  The response is the full saved map. **Reset:** `PUT {}` deletes the file and returns `{}`. The next GET is `layout:"auto"`.
  `GET …/flow` attaches `position` to nodes that have one, and returns `layout:"stored"` when at least one *returned* node has a stored
  position, otherwise `"auto"`. Nodes without a position (e.g. new in the registration) are left to auto-layout.
  An unreadable positions file gives `auto` + a `FLOW_POSITIONS_UNREADABLE` warning, and the next PUT overwrites it.
- `scripts/demo-flow.sh` backs up and restores an existing positions file, so it doesn't wipe a layout dragged in the UI.

### Console (ADR-004, slice 4)

The overview is the process manager for local runs. Code: `src/Dsp.DevOverview.Api/AppConsole/`.

| Method | Path | Returns |
|---|---|---|
| GET | `/api/projects/{id}/console` | `ConsoleAppStatus[]`: `{appId, name, source: process\|tail\|docker\|stub, attached, managed, pid?, port?, startedAt?, exitCode?, command?, logFile?, lastSeq}` |
| GET (SSE) | `/api/projects/{id}/console/{appId}/stream` | `ConsoleChunk` events (`id:` = `seq`), plus `event: heartbeat` `{appId, attached, timestamp}` every 15 s |
| POST | `/api/projects/{id}/console/{appId}/start` | `{attached}`; idempotent while running |
| POST | `/api/projects/{id}/console/{appId}/stop` | `{attached:false}`; kills the whole process tree; idempotent |
| POST | `/api/projects/{id}/console/{appId}/restart` | `{attached}`; stop → (wait for the port to free) → start under one lock; emits `system` lines `Restarting…` … `Attached` |

- **App ids:** `app:api`, `app:functions`, `app:ingress` (registration `apps[].local.runScript`), `docker:<container>`, `stub:ticker`.
  Raw (`/console/app:api/start`) and encoded (`/console/app%3Aapi/start`) both work (catch-all route, like `/sb`).
- **Apps** run `pwsh -NoProfile -File <runScript>` with stdout/stderr captured. Env: `CUSTOMERPROJECT_REPO=/workspace/dsp/dsp-customerproject-localrun`
  (only if the overview's own env doesn't set it), `DOTNET_USE_POLLING_FILE_WATCHER=1`, `DOTNET_hostBuilder__reloadConfigOnChange=false`
  (box inotify limit). The overview's own `ASPNETCORE_URLS`/`ASPNETCORE_ENVIRONMENT`/launch-profile vars are stripped so a child doesn't bind :5090.
  Extra env: `Overview:Console:Environment`.
- **Buffer + files:** ring buffer of 10k lines per app (`BufferLines`); every line also goes to `data/console/<appId ':'→'-'>-yyyyMMdd.log`
  (`app:api` → `data/console/app-api-20261102.log`), as `<ISO timestamp> <stream> <line>`. `seq` is monotonic per app for the life of the
  overview process, across restarts.
- **SSE:** on connect the buffer is replayed, then live lines. `Last-Event-ID` (EventSource reconnect) or `?lastEventId=` replays only
  `seq > id`. ConsoleChunks use the default event (`onmessage`); heartbeats are the named event `heartbeat`. One line per chunk.
- **`attached`** on a chunk/status = a source is running for that app (managed process, docker logs, stub or fallback tail).
  Flow `app.attached` is stricter: only an **overview-managed** process.
- **Fallback attach:** when an app is not managed and `data/console/<app>.log` (no date, e.g. `data/console/app-api.log`) exists,
  `GET /console` or `/stream` tails it (source `tail`, polling, no inotify). Run elsewhere with
  `pwsh run-api.linux.ps1 *> /workspace/dsp/dev-overview/data/console/app-api.log`. Start stops the tail and launches the managed process.
- **Port check:** start returns **409 `APP_PORT_IN_USE`** when the app's `local.port` already listens (app started outside the overview).
- **Docker:** `docker:<name>` for `dsp-shared-*` containers (listed: `Overview:Console:DockerContainers`) runs
  `Overview:Console:DockerLogsCommand` (default `sudo -n docker logs -f --tail 200 {name}`). Other names → 404.
- **stub:ticker** emits one line per second; Development only (`Overview:Console:StubTicker` null = Development, true/false forces it).
- Errors: `CONSOLE_APP_NOT_FOUND` (404), `RUN_SCRIPT_NOT_FOUND` (404), `APP_PORT_IN_USE` (409), `CONSOLE_START_FAILED` (503, e.g. `pwsh`
  not on PATH; the reason is also a `system` line in the stream), `NOT_FOUND` (unknown `/console/…` action).
- Overview shutdown stops managed processes (graceful stop only; a `kill -9` of the overview leaves its children running).
- **Box caveat (2026-11-02):** `pwsh` and `docker` are not installed here, so `app:*` start returns 503 `CONSOLE_START_FAILED` and
  `docker:*` exits with `sudo: docker: command not found` (shown in the stream). `stub:ticker` and the fallback tail work.

```bash
scripts/demo-console.sh                       # stub:ticker: status, start, SSE, Last-Event-ID replay, restart (Restarting…/Attached), stop
TARGET=app:functions scripts/demo-console.sh  # the real Functions run script (needs pwsh)
scripts/demo-console.sh --start               # start/stop the API around the demo
```
The demo takes `/workspace/dsp/test.lock` via `flock` unless `DSP_TEST_LOCK_HELD=1`.

### App Insights logs (ADR-004, slice 5)

Read-only query of the shared, workspace-based App Insights `appin-870-dev-euw-dahl-shared` with `LogsQueryClient.QueryResourceAsync`
(resource-centric, classic schema: `requests`, `traces`, `exceptions`, `dependencies`). Code: `src/Dsp.DevOverview.Api/AppInsights/`.

| Method | Path | Returns |
|---|---|---|
| GET | `/api/projects/{id}/logs?roles=&since=&kql=&max=` | `LogEvent[]` (HANDOFF-ui-ux-data-contracts), oldest → newest |
| GET | `/api/projects/{id}/logs/roles?since=1d` | `{resourceId, query, since, observed[{cloudRoleName,count,registered,matchesProject}], registeredMissing[], unregistered[]}` (role confirmation) |

- **roles**: comma-separated `cloud_RoleName`s. Default = every registered one (`apps[].cloudRoleName` +
  `observability.appInsights.cloudRoleNames`): `web-870-dev-euw-dahl-customerproject`, `func-870-dev-euw-dahl-customerproject`,
  `func-870-dev-euw-dahl-customerproject-ingress`. Each value must look like an Azure resource name (`INVALID_ROLE`) and be registered
  for the project (`UNKNOWN_ROLE`): the AI resource is shared by all dev services, so the filter is the project boundary.
- **since**: ISO date-time (`2026-11-02T11:41:00Z`; offset → UTC; no offset = UTC) or relative `30m`/`2h`/`1d`. Default 30 min,
  max 7 days (`INVALID_SINCE`). Becomes `| where timestamp > datetime(…)`. **Tail:** poll every 5 s with `since=<timestamp of the last
  event>`; timestamps are returned with 100 ns precision so the last row isn't returned again.
- **kql**: optional single-line fragment, inserted as `| <fragment>` after the role/time filter (use native columns:
  `where severityLevel >= 3`, `where operation_Id == "…"`). Rejected with 400 `KQL_REJECTED`: newlines/control chars, `;` (no `let`),
  control commands (`.set`, `.drop`, `.show` …), ```` ``` ```` strings, > 1000 chars, and `union`/`join`/`lookup`/`evaluate`/`externaldata`/
  `materialize`/`invoke`/`fork`/`facet` or `workspace()`/`app()`/`resource()`/`cluster()`/`database()` (they could read outside the role filter
  or the resource; a string literal containing such a word is rejected too). A fragment the service can't compile → 400 `KQL_INVALID`.
- **max**: rows, default 200, 1–1000 (`INVALID_MAX`); the newest `max` rows in the window.
- Query shape (never a column named `last`):
  ```kusto
  union withsource=SourceTable requests, traces, exceptions, dependencies
  | where timestamp > datetime(2026-11-02T11:41:00.0000000Z)
  | where cloud_RoleName in ("web-870-dev-euw-dahl-customerproject", "func-870-dev-euw-dahl-customerproject", "func-870-dev-euw-dahl-customerproject-ingress")
  | <kql fragment>
  | top 200 by timestamp desc
  | project timestamp, SourceTable, cloud_RoleName, severityLevel = column_ifexists("severityLevel", int(null)), message = …, …
  | order by timestamp asc
  ```
- **LogEvent mapping:** `table` = source table; `severity` = `severityLevel` (traces/exceptions only); `message` = trace message,
  `<ShortType>: <outerMessage>` for exceptions, `<name> → <resultCode>` for requests, `[<type>] <target> <name> → <resultCode>` for dependencies;
  `operationId` = `operation_Id`; `durationMs`, `success`; `details` = full exception `type`, `problemId`, `url`, `resultCode`, dependency
  `target`/`data`, `customDimensions`, and `itemCount` when > 1. `traceItUrl` is not set (TraceIt is link-only, see slice 7).
- **Sampling:** dev AI samples at **12.5 %** (ADR-004/ADDENDA-001, 10 GB/day cap). The list is a sample, not every log line. A row with
  `details.itemCount: 8` stands for ~8 items. The UI must say so.
- **Local runs** don't send to App Insights (no `APPLICATIONINSIGHTS_CONNECTION_STRING` in the overview launch env), so the Logs tab
  shows cloud dev only. UI hint: **"Local runs → see Console tab"**.
- **Target:** resource id `/subscriptions/{sub}/resourceGroups/rg-870-dev-euw-dahl-shared/providers/Microsoft.Insights/components/appin-870-dev-euw-dahl-shared`
  from the registration's `observability.appInsights`. The subscription display name (`870Z-DAHL-DigitalServicePlatform-West-Europe-DEV`) is
  resolved to its id (`2562d3de-…`) via `~/.azure/azureProfile.json` (only subscription names/ids are read, no tokens).
  Overrides: `Overview:AppInsights:SubscriptionId|ResourceGroup|Resource|TenantId`. RG and subscription are inferred (ADDENDA-001).
- **Auth:** `DefaultAzureCredential` only, no connection strings or keys. It needs one of: the `az` CLI after `az login` (AzureCliCredential),
  `Connect-AzAccount` in pwsh with Az.Accounts (AzurePowerShellCredential), `azd auth login`, or `AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/
  `AZURE_CLIENT_SECRET` (EnvironmentCredential) in the API's environment. The identity needs **Reader** or **Monitoring Reader** on the AI resource.
  Managed identity is skipped by default (`Overview:AppInsights:ExcludeManagedIdentity=true`, no IMDS on a dev box).
- **Errors** (never an empty list in place of a failure): `AI_AUTH_FAILED` (503 no/failed credential; 502 token rejected), `AI_FORBIDDEN` (502, 403),
  `AI_RESOURCE_NOT_FOUND` (502, wrong sub/RG/name), `AI_UNREACHABLE` (503, network/timeout, retryable), `AI_THROTTLED` (503, retryable),
  `AI_UPSTREAM_ERROR` (502, 5xx, retryable), `AI_QUERY_FAILED` (502), `AI_NOT_CONFIGURED` (503, no resource / unknown subscription name),
  `KQL_REJECTED` / `KQL_INVALID` / `INVALID_ROLE` / `UNKNOWN_ROLE` / `INVALID_SINCE` / `INVALID_MAX` (400).
- **Health:** `healthDetail.aiQueryOk` comes from a background probe (`print aiProbe = 1` against the resource), cached 120 s
  (`ProbeTtlSeconds`). `GET /api/projects` never waits on it: it is `false` until a probe or a `/logs` call succeeds. `ProbeEnabled=false` turns it off.
- **Role confirmation (2026-11-02): blocked.** `GET /logs/roles?since=1d` (= ADR-004's `union … | where timestamp > ago(1d) | distinct cloud_RoleName`)
  returns 503 `AI_AUTH_FAILED` on this box: no `az`/`azd`, no Az.Accounts module for pwsh, no `AZURE_*` env vars, no MSAL IdentityService cache.
  `~/.azure` holds an old az profile and token cache, but the overview does not read tokens out of it. The three role names are therefore still the
  registration's convention-based guesses. To confirm: `az login`, then restart the API and run `scripts/demo-logs.sh` (prints
  `registeredMissing` / `unregistered`).

```bash
scripts/demo-logs.sh            # roles, logs, logs+kql, sanitizer 400, aiQueryOk; reports 503 AI_AUTH_FAILED when there is no Azure identity
curl -s 'http://127.0.0.1:5090/api/projects/customerproject.dev/logs?since=15m&roles=func-870-dev-euw-dahl-customerproject&kql=where%20severityLevel%20%3E%3D%203'
```

### Registration generator (ADR-002, slice 6)

One library, two entry points: `src/Dsp.DevOverview.RegistrationGenerator` is called by the CLI and **in-process** by the
API (no shelling out to `dotnet run`), so both produce byte-identical records.

```bash
dotnet run --project tools/Generate-Registration -- --project customerproject --env dev --dry-run     # diff + gaps, writes nothing
dotnet run --project tools/Generate-Registration -- --project customerproject --env dev               # write the record the API loads
dotnet run --project tools/Generate-Registration -- --out /tmp/cp.json --include-external             # elsewhere, plus cross-service cloud subs
curl -X POST 'http://127.0.0.1:5090/api/projects/customerproject.dev/registration/refresh?dryRun=true' # same thing, inside the API
curl -X POST 'http://127.0.0.1:5090/api/projects/customerproject.dev/registration/refresh'             # write + hot reload
curl -X POST 'http://127.0.0.1:5090/api/projects/customerproject.dev/registration/refresh?source=file' # reload the file only (after the CLI)
scripts/demo-registration.sh [--keep] [--start]
```

- **Output path.** The API loads `Overview:Registrations` from `appsettings.json` = `architecture/registrations/customerproject.dev.json`.
  The CLI's default `--out` (and the record it merges into, `--current`) is that file; refresh writes the file the
  project was loaded from. The previous version goes to `data/registrations/history/` first; identical output is not rewritten.
- **Sources (read-only).** TF `unprovisioned_app-{project}` (`locals-web.tf` web_app, `locals-function.tf` function_app) and
  `unprovisioned_app-{project}-ingress`; `unprovisioned_shared-message/locals-sb-{queue,topic,subscription}-dsp.tf` (all
  `locals-sb-*.tf` with `--include-external`); `[ServiceBusTrigger("%X%")]` in `{repo}/src/<app project>/**/Functions/*.cs`;
  `local-dev/{project}/Config.json`, `env.linux.ps1` (setting → emulator name), `run-*.linux.ps1` (app, project folder, port;
  the API port/URL comes from its `--launch-profile` in `launchSettings.json`). HCL is parsed by a small reader (`Hcl.cs`), not
  evaluated: names are rebuilt from `module.naming` references with the naming values `870/dev/euw/dahl` (from the record's
  `naming.values`; the stacks' `terraform.tfvars.ci` are placeholders).
- **What it derives.** Apps (id from the run script, kind web/func/func-ingress, `azureResource` = `{web|func}-870-dev-euw-dahl-{app_name}`,
  local port/url/runScript, triggers); entities in Config.json order with local name (env.linux.ps1), cloud name and
  `cloudMaxDeliveryCount` (TF, default 3) and scope local/cloud/both; edges: `trigger` (confidence `code`), `publish` for every
  queue/topic setting the app does not trigger on (`inferred-from-settings`, ADR-002), local subscription/forward from
  Config.json (`manual`), cloud subscriptions from TF (`terraform`, named/placed like `r-servicebus-entities.tf`: v1
  `{from}-to-{to}-subscription` on topic `to`, v2 `{from}-to-{to}` / v3 `{to}` on topic `from`). By default only subscriptions
  internal to the project (project topic → project queue); `--include-external` / `includeExternal=true` adds the ones that
  connect other services (foreign topics/queues become cloud-only `shared` nodes).
- **What it preserves.** `overrides` verbatim (never rewritten); edges with confidence `manual` (seed-messages injects);
  top-level sections it does not own (`observability`, `naming`, `samples.counts`, ...); extra properties on apps/entities/
  edges (`namingNote`, hand `label`s, a `cloudRoleName` confirmed from App Insights); a trigger-bound publish edge already in the
  record (Ingress → customerproject-snapshot). Optional structured overrides are applied last: `overrides.apps[]` /
  `overrides.entities[]` (patch/add by id), `overrides.edges[]` (patch/add by from|to|kind). Anything else under `overrides` is notes.
- **Output check.** Every result is validated against `architecture/registration.schema.json`; invalid output is never
  written or loaded (CLI exit 1, API 422). The CLI prints a diff summary (`+`/`-`/`~` per app/entity/edge, changed paths
  elsewhere, `overrides preserved`) and the **gaps** (what TF could not back up). The API returns both in `refresh`.
- **Hot reload.** `ProjectStore` swaps the project atomically; every service resolves the project per request, so the next
  `/flow`, `/sb/*`, `/console`, `/logs` call sees the new record. Running console processes are untouched. Local anonymous as
  before (ADR-000); the API stays bound to 127.0.0.1.
- **Config** (`Overview:RegistrationGenerator`): `TerraformEnvDir`, `ServiceRepo`, `LocalDevDir`, `SamplesCatalog`,
  `IncludeExternalSubscriptions`, `HistoryDir` (under `DataDir`). Null paths = `/workspace/dsp/dsp-{project}-localrun`,
  `/workspace/dsp/local-dev/{project}`, `catalog/{project}.samples.json`.

Current diff for customerproject.dev (draft → generated): `+ app:api -publish-> q:customerproject-ingress` (TF web
app_settings; also listed in ADR-002), cloud subscription `cloudName` `customerproject-to-customerproject-subscription` →
`customerproject-to-customerproject` (it is a v2 subscription), `samples.catalog` becomes relative, 4 more `sources`.
`scripts/demo-registration.sh` puts the previous record back at the end (unless `--keep`), because `FlowTests` and
`demo-flow.sh` pin the draft's counts (local 14 edges). Adopting the generated record means updating those counts to 15.

### Health + TraceIt link (slice 7)

`GET /api/projects` returns one `ProjectSummary` per registered project, built from live signals only
(`Health/ProjectHealth.cs`). Each project is probed on its own, so several registrations work side by side.

| Field | Source |
|---|---|
| `healthDetail.appsUp` / `appsTotal` | `appsTotal` = registration `apps[]`. An app is up when the console process manager (slice 4) runs it as a managed process, **or** something listens on its registered local port (runs started outside the overview). No evidence = down. |
| `healthDetail.sbReachable` | Peek of one message on the project's first local queue via `SbGateway`. No queue in scope = `false`. |
| `healthDetail.aiQueryOk` | The slice 5 App Insights background probe (cached 120 s). `false` until a probe succeeds, and `false` when the probe is disabled or absent. Never assumed. |
| `dlqTotal` | Sum of dead-letter counts over the project's active entities. `0` when SB is unreachable (check `sbReachable`). |
| `queueCount` / `appCount` | Queues in the active SB scope / registration apps. |
| `registrationGeneratedAt` | Registration `generatedAt` (changes after a slice 6 refresh). |
| `traceItUrl` | Registration `observability.traceIt.url`, else `Overview:TraceIt:Url` (default `https://traceit-dev.sgdn.no`), else `null`. |

**Rules** (`HealthRules.Evaluate`, a pure function; severity red > amber > unknown > green, first match wins):

| # | Condition | `health` |
|---|---|---|
| 1 | `sbReachable == false` | `red` |
| 2 | `appsUp < appsTotal` | `amber` (with or without App Insights: a known local problem beats "can't tell") |
| 3 | `aiQueryOk == false` | `unknown` (local is fine, but telemetry can't be confirmed, so never `green`) |
| 4 | otherwise | `green` |

A project with no apps counts as "all apps up". Right after an API start `aiQueryOk` is `false` until the first probe
finishes, so a healthy stack shows `unknown` for a moment. On a box without an Azure identity it stays `unknown`.

**Caching:** the SB reachability + DLQ total is cached per project for `Overview:Health:SbProbeCacheSeconds` (default 5,
`0` = no cache). A registration hot reload invalidates the entry. App ports and console state are checked on every call.

**TraceIt (ADR-004): v1 is a link only.** `ProjectSummary.traceItUrl` opens TraceIt; there are no per-log deep links
because TraceIt has no documented URL pattern. `LogEvent.traceItUrl` stays `null`. Still unknown: auth (probably SSO),
whether there is a query API, the correlation key (`operation_Id`?), and whether local runs are ingested. Confirm these
with the owner before building a deeper integration.

```bash
scripts/demo-health.sh      # GET /api/projects → health, healthDetail, counts, traceItUrl (jq if installed, else python3)
```

Tests: `HealthTests.cs`: the rule table, the aggregation with fake SB/AI/console/port probes (multi-project), and
`GET /api/projects` over HTTP with no Service Bus (must be `red`, never a faked green).

### DLQ retry / discard (ADR-005)

- Service Bus can't receive a dead letter by sequence number, so retry/discard **PeekLock-receive the DLQ** and act on the
  requested sequence numbers. Other messages stay locked until the run ends (so the loop keeps getting new ones), then
  are **abandoned**: they stay in the DLQ, but their DLQ delivery count goes up by one. Requested numbers that weren't in the DLQ come back in `notFound` (still 200).
- **Retry** sends a clone to the main entity and then **Completes** the DLQ message. The clone copies the body, ApplicationProperties (minus
  the broker's `DeadLetterReason` / `DeadLetterErrorDescription`), Subject, ContentType and CorrelationId, and adds `x-overview-retry-of=<DLQ sequence number>`
  (it overwrites any earlier value). It gets a new MessageId, and its delivery count starts at 0. Sends are batched (`ServiceBusMessageBatch`), and each batch is completed only after
  it was sent, so a failed send leaves the originals in the DLQ.
- `"all"` = the DLQ sequence numbers peeked at the start (cap 5000), so a message that dead-letters again during the run isn't retried twice.
- Main entity: a queue's DLQ goes back to the queue. A subscription's DLQ goes to its **topic** (you can't send to a single subscription),
  so every matching subscription gets a copy. Locally the topic has only `local-inspect`.
- **Discard** = Complete only. It accepts explicit sequence numbers only, not `"all"` (use purge for that).
- Use the sequence numbers that `GET …/dlq` (and `peek?dlq=true`) show for retry/discard.

### Forcing a DLQ for tests (no service or stack edits)

`tools/DlqForcer` PeekLock-receives a queue or subscription and calls `DeadLetterMessageAsync` on messages whose
application property matches `--match key=value`. It abandons the rest. It refuses connection strings that are not the emulator.
```bash
dotnet run --project tools/DlqForcer -- --queue customerproject-ingress --match x-overview-demo-run=abc --reason R --description D
```
`scripts/demo-dlq.sh` injects 2 kit messages tagged `x-overview-demo-run=<run>` (via `applicationPropertiesOverride`) and forces them into the DLQ with the tool. It then
lists them, retries one and discards the other, and checks that the retried message left the DLQ and its clone is on active peek. The emulator test
(`DlqEmulatorTests`, opt-in `DSP_EMULATOR_TESTS=1`) does the same on `customerproject-topic-inspect` (no consumer), and also covers `"all"`.
It does `"all"` only when the DLQ holds nothing but its own messages. It cleans up only its own marked messages.
The alternative (let an app abandon until MaxDeliveryCount=10) needs a failing handler, so it isn't used.

### Send modes (= the kit CLI)

| mode | target | count | notes |
|---|---|---|---|
| `ingress` (+`dataVersion` N) | customerproject-ingress | 21 | 6 events × 3 projects, then project 1 renamed (N+1), stale (N), duplicate (N+1, byte-identical to renamed) |
| `commands` | customerproject | 2 | SyncProjectDates, CreateMissingProjects `{"Tenant":2}` (Optimera), no ContentType |
| `all` (+`dataVersion`) | both | 23 | ingress then commands |
| `one` `event` [`dataVersion`] [`projectIndex` 1-3] | customerproject-ingress | 1 | Name / YourReference1 get ` (v{N})` |
| `purge` | 6 queues + DLQs | – | customerproject-ingress, -snapshot, -domain, customerproject, communication, customerproject-topic-inspect; emulator only |


`commandPropertyProfile` on `POST …/samples/send` is **`kit` (or configured profile) when the send included command messages**, and **`null` for ingress-only / `one` / purge** — intentional: the profile only shapes command application properties. `GET …/samples` always reports the active profile.

Every injected message also gets `x-overview-injected=true`. `bodyOverride` replaces the body only (single-message sends);
`applicationPropertiesOverride` merges over the kit properties (override wins per key).

## How the catalog mirrors the kit

The kit is never executed or edited. `tools/CatalogGenerator` contains a line-by-line copy of the kit's sample data and
builders with an injected clock and serializes them with the kit's contract packages (`Dahl.M3.Event.* 1.0.10`,
`Dahl.M3.Event.Common 1.0.7`, `Dahl.Core.Tenant 2.0.15`) through `new BinaryData(obj, type)`, just like `ToIngressMessage`.
The catalog stores those bodies (at `referenceUtc`) plus the rules for the dynamic fields (`Envelope.DataVersion`,
`Envelope.LastModifiedUtc = now + DataVersion s`, `yyyyMMdd` dates at −6/+18 months, ` (vN)` suffixes for `one`).

`dotnet run --project tools/CatalogGenerator -- verify` rebuilds every mode with the real contract types and with
`SampleMessageBuilder` at the same clock and compares the bytes (125 checks). The test suite re-checks the reference
hashes on net10 and fails when `Program.cs` changes (SHA-256 recorded in the catalog). Regenerate with `-- generate`.

### Command properties: `kit` vs `addenda` profile (read this)

The kit sends commands through `ICustomerProjectQueueClient` → `QueueClientBase` from **Dahl.Core.Messaging 2.0.1**
(the version `dsp-customerproject-localrun` resolves). Read-only source checks:

- `SetTenantProperties` only adds `Tenant<Name>` bools for messages implementing `ITenants` (`List<Tenant> Tenants`).
  `SyncProjectDates` / `CreateMissingProjects` implement `ICommand { Tenant Tenant }`, **not** `ITenants`, so the kit sends
  **no Tenant\* properties** (same in the newer `_src-fee` source).
- `Dsp_OriginalEnqueuedTime` is set from **2.0.2** on (present in the 2.0.2/2.0.4/2.0.8/2.0.11 DLLs and `_src-fee`), not in 2.0.1.

So the default profile **`kit`** = `Dsp_ShardingBucketNumber` (random 1–12), `Dsp_CorrelationContextId` (new GUID),
`Dsp_IsM3Cloud=true`, `DataSource=M3Cloud`, the exact set the kit puts on the wire. Profile **`addenda`** is the
ADDENDA-001 / HANDOFF set (9 `Tenant*` bools with only `TenantOptimera=true`, + `Dsp_OriginalEnqueuedTime`, in
≥2.0.2 order). Switch with `Overview:CommandPropertyProfile=addenda`. The handlers don't read `Tenant*` properties
(`MessageHandler` only reads IsM3Cloud/DataSource/correlation/retry), so either profile is consumable.

## Service Bus notes

- Scope is `Local` (shared-stack emulator, namespace `sbemulatorns`, local entity names). Cloud names
  (`sbq-870-dev-euw-dahl-shared-<name>`) are informational only. `MaxDeliveryCount` is scope-aware: local = the
  registration's `maxDeliveryCount` (10), cloud = `cloudMaxDeliveryCount` (3, default 3). The active-scope value is in `QueuePeek.maxDeliveryCount`.
- **Depths on the emulator come from peek counts (cap 250).** The emulator management endpoint on :5300 answers
  `GetQueueRuntimeProperties` but reports 0 active/dead-letter messages while messages are on the queue (seen
  2026-11-02), so admin counts are off by default for the emulator (`Overview:ServiceBus:UseAdminCounts` overrides). For
  non-emulator scope the admin client is used, falling back to peek counts if unavailable.
- `local-inspect` auto-forwards to `customerproject-topic-inspect`; the emulator cannot browse auto-forwarding entities,
  so its `activeCount` is `null` (DLQ count works) and peek returns `ENTITY_NOT_PEEKABLE` pointing at the forward queue.
  Topics have null counts by design.
- Send, DLQ retry/discard and purge refuse anything but the emulator (`CLOUD_WRITE_FORBIDDEN`, `PURGE_CLOUD_FORBIDDEN`). No cloud writes.
  DLQ list (`GET …/dlq`) is read-only peek.
- Purge = the kit's list, which includes the shared `communication` queue (as the kit does). The emulator only hosts
  customerproject entities today; recheck before other projects share it.
- Health: superseded by slice 7 (see "Health + TraceIt link (slice 7)"); SB unreachable is still `red`.
- The box hits the inotify limit (128); the API turns off config reload-on-change (`DOTNET_hostBuilder__reloadConfigOnChange=false`).

## TODO-verify

- [ ] Command property set: confirm with Architect which profile is the contract. Default `kit` follows the kit's actual
      Dahl.Core.Messaging 2.0.1 (no `Tenant*`, no `Dsp_OriginalEnqueuedTime`); ADDENDA-001 lists both.
- [ ] Byte-equality on the wire against a real kit run: done here offline (generator `verify`, same contracts and serializer).
      A live check needs someone to run `dotnet run -- one CustomerChanged 3` in the kit and compare peeks (not run by the overview).
- [ ] Command body enum serialisation: `{"Tenant":2}` (number) under default STJ options; `CustomerProjectQueueClient` doesn't
      override `JsonSerializerOptions`. Confirm on a first kit-sent peek.
- [ ] Functions/Ingress consoles showing the handlers firing (slice-1 done criterion): needs the apps running (`run-*.linux.ps1`); not run here.
- [ ] Emulator admin counts: re-test when the emulator image is updated; if it reports real counts, set `UseAdminCounts=true`.
- [ ] DLQ `deadLetterReason` for real `MaxDeliveryCountExceeded` dead letters (handler failures ×10) is not yet seen on
      the emulator; the demo uses forced dead letters (reason `DemoForcedDeadLetter`). Check once the apps run.
- [ ] `deadLetterSource` is only set by the broker for forwarded/subscription dead letters; not exercised locally (local-inspect DLQ empty).
- [ ] `cloudRoleName`s (esp. Ingress `func-870-dev-euw-dahl-customerproject-ingress`) still unconfirmed: the slice 5 confirmation query
      (`GET …/logs/roles?since=1d`) is built but returned 503 `AI_AUTH_FAILED` on this box (no Azure identity). Re-run after `az login`.
- [ ] App Insights RG `rg-870-dev-euw-dahl-shared` and the DEV subscription are inferred (ADDENDA-001); a 404 `AI_RESOURCE_NOT_FOUND` from `/logs` means one is wrong.
- [ ] Registration `samples.catalog` is an absolute path; fine on the box, revisit for the PC/cloud modes. (The slice 6 generator writes it relative: `catalog/customerproject.samples.json`.)
- [ ] Slice 6 TF gaps (printed by the generator): `ServiceBusCustomerProjectSubscription = customerproject-to-customerproject-subscription`
      (functions app setting) names a v1 subscription, but TF declares it as v2 `customerproject-to-customerproject`;
      `ServiceBusErpEventPublisherSubscription = customerproject-to-erpeventpublisher-subscription` matches no TF subscription;
      the Ingress Azure name comes from the external dahl-app module (`app_short_name = custprin`) and is unconfirmed; 11
      cross-service cloud subscriptions exist (`--include-external`). Ask the Architect before adopting the generated record.
- [ ] Generator scope: customerproject only. Other `unprovisioned_app-*` stacks need a `local-dev/<project>` (run scripts, Config.json)
      before they can be generated; TF-only generation is not implemented.
- [ ] `scripts/demo-dlq.sh` needs the Ingress app stopped: a running consumer (`console/app:ingress/start`) takes the marked
      messages before `DlqForcer` can dead-letter them (seen 2026-11-02; passes with `console/app:ingress/stop` first).

## Service Bus host: emulator vs Bjor's Azure local namespace (ADDENDA-004)

Default (`appsettings.Development.json`) stays on the shared-stack **emulator** (connection string with `UseDevelopmentEmulator=true`).

To point the API at the personal Azure namespace `sb-bjoo-local-noe-dahl-shared.servicebus.windows.net`
(host kind `developer-local-azure`), use **DefaultAzureCredential** (e.g. `az login` as bjor.byrkjedal@osilion.no) — no SAS keys in the repo:

```bash
az login            # identity needs Service Bus Data Owner/Sender+Receiver on the namespace
export Overview__ServiceBus__ConnectionString=""          # empty = credential mode (connection string always wins)
export Overview__ServiceBus__FullyQualifiedNamespace=sb-bjoo-local-noe-dahl-shared.servicebus.windows.net
export Overview__ServiceBus__Scope=Local
dotnet run --project src/Dsp.DevOverview.Api --urls http://localhost:5090
```

Host kind is detected from the host name:

| Host kind | Detect | Entity names | inject / DLQ retry / discard | purge |
|---|---|---|---|---|
| emulator | `UseDevelopmentEmulator=true` | registration `localName` | allowed | allowed (confirm) |
| developer-local-azure | `sb-{dev}-local-noe-dahl-shared.servicebus.windows.net` | `sbq-{dev}-local-noe-dahl-shared-{localName}` / `sbt-…` (subscriptions keep `localName`) | allowed | allowed (confirm) |
| cloud-dev | anything else (e.g. 870 shared) | registration `cloudName` | 403 `CLOUD_WRITE_FORBIDDEN` | 403 `PURGE_CLOUD_FORBIDDEN` |

Emulator-only entities (`customerproject-topic-inspect`, `local-inspect`) may be absent on Azure; peek/depth soft-fail per entity.

## Render hosting and Service Bus (ADDENDA-005)

On Render (`RENDER` env var or `Overview__Hosting=Render`) SB errors describe the cloud API's Service Bus config, never the local shared stack.
Error codes: `SB_NOT_CONFIGURED` (no namespace/credential), `SB_AUTH_FAILED` (credential rejected / missing RBAC, 401/403), `SB_UNREACHABLE` (network).

Option (a), chosen: service principal + `DefaultAzureCredential` (→ `EnvironmentCredential`) over AmqpWebSockets/443. Render env vars:
`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` (secret env only), `Overview__ServiceBus__FullyQualifiedNamespace=sb-bjoo-local-noe-dahl-shared.servicebus.windows.net`;
leave `Overview__ServiceBus__ConnectionString` empty. SP/role commands: `architecture/RUNBOOK-render-sb-sp.md` (not run; needs Bjor's OK).

Option (b), off by default: `Overview__ServiceBus__ProxyUpstream=https://<box-api>` forwards SB endpoints (entities, peek, dlq list/retry/discard,
samples/send, purge) to `{upstream}/api/...` (status + body passed through, 30 s timeout `Overview__ServiceBus__ProxyTimeoutSeconds`,
`SB_PROXY_UNREACHABLE`/`SB_PROXY_TIMEOUT` on failure); health `sbReachable` comes from the upstream `/api/projects`. No auth header to the
upstream yet (an OAuth-gated ngrok host would block it).

`GET /healthz` → `ok` (Render health check / keepalive).

## Front-door secret (ADDENDA-007 layer 1)
Every API route except `/healthz` requires header `X-Frontdoor-Secret` equal to `FRONTDOOR_SHARED_SECRET` or
`FRONTDOOR_SHARED_SECRET_NEXT` (rotation). Neither set → 503 `FRONTDOOR_NOT_CONFIGURED`; wrong/missing → 401. No local bypass, no CORS.
- Local: `(umask 077; openssl rand -base64 48 > ~/.dsp-overview-frontdoor-secret)` once. `scripts/run-api.sh` and the demo
  scripts source `scripts/frontdoor-env.sh` (loads the file, wraps `curl` to send the header). The Vite dev proxy reads the
  same env var/file and adds the header server-side. Manual curl: `curl -K <(printf 'header = "X-Frontdoor-Secret: %s"\n' "$(cat ~/.dsp-overview-frontdoor-secret)") …`
- Render: same value in `FRONTDOOR_SHARED_SECRET` on the API and the web front door (which injects it only after the OIDC session check).
- `Overview:ServiceBus:ProxyUpstream` calls send this API's `FRONTDOOR_SHARED_SECRET`, so the upstream must accept the same value.
- Rotation: set `_NEXT` on the API → switch the web's value → move it into the API's `FRONTDOOR_SHARED_SECRET`, clear `_NEXT`.
