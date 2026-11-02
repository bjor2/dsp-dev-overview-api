# Handoff → UI/UX: data contracts (aligned to `ui/IA-customerproject.md`)
Stack (ADR-000): Vite + React SPA → overview API `http://localhost:5090/api`. No direct SB/AI/repo access.

## Answers to IA open questions
1. **JSON shapes:** below (TypeScript + example).
2. **Auth:** v1 = **local anonymous**, API bound to 127.0.0.1, no login UI. Later cloud-hosted = **Entra ID** (MSAL in the SPA, roles `DSP-Overview.Read` / `DSP-Overview.Write`). Design a slot in the header for the user chip, hidden in v1.
3. **Console:** the overview API launches the existing `run-*.linux.ps1` scripts as child processes and captures stdout/stderr (no service edits), streamed by SSE. Fallback: tail a log file. Otherwise `attached:false` shows the "not attached" badge. Buttons: Start / Stop / Restart per app (v1).
4. **Flow positions:** **auto-layout v1** (ELK/dagre, LR/TB). Optional drag positions are saved via `PUT /flow/positions` in the overview store, plus a "Reset layout" button.

## Endpoints
| Method | Path | Returns | IA screen |
|---|---|---|---|
| GET | /api/projects | ProjectSummary[] | 1 list |
| POST | /api/projects/{id}/registration/refresh | ProjectSummary | 2 header |
| GET | /api/projects/{id}/flow?scope=local\|all | FlowGraph | 3 Flow |
| PUT | /api/projects/{id}/flow/positions | {nodeId:{x,y}} | 3 |
| GET | /api/projects/{id}/samples | SampleMessageCatalog | 4 picker |
| POST | /api/projects/{id}/samples/send | {mode, event?, dataVersion?, projectIndex?, bodyOverride?} → {sent:n, entity} | 4 |
| POST | /api/projects/{id}/sb/purge | {purged:n} (confirm) | 4 utilities |
| GET | /api/projects/{id}/sb/entities | EntityStatus[] | 5 |
| GET | /api/projects/{id}/sb/{entityId}/peek?max=20 | QueuePeek | 5 |
| GET | /api/projects/{id}/sb/{entityId}/dlq?max=50 | DlqMessage[] | 5 DLQ |
| POST | /api/projects/{id}/sb/{entityId}/dlq/retry | {sequenceNumbers:[...]\|"all"} → {retried:n} | 5 |
| POST | /api/projects/{id}/sb/{entityId}/dlq/discard | {sequenceNumbers:[...]} | 5 |
| GET | /api/projects/{id}/logs?roles=&since=&kql= | LogEvent[] | 6 |
| GET (SSE) | /api/projects/{id}/console/{appId}/stream | ConsoleChunk events | 7 |
| POST | /api/projects/{id}/console/{appId}/start\|stop | {attached} | 7 |

Errors: `{ "error": { "code": "SB_UNREACHABLE", "message": "...", "retryable": true } }`. Never fake healthy.

