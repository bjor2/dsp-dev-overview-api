# NOTE: ADDENDA-008 API contract, confirmed against the UI (Developer, Nov 4 2026)

I checked the API output against the UI/UX build in `ui/app/src/pages/flow/model.ts` (`normalizeGraph`, `V2Graph`), `pages/flow/layout.ts` and `pages/FlowTab.tsx`. I read those files only and edited nothing under `ui/`.

## GET /api/projects/{id}/flow (schemaVersion 2)

```jsonc
{
  "projectId": "customerproject.dev",
  "schemaVersion": 2,
  "layout": "auto" | "partial" | "stored",      // nothing stored | some stored (pin those) | every visible node+container stored
  "direction": "LR" | "TB",
  "layoutHints": { "containerPadding": 16, "innerDirection": "LR" | "TB" },   // innerDirection = direction
  "containers": [{
    "id": "container:customerproject.func-app", "label": "Functions :7239", "kind": "resource" | "logical",
    "resourceRefs": ["app:functions"],
    "appIds": ["app:functions"],                // always present, may be empty, several for a multi-app container (ADDENDA-010 §9 #26)
    "appId": "app:functions",                   // DEPRECATED alias, always present: appIds[0] if exactly one, else null
    "app": { "appKind": "func", "port": 7239, "cloudRoleName": "func-870-…", "attached": false },  // resource containers only
    "scope": "both",
    "position": { "x": 420, "y": 80 }           // ABSOLUTE, only when stored
  }],
  "nodes": [{
    "id": "customerproject.fn.commands", "kind": "handler", "label": "COMMANDS", "scope": "both",
    "containerId": "container:customerproject.func-app",
    "handler": {
      "function": "CustomerProjectQueueHandler",                 // singular (§11); omitted for *.http
      "triggerEntityIds": ["q:customerproject"],
      "selfRequeue": [{ "entityId": "q:customerproject", "messages": ["…"], "sources": ["src/…cs:59"] }]  // always an array
    },
    "position": { "x": 24, "y": 40 }            // RELATIVE to the container origin, only when stored
  },
  { "id": "q:customerproject", "kind": "queue", "sb": { … }, "position": { … } /* absolute */ }],
  "edges": [{ "id": "e5", "from": "customerproject.api.http", "to": "q:customerproject", "kind": "publish",
              "label": "commands", "scope": "both", "confidence": "code", "sources": ["src/…Controller.cs:238", "…"] }],
  "warnings": []
}
```

- Node kinds are `handler | queue | topic | subscription | external`. There are no `app` nodes; apps are `containers[].app`.
- Edges only connect handlers, entities and `ext:*`. They never touch `container:*` or `app:*`; the registration validator V-2 rejects that.
- Self-requeue is a badge (OQ-1). The COMMANDS → `q:customerproject` edge does not exist.
- `sources` on edges is new and optional. The UI type has no such field yet; it is safe to ignore.
- A handler whose container is hidden in the current scope comes back without `containerId`. This cannot happen for customerproject, where every container is `both`.
- A v1 registration (no `schemaVersion`) is upgraded in memory. The API then adds `warnings: ["REGISTRATION_V1_UPGRADED: …"]`.

## PUT /api/projects/{id}/flow/positions

- The body is `{ graphId: {x,y} }`. Container and standalone positions are absolute; handler positions are relative to their container, which is the frame `layoutGraph` uses.
- **Partial merge:** only the keys you send change. FlowTab sends one dragged key per batch, which is fine.
- Unknown ids, such as stale `app:*` from an old UI map, are ignored and return 200, not 400.
- Valid ids are every node id plus every container id (`AllNodeIds`), cloud-only ones included.
- `{}` resets: it clears only this env's `pos:{project}:{env}:*` keys and keeps the file. The next GET returns `layout: "auto"`.
- The response is this env's saved map in the same wire frame.
- On disk the file is `data/flow-positions/{registrationId}.json`, as `{"version":2,"positions":{"pos:customerproject:dev:<graphId>":{x,y[,parent]}}}`. A v1 file is migrated on load: `app:X` becomes its container key. Unknown keys are kept verbatim.

## Mismatches found in the UI types (for UI/UX)

