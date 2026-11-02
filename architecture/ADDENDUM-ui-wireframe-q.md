# Addendum — UI wireframe open questions (Nov 2, 2026)

Answers for Team Lead / UI/UX from `ui/COMPONENTS.md` § open questions. Extends `HANDOFF-ui-ux-data-contracts.md`.

## 1) `EntityStatus` (GET `/api/projects/{id}/sb/entities`)

UI assumption accepted and slightly extended:

```ts
type SbEntityKind = "queue" | "topic" | "subscription";
interface EntityStatus {
  entityId: string;           // e.g. "q:customerproject-ingress", "t:customerproject-topic", "s:customerproject-topic/local-inspect"
  kind: SbEntityKind;
  label: string;              // short display name
  activeCount: number | null; // null if probe failed
  deadLetterCount: number | null;
  dlqHot: boolean;            // deadLetterCount !== null && deadLetterCount > 0
  owner: "project" | "shared"; // communication = shared
  parentEntityId?: string;    // subscription → topic entityId
}
```

```json
[
  {"entityId":"q:customerproject","kind":"queue","label":"customerproject","activeCount":0,"deadLetterCount":0,"dlqHot":false,"owner":"project"},
  {"entityId":"q:customerproject-ingress","kind":"queue","label":"customerproject-ingress","activeCount":0,"deadLetterCount":2,"dlqHot":true,"owner":"project"},
  {"entityId":"q:communication","kind":"queue","label":"communication","activeCount":0,"deadLetterCount":0,"dlqHot":false,"owner":"shared"},
  {"entityId":"t:customerproject-topic","kind":"topic","label":"customerproject-topic","activeCount":null,"deadLetterCount":null,"dlqHot":false,"owner":"project"},
  {"entityId":"s:customerproject-topic/local-inspect","kind":"subscription","label":"local-inspect","activeCount":0,"deadLetterCount":0,"dlqHot":false,"owner":"project","parentEntityId":"t:customerproject-topic"}
]
```

Include **subscriptions** in the table (local-inspect has its own DLQ). Topic `activeCount`/`deadLetterCount` may be null — depths live on subscriptions/forward queues.

## 2) Console Restart

**Dedicated endpoint** (preferred):

`POST /api/projects/{id}/console/{appId}/restart` → `{ attached: boolean }`

Server does stop → start atomically and emits a `system` ConsoleChunk (`Restarting…` / `Attached`). Client stop→start is allowed but races SSE reconnect; wireframe Restart button should call `/restart` only.

Existing: `…/start` and `…/stop` remain.

## 3) `bodyOverride` on `POST …/samples/send`

**Body only.** It replaces the kit JSON body for that send; it does **not** replace `applicationProperties`.

Optional separate field for advanced:

```ts
interface SampleSendRequest {
  mode: "all" | "ingress" | "commands" | "one" | "purge";
  event?: string;
  dataVersion?: number;
  projectIndex?: 1 | 2 | 3;
  bodyOverride?: unknown;                          // body only
  applicationPropertiesOverride?: Record<string, string | number | boolean>; // optional; merges over kit props (override wins per key)
  targetEntityId?: string;                         // optional when mode implies target
}
```

UI: editable JSON preview → `bodyOverride`. Do not invent a props editor in v1; if needed later, a second “Application properties” panel maps to `applicationPropertiesOverride`. “Reset to kit” clears both overrides.

## 4) `cloudRoleNames` for Api + Functions

From `registrations/customerproject.dev.json` (site-name convention; confirm with one AI query in slice 2+):

| App | appId | cloudRoleName |
|-----|-------|---------------|
| Api | `app:api` | `web-870-dev-euw-dahl-customerproject` |
| Functions | `app:functions` | `func-870-dev-euw-dahl-customerproject` |
| Ingress | `app:ingress` | `func-870-dev-euw-dahl-customerproject-ingress` |

Logs filter chips = these three strings. FlowNode.app.cloudRoleName must match.

## Endpoint table delta

Add to HANDOFF endpoints:

| Method | Path | Returns |
|--------|------|---------|
| POST | /api/projects/{id}/console/{appId}/restart | `{ attached }` |