## Shapes
```ts
type Health = "green" | "amber" | "red" | "unknown";
interface ProjectSummary { id: string; project: string; env: "dev"; health: Health;
  healthDetail: { appsUp: number; appsTotal: number; sbReachable: boolean; aiQueryOk: boolean };
  appCount: number; queueCount: number; dlqTotal: number; registrationGeneratedAt: string; }
```
```json
{"id":"customerproject.dev","project":"customerproject","env":"dev","health":"amber",
 "healthDetail":{"appsUp":2,"appsTotal":3,"sbReachable":true,"aiQueryOk":true},
 "appCount":3,"queueCount":6,"dlqTotal":4,"registrationGeneratedAt":"2026-11-02T12:45:00+01:00"}
```
```ts
type NodeKind = "app" | "queue" | "topic" | "subscription" | "external";
interface FlowNode { id: string; kind: NodeKind; label: string; scope: "local"|"cloud"|"both";
  app?: { appKind: "web"|"func"|"func-ingress"; port?: number; cloudRoleName?: string; attached: boolean };
  sb?: { activeCount: number|null; deadLetterCount: number|null; dlqHot: boolean; owner: "project"|"shared" };
  position?: { x: number; y: number }; }   // only when the user has stored one
interface FlowEdge { id: string; from: string; to: string; kind: "trigger"|"publish"|"subscription"|"forward"|"inject";
  label?: string; scope: "local"|"cloud"|"both"; confidence: "code"|"terraform"|"inferred-from-settings"|"manual"; }
interface FlowGraph { projectId: string; layout: "auto"|"stored"; direction: "LR"|"TB"; nodes: FlowNode[]; edges: FlowEdge[]; }
```
```json
{"projectId":"customerproject.dev","layout":"auto","direction":"LR",
 "nodes":[
  {"id":"app:ingress","kind":"app","label":"Ingress :7240","scope":"both","app":{"appKind":"func-ingress","port":7240,"cloudRoleName":"func-870-dev-euw-dahl-customerproject-ingress","attached":true}},
  {"id":"q:customerproject-ingress","kind":"queue","label":"customerproject-ingress","scope":"both","sb":{"activeCount":0,"deadLetterCount":2,"dlqHot":true,"owner":"project"}}],
 "edges":[{"id":"e1","from":"q:customerproject-ingress","to":"app:ingress","kind":"trigger","label":"IngressQueueHandler","scope":"both","confidence":"code"}]}
```
Edge styling: trigger = solid, publish = solid arrow, `confidence:"inferred-from-settings"` = dotted, `scope:"cloud"` = dashed + hidden unless `scope=all`.
```ts
interface SamplePreset { id: string; mode: "all"|"ingress"|"commands"|"one"|"purge"; label: string;
  targetEntityId: string|null; messageCount: number; params?: { event?: string[]; dataVersion?: number; projectIndex?: (1|2|3)[] };
  destructive: boolean; }
interface SampleMessage { presetId: string; subject: string; targetEntityId: string;
  applicationProperties: Record<string,string|number|boolean>; body: unknown; }
interface SampleMessageCatalog { projectId: string; source: string; mirroredAt: string;
  sampleProjects: { index: 1|2|3; customerId: string; name: string; orderNumber: string; active: boolean }[];
  presets: SamplePreset[]; messages: SampleMessage[]; }
```
```json
{"projectId":"customerproject.dev","source":"local-dev/customerproject/seed-messages/Program.cs","mirroredAt":"2026-11-02T12:45:00+01:00",
 "sampleProjects":[{"index":1,"customerId":"10045871","name":"Bygg og Montasje Nordre Aker AS","orderNumber":"1000231871","active":true}],
 "presets":[
  {"id":"ingress-all","mode":"ingress","label":"Ingress batch","targetEntityId":"q:customerproject-ingress","messageCount":21,"destructive":false},
  {"id":"one","mode":"one","label":"Single ingress event","targetEntityId":"q:customerproject-ingress","messageCount":1,
   "params":{"event":["CustomerChanged","CustomerEmailChanged","CustomerAddressChanged","CustomerAgreementHeadChanged","QuotationOrderHeadChanged","HeaderFileChanged"],"dataVersion":1,"projectIndex":[1,2,3]},"destructive":false},
  {"id":"commands","mode":"commands","label":"Commands (Optimera)","targetEntityId":"q:customerproject","messageCount":2,"destructive":false},
  {"id":"all","mode":"all","label":"Everything","targetEntityId":null,"messageCount":23,"destructive":false},
  {"id":"purge","mode":"purge","label":"Purge all customerproject queues + DLQs","targetEntityId":null,"messageCount":0,"destructive":true}],
 "messages":[{"presetId":"commands","subject":"SyncProjectDates","targetEntityId":"q:customerproject","applicationProperties":{"Dsp_IsM3Cloud":true,"DataSource":"M3Cloud"},"body":{"Tenant":"Optimera"}}]}
```
(Counts 21/2/23/1 confirmed against Program.cs. **Command contract = kit / messaging 2.0.1** (ADDENDA-002): tenant in body; no `Tenant*` app props; no `Dsp_OriginalEnqueuedTime`. Runtime may still set `Dsp_IsM3Cloud`, `DataSource`, `Dsp_ShardingBucketNumber`, `Dsp_CorrelationContextId` — confirm on peek. Ingress: `contentType:"application/json"` + `DataSource:"M3Cloud"`. See ADDENDA-001/002.)
```ts
interface PeekedMessage { sequenceNumber: number; messageId: string; subject?: string; contentType?: string; correlationId?: string;
  enqueuedTime: string; deliveryCount: number; applicationProperties: Record<string,unknown>; body: unknown; bodyTruncated: boolean; }
interface QueuePeek { entityId: string; activeCount: number|null; deadLetterCount: number|null; messages: PeekedMessage[]; }
interface DlqMessage extends PeekedMessage { deadLetterReason?: string; deadLetterErrorDescription?: string; deadLetterSource?: string; }
```
```json
{"sequenceNumber":42,"messageId":"b1f..","subject":"CustomerChanged","enqueuedTime":"2026-11-02T11:40:12Z","deliveryCount":10,
 "applicationProperties":{"DataVersion":1},"body":{"CustomerId":"10045871"},"bodyTruncated":false,
 "deadLetterReason":"MaxDeliveryCountExceeded","deadLetterErrorDescription":"Message could not be consumed after 10 delivery attempts."}
```
Times are UTC ISO from the API. The UI renders them in Europe/Oslo.
```ts
interface LogEvent { timestamp: string; table: "requests"|"traces"|"exceptions"|"dependencies";
  cloudRoleName: string; severity?: 0|1|2|3|4; message: string; operationId?: string; name?: string;
  durationMs?: number; success?: boolean; details?: Record<string,unknown>; traceItUrl?: string; }
interface ConsoleChunk { appId: string; seq: number; timestamp: string; stream: "stdout"|"stderr"|"system"; lines: string[]; attached: boolean; }
```
```json
{"timestamp":"2026-11-02T11:41:00Z","table":"exceptions","cloudRoleName":"func-870-dev-euw-dahl-customerproject-ingress","severity":3,"message":"SqlException: ...","operationId":"4bf9..","details":{"type":"Microsoft.Data.SqlClient.SqlException"}}
{"appId":"app:functions","seq":1201,"timestamp":"2026-11-02T11:41:01Z","stream":"stdout","lines":["Executing 'Functions.CustomerProjectQueueHandler' ..."],"attached":true}
```

## Deltas vs IA
- The IA's "Retry all" also exists on subscriptions (local-inspect DLQ).
- Health amber = some apps down but SB+AI ok.
- The cloud-only subscriptions (ADR-003 delta 2) aren't in the IA. Add a "Show cloud edges" toggle to the Flow toolbar.
- Purge is local-emulator only. The API returns 403 `PURGE_CLOUD_FORBIDDEN` otherwise.
- The Logs tab: local runs don't send to AI by default, so show the hint "Local runs → see Console tab".

## Wireframe Q addendum (Nov 2)
See [`ADDENDUM-ui-wireframe-q.md`](./ADDENDUM-ui-wireframe-q.md) for EntityStatus, console `/restart`, `bodyOverride` scope, and Api/Functions `cloudRoleNames`.