1. **`src/api/types.ts` is still v1.** `NodeKind` has no `"handler"`, `FlowGraph.layout` is `"auto" | "stored"`, and there is no `containers`, `layoutHints` or `schemaVersion`. `model.ts` works around this with `V2Graph` and casts. Please move the v2 shapes into `types.ts`. The demo mock in `src/mocks/demo.ts` still uses `app` nodes.
2. **`layout` values.** The UI accepts `auto | stored | partial`. The API emits exactly those three; I used `stored` rather than `full` for "everything pinned" to match `model.ts`. The UI pins per item from `position`, so `layout` is informational only.
3. **`HandlerInfo.functions[]` fallback.** This is not needed: the API only sends the singular `function` (§11). It is harmless to keep.
4. **`FlowContainer.size`.** The API never sends it, because the UI computes box size from children. It stays optional.
5. **`innerDirection`.** `normalizeGraph` treats anything other than `"TB"` as `"LR"`. The API echoes the graph direction, `LR` by default, so the two agree.
6. **`FlowEdge.sources`** (code file:line) is not in the UI type. It could feed an edge tooltip.

No blocking mismatch: the API output passes `normalizeGraph` unchanged.

## Contract change (Nov 4 2026, option C): checked-in default layout

Render's disk is ephemeral, so runtime positions are lost on every redeploy. The registration now carries a default layout, and the API merges two layers.

- **Registration:** an optional `layoutDefaults: { "note"?: string, "positions": { graphId: {x,y} } }`. The frames match PUT/GET: containers absolute, handler nodes relative to their container, entities and `ext:*` absolute. customerproject.dev ships a seeded LR layout (§8) that covers all 18 ids: 15 nodes and 3 containers.
- **GET /flow:** `position` = `layoutDefaults` (base) ← runtime store (override), per key. There is one new optional field on nodes and containers: **`positionSource: "default" | "stored"`**, present whenever `position` is.
  - `layout` counts both layers: `stored` when every visible item has a position, `partial` when some do, `auto` when none.
  - With the seeded defaults, customerproject.dev now arrives as `stored` / `default` on a fresh API. It is no longer `auto`.
- **PUT /flow/positions:** unchanged. A partial merge into the runtime store, and unknown ids are ignored. `PUT {}` clears the runtime keys only, so the next GET shows the **defaults** again. "Reset layout" therefore means "back to the checked-in layout", not "auto". To get a pure auto layout, the UI has to ignore `positionSource: "default"` positions itself.
- **New:** `GET /api/projects/{id}/flow/positions/export` returns `{ projectId, positions, fromStored, fromDefaults }`. `positions` is the effective merged map in `layoutDefaults.positions` shape, with sorted keys and whole pixels.
  - `scripts/export-flow-layout.sh` (or `tools/Generate-Registration export-layout`) writes it into `architecture/registrations/<id>.json`. Commit the file to make a dragged layout survive redeploys.
- **Unknown ids in `layoutDefaults`** are ignored. GET /flow then adds a `LAYOUT_DEFAULTS_UNKNOWN_ID: …` warning.
- **UI impact:** none required. `normalizeGraph` / `layoutGraph` already pin anything with a `position`. Optional follow-ups for UI/UX:
  - add `positionSource` to the types;
  - relabel "Reset layout" as "Reset to default layout";
  - offer an "Export layout" hint.

## Contract change (Nov 4 2026): `containers[].appIds` (+ deprecated `appId`)
This is additive. It was requested by UI/UX to join Flow containers to the agent's `AppStatus` (ADDENDA-010, appKey `<regId>/<appId>`). It became `appIds` in ADDENDA-010 v1.2.2 §9 #26.

- **`appIds: string[]`**, always present on every container, may be empty. These are the apps the container covers.
  - Source: the registration container's optional `appIds` (schema: `containers[].appIds`, items `^app:`).
  - When `appIds` is absent from the registration, the API falls back to the container's `resourceRefs`.
  - The values are passed through as written; they are not checked against `apps[]`.
- **`appId: string | null`** is a **deprecated** alias, kept for one version. It is always present: `appIds[0]` when `appIds` has exactly one entry, else `null` (none, or a multi-app container). Readers should move to `appIds`.
- Today every container of customerproject.dev (3) and price.dev (4) has exactly one app, so `appIds` = `[appId]`.
- `containers[].app` (appKind/port/cloudRoleName/attached) is unchanged. It is still only set for a `resource` container with exactly one `resourceRefs` entry that names a registered app.
- **Agent join:** `appKey = projectId + "/" + appIds[i]` (e.g. `price.dev/app:functions`) matches `AppStatus.regId/appId` from the front door's `GET /agent/status`.
- **Generator:** `tools/Generate-Registration` keeps a hand-written `containers[].appIds` on regenerate. The generator owns only `id`/`label`/`kind`/`resourceRefs`/`scope`, and other keys are merged through, as with `layoutDefaults`. A test covers this.
