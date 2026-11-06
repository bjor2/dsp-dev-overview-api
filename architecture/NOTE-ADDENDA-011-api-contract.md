# ADDENDA-011 — architecture layer API contract (as implemented)
Status: implemented on `feat/arch-layers`, local only. Not pushed or deployed. Implements NOTE-ADDENDA-011-ui-developer "Wire proposal" against ADDENDA-011 §2–§7.

All routes are read-only, anonymous like the rest of `/api`, and live under the existing project namespace. Errors use the usual envelope `{"error":{"code","message","retryable"}}`. Messages never contain machine paths or stack traces.

## `GET /api/projects/{id}/architecture`
```json
{
  "projectId": "customerproject.dev",
  "schemaVersion": 1,
  "roots": [{
    "layerId": "layer:customerproject.ingress-app",
    "title": "Ingress — import and snapshot responsibilities",
    "flowRef": { "containerId": "container:customerproject.ingress-app" }
  }],
  "warnings": []
}
```
- `roots` lists root layers only (sorted by `layerId`). Descendants are reached through `children` on the layer route.
- `flowRef.handlerId` appears only for a handler-bound root.
- No catalog for the project (e.g. `price.dev` today): `200`, `roots: []`, `warnings: []`.
- Invalid catalog: `200`, `roots: []`, `warnings: ["ARCHITECTURE_INVALID: …"]`.
- Unknown project: `404 PROJECT_NOT_FOUND`.

## `GET /api/projects/{id}/architecture/layer?layerId=<url-encoded id>`
```jsonc
{
  "projectId": "customerproject.dev",
  "schemaVersion": 1,
  "layer": { /* the authored layer document, validated, minus "$schema" */ },
  "rootFlowRef": { "containerId": "container:customerproject.ingress-app" },
  "breadcrumbs": [{ "layerId": "layer:customerproject.ingress-app", "title": "Ingress — import and snapshot responsibilities" }],
  "children": [],
  "warnings": []
}
```
- `breadcrumbs`: forest root → current layer, inclusive, derived from `parentLayerId`. The UI prepends its own Flow crumb from `projectId` + `rootFlowRef`. Up/Back follows `breadcrumbs[-2]`, not browser history.
- `rootFlowRef`: inherited from the forest root for every depth. Descendant documents have no `flowRef` of their own.
- `children`: `{layerId, title, openedByNodeId}` for each direct child, sorted by `layerId`. `openedByNodeId` is the inside component in `layer.nodes` that carries the matching `opensLayerId`, so "click node → open child" and the child list always agree.
- No depth ceiling. Every level uses this route with an id from `children`. Never build a file name or path from an id.

| Case | Status | `error.code` |
|---|---|---|
| Unknown project | 404 | `PROJECT_NOT_FOUND` |
| `layerId` missing/blank | 400 | `LAYER_ID_REQUIRED` |
| Catalog invalid (whole catalog rejected) | 503 | `ARCHITECTURE_INVALID` (`retryable: false`) |
| No catalog, or id not in the validated catalog | 404 | `ARCHITECTURE_LAYER_NOT_FOUND` |

The id is looked up only in the in-memory validated catalog. Paths, basenames (`ingress-app.json`), traversal (`../…`), absolute paths and other projects' ids all return `404 ARCHITECTURE_LAYER_NOT_FOUND`, and the message does not echo the id back.

## `GET /api/projects/{id}/flow`: additive `opensLayerId`
- `containers[].opensLayerId` is set when a valid root's `flowRef` names that container with no `handlerId`. Today that is only `container:customerproject.ingress-app` → `layer:customerproject.ingress-app`.
- `nodes[].opensLayerId` (handler nodes only) is set when a valid root's `flowRef` names that handler. No such root exists yet.
- When there is no link, the property is **omitted**, not `null`. That covers an absent catalog, an invalid catalog, and containers/handlers without a root.
- Nothing else changes: `schemaVersion` stays 2, and `appIds`/`appId`, ports, positions/`positionSource`, edges, edge ids, `sb` counts, sources and layout are byte-identical with the field removed (covered by tests).
- Invalid catalog: Flow also gets one `ARCHITECTURE_INVALID: …` entry in `warnings`, so the UI can explain why "Explore architecture" is missing. The Flow itself renders normally.
- Links come from the architecture sidecar only. Registration JSON and `registration.schema.json` are untouched. Registration-side `opensLayerId` stays deferred (ADDENDA-011 §7).

UI rule: show "Explore architecture" (button + keyboard action) only when `opensLayerId` is present, then call the layer route with that id.

