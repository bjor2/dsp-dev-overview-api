# NOTE: ADDENDA-009 (price.dev) API contract for UI/UX (Developer, Nov 4 2026)

All changes are **additive**. Existing fields and values are unchanged, and customerproject.dev renders as before. I did not touch anything under `ui/`.

## 1. Projects list: `GET /api/projects`
- A second row appears: `price.dev` (4 apps, 6 queues).
- New optional field **`notice`** on `ProjectSummary`, present only when there is something to say:
  ```json
  "notice": { "code": "ENTITIES_NOT_PROVISIONED", "severity": "warn",
              "message": "31 of 31 Service Bus entities are not provisioned on this namespace (the namespace itself answers).",
              "missing": 31, "checked": 31 }
  ```
  - `severity` is `info` or `warn`, **never error**. Render it as a neutral or amber hint, not a red banner.
  - The rule: the namespace is reachable (`healthDetail.sbReachable: true`) and at least half of the entities with a definite answer are not found.
  - customerproject has one emulator-only queue missing on the developer namespace, so it gets no notice.
- `dlqTotal` is `null` when **no** entity reports a DLQ count, for example when everything is missing. It is unknown, not 0. customerproject is unaffected.
- `health` colour rules are unchanged. price shows `amber` because no app runs locally; "not provisioned" alone never makes it red.

## 2. Flow: `GET /api/projects/{id}/flow`
| What | Shape | Render |
|---|---|---|
| **blobContainer node** | `{ "id": "b:pricedumpfromoptimera", "kind": "blobContainer", "label": "pricedumpfromoptimera", "scope": "both", "position"?: … }`, **no `sb`** | Standalone storage source node, never inside a container, with no counts and no DLQ. Not peekable: `/sb/b:…/*` returns 400 `ENTITY_NOT_SERVICE_BUS`. |
| **Blob trigger edge** | `{ "kind": "trigger", "from": "b:pricedumpfromoptimera", "to": "price.fn.blob-optimera", "label": "OptimeraSnapshotFileBlobTrigger", "confidence": "code" }` | **No new edge kind.** It is a normal `trigger` edge whose `from` node has `kind: "blobContainer"`. Style it by the source node kind if you want (e.g. a storage colour). |
| **Handler triggers** | `handler.triggers: [{ kind: "servicebus"\|"http"\|"timer"\|"blob", function?, entityId?, schedule?, setting?, path? }]` (every handler) | `timer`: clock badge with `schedule` (or `setting` when the schedule is an app setting) as tooltip. Timers have **no inbound edge**. `price.fn.timer-reload-m3cloud` groups **6** timer functions, one entry each. `blob`: optional badge or tooltip from `path`, e.g. `pricedumpfromoptimera/{filename}`. `handler.function` stays the first function, and `triggerEntityIds` is empty for timer-only handlers. |
| **Missing entity** | `sb: { activeCount: null, deadLetterCount: null, dlqHot: false, owner, maxDeliveryCount?, "state": "missing" }` | "Not provisioned" style: muted or dashed, with a label such as "not on namespace". Do **not** treat it as an error or as DLQ-hot. `state` is absent for normal entities. Topics only carry `state: "missing"`; their counts are always null. |
| Warnings | Possible new warning `SB_HOST_NAMESPACE_MISMATCH: …` | Shown when the registration's `hosts.<hostKind>.namespace` differs from the namespace this API reads. Not the case for price on bjoo. |

- price.dev ships `layoutDefaults` for all 58 ids (4 containers, 18 handlers, 33 entities, 2 ext, and 1 blobContainer), so a fresh API returns `layout: "stored"`, `positionSource: "default"`.
  - The 4 containers include `price.api-batch`, which has no edges.
  - `scope=local` hides the two cloud-only ext injects (`ext:m3-pico`, `ext:external-subscriptions`).
- `GET /api/projects/{id}/sb/entities` also gets an optional `state: "missing"` per `EntityStatus`. Topics are listed as before, and blobContainers are never listed.

## 3. Samples
`GET /api/projects/price.dev/samples` returns **200** with an empty catalog and two new fields:
```json
{ "projectId": "price.dev", "presets": [], "messages": [], "sampleProjects": [], "available": false,
  "reason": "no sample kit for price.dev (samples.catalog = none; none: no price seed kit / test-kit found)", … }
```
- Render "No sample kit for this project". Hide or disable "Inject sample…" when `available === false`.
- `available` is `true` for customerproject.
- `POST …/samples/send` and `POST …/sb/purge` on price return 404 `SAMPLES_NOT_AVAILABLE` (not retryable).

## 4. Errors are now told apart
| Condition | API result |
|---|---|
| Entity not on the namespace (404 / MessagingEntityNotFound) | Per entity: `state: "missing"`, null counts. Cached for 5 min. No warning or banner. |
| Credential rejected (401/403, az login, RBAC) | `SB_AUTH_FAILED` (503, not retryable), as before |
| Namespace unreachable / timeout | `SB_UNREACHABLE` warning, as before. The first probe after an API start gets 12 s (token + link), later probes 3 s. A successful probe is reused for 10 s. |

## 5. Suggested `types.ts` additions (I did not edit `ui/`)
```ts
export type NodeKind = "app" | "handler" | "queue" | "topic" | "subscription" | "blobContainer" | "external";
// FlowNode.sb: state?: "missing"
// handler: triggers?: { kind: "servicebus" | "http" | "timer" | "blob"; function?: string; entityId?: string; schedule?: string; setting?: string; path?: string }[]
// ProjectSummary: notice?: { code: string; severity: "info" | "warn"; message: string; missing?: number; checked?: number }
// SampleMessageCatalog: available?: boolean; reason?: string
// EntityStatus: state?: "missing"
```
`normalizeGraph` already passes unknown node kinds and extra fields through, so nothing breaks before these are added. A blobContainer currently renders with the fallback style.
