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