## Warning / diagnostic codes
- `ARCHITECTURE_INVALID: Architecture catalog rejected; drill-down disabled, Flow unaffected. <up to 3 V-rule issues> (+N more)` appears on the index `warnings`, the Flow `warnings`, and the layer route's 503 message. Issues name a rule (`V-001`…`V-008`), layer/node/edge ids and catalog basenames only. The full list goes to the server log (`ArchitectureService`).
- Treat the text after the code as a developer diagnostic. Do not parse it.

## Conceptual vs live (must hold in the UI)
- Architecture ids (`layer:`, `comp:`, `aedge:`) are never Flow ids. They do not appear in Flow `nodes`/`containers`/`edges`. `PUT /flow/positions` ignores them, and Flow reset/export never touches them.
- Never join activity, SB counts, DLQ state, glows, console lines or throughput onto `comp:`/`aedge:` ids. The layer payload has no `sb`, count, activity or position fields, and the server never adds any.
- `node.flowEntityId` (e.g. `q:customerproject-ingress`) is only a reference for an optional "View in Flow" navigation. Do not show live status inherited from it.
- Every edge is `kind: "conceptual"`, meaning a message/data/dependency relationship. It says nothing about order, timing, causality or delivery. Do not animate edges or number them as steps. Conceptual cycles are allowed and can appear.
- `confidence: "confirmed"` means the cited artifact supports the statement. It does **not** mean runtime behaviour was observed. Show layer `uncertainties` globally and node/edge `uncertainties` + `sources` on selection.
- Clickable sources: repo refs are `<repo>/<relative path>`, registration refs are `dev-overview/architecture/registrations/<id>.json`, and Confluence refs are `https://<site>.atlassian.net/wiki/…/pages/<id>…`. The server rejects other forms, but the UI must still escape labels and apply its own link allowlist. Never open `file://` or run anything.
- Layout is auto-layout only. If positions are added later, use a separate `apos:v1:{registrationId}:{layerId}:{nodeId}` store, never FlowPositionStore.

## Validation (server, atomic per catalog)
The loader reads `architecture/layers/{registration.project}/index.json` plus every layer it lists, then validates in this order:
1. **V-001 schema**: each document against `architecture-layer.schema.json` (Draft 2020-12, `date-time` format enforced), catalog vs layer branch by document type. Unknown properties, other `schemaVersion`s and non-`conceptual` edge kinds are rejected.
2. **V-003 safe resolution**: catalog paths must be sibling basenames `^[a-z0-9][a-z0-9.-]*\.json$`. Each file, `index.json` included, must resolve through symlinks to a regular file under the project directory, and the project directory must resolve under the layers root. Files are capped at 1 MiB. Nothing is fetched and nothing is read from request input.
3. **V-002 identity/scope**: catalog `project`/`registrationId` must match the registration. All ids must sit in the `layer:|comp:|aedge:{project}.` namespaces. Layer, node and edge ids are unique across the catalog, catalog entries are unique by id and path, and `entry.id` equals `document.id`. A descendant scope cannot widen its parent's scope, and a root scope cannot widen its Flow source's scope.
4. **V-004 parents**: every parent exists, is not the layer itself, and the parent graph is acyclic.
5. **V-005 drill ownership**: each `opensLayerId` exists, is a direct child of the containing layer, and sits on a `kind: component, boundary: inside` node. Each non-root has exactly one opener, and roots have none.
6. **V-006 Flow binding**: the root container exists, the optional handler exists and belongs to it, there is at most one root per Flow source, and `flowEntityId` exists in `registration.entities`.
7. **V-007 endpoints**: edge `from`/`to` are nodes of the same layer, with no self edges.
8. **V-008 evidence**: every layer/node/edge has sources, `inferred`/`uncertain` sources need a `note`, an `inferred` kind is never `confirmed`, and refs use the allowed forms above.

Any issue rejects the whole catalog (`ARCHITECTURE_INVALID`), so partial layers are never served. Other projects and the Flow keep working.

A catalog whose `registrationId` names another env of the same project counts as **absent** for this registration. There is no cross-env fallback.

## Lifetime and packaging
- Loaded once per registration instance (validated at startup and logged). Registration hot reload (`POST /registration/refresh`) re-validates against the new registration. Editing layer files requires an API restart. GET never writes, repairs or generates anything.
- Files ship in the existing artifact: the Dockerfile already has `COPY architecture/ /repo/architecture/`, which covers `architecture/layers/**` and `architecture/architecture-layer.schema.json` (with `Overview__Root=/repo`). On the box the API reads them from the dev-overview checkout.
- Options (relative to `Overview:Root` unless absolute): `Overview:ArchitectureLayersDir` (default `architecture/layers`) and `Overview:ArchitectureSchema` (default `architecture/architecture-layer.schema.json`). Defaults need no configuration.
- Before deployment, box and Render must serve the same catalog. If the front door falls back between upstreams, a catalog mismatch shows up as different `roots`/`opensLayerId` and should be avoided.
