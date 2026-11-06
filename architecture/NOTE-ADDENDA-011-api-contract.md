# ADDENDA-011 — architecture layer API contract (as implemented)
Status: ADDENDA-011.1 implemented on `feat/arch-handler-layers`, local only. Not pushed or deployed. Implements NOTE-ADDENDA-011-ui-developer "Wire proposal" and its 011.1 delta against ADDENDA-011 §2–§7 plus the ADDENDA-011.1 section, with the per-handler catalogs from `NOTE-ADDENDA-011-flow-item-layer-map.md`.

## ADDENDA-011.1 in one paragraph
Explore targets are **Flow handler items** (the nodes inside the blue containers). Every root layer binds exactly one handler through `flowRef: { containerId, handlerId }`; `handlerId` is required and must belong to `containerId` in the registration. Containers are never Explore targets: the Flow container DTO has no `opensLayerId`, and handler DTOs carry it only when a valid root names that handler. There is no legacy container-root mode in the API (the verifier's `--allow-legacy-container-roots` is not ported).

## Catalogs served (011.1)
### customerproject.dev: 5 handler roots, 1 nested child
| Root layerId | flowRef.containerId | flowRef.handlerId |
|---|---|---|
| `layer:customerproject.api` | `container:customerproject.api` | `customerproject.api.http` |
| `layer:customerproject.fn.commands` | `container:customerproject.func-app` | `customerproject.fn.commands` |
| `layer:customerproject.fn.domain` | `container:customerproject.func-app` | `customerproject.fn.domain` |
| `layer:customerproject.fn.ingress` | `container:customerproject.ingress-app` | `customerproject.fn.ingress` |
| `layer:customerproject.fn.snapshot` | `container:customerproject.ingress-app` | `customerproject.fn.snapshot` |

Nested: `layer:customerproject.func-app.domain-update` (id kept, now a child of `layer:customerproject.fn.domain`, opened by `comp:customerproject.func.snapshot-apply`). Breadcrumbs `fn.domain → func-app.domain-update`. All 5 registration handlers have a root.

### price.dev: 8 handler roots, 1 nested child
| Root layerId | flowRef.containerId | flowRef.handlerId |
|---|---|---|
| `layer:price.api` | `container:price.api` | `price.api.http` |
| `layer:price.api-batch` | `container:price.api-batch` | `price.api-batch.http` |
| `layer:price.fn.blob-optimera` | `container:price.func-app` | `price.fn.blob-optimera` |
| `layer:price.fn.commands` | `container:price.func-app` | `price.fn.commands` |
| `layer:price.fn.domain` | `container:price.func-app` | `price.fn.domain` |
| `layer:price.fn.domain-ingress` | `container:price.func-app` | `price.fn.domain-ingress` |
| `layer:price.fn.ingress` | `container:price.ingress-app` | `price.fn.ingress` |
| `layer:price.fn.snapshot` | `container:price.ingress-app` | `price.fn.snapshot` |

Nested: `layer:price.api.get-prices` (opened by `comp:price.api.price-reader`), breadcrumbs `price.api → price.api.get-prices`.

**Intentionally no Explore affordance** (no root, no `opensLayerId`) for 10 of the 18 price handlers, per the item map: `price.fn.subscriptions` and the five `price.fn.sub-*` handlers (deferred: one shared reference-sync node, per-handler split not evidenced), and the four timers `price.fn.timer-snapshot`, `price.fn.timer-check-campaigns`, `price.fn.timer-campaign-lowest`, `price.fn.timer-reload-m3cloud` (no-layer: the Flow node already says everything). No empty roots are invented for them.

### Kept and retired ids
- Kept (semantics unchanged, saved positions stay valid): `layer:customerproject.api`, `layer:customerproject.func-app.domain-update`, `layer:price.api`, `layer:price.api.get-prices`, `layer:price.api-batch`.
- Retired (re-cut into handler roots; never served, never remapped, never recycled): `layer:customerproject.ingress-app`, `layer:customerproject.func-app`, `layer:price.ingress-app`, `layer:price.func-app`. The layer route answers `404 ARCHITECTURE_LAYER_NOT_FOUND` for them.
- `_obsolete/` (or any `_`-prefixed directory) is never a project or layer source. Packaged trees omit it; if one is present next to a catalog, only the index-listed sibling basenames are read.

All routes are read-only, anonymous like the rest of `/api`, and live under the existing project namespace. Errors use the usual envelope `{"error":{"code","message","retryable"}}`. Messages never contain machine paths or stack traces.

## `GET /api/projects/{id}/architecture`
```json
{
  "projectId": "customerproject.dev",
  "schemaVersion": 1,
  "roots": [
    { "layerId": "layer:customerproject.api", "title": "Api — HTTP entry points and their responsibilities",
      "flowRef": { "containerId": "container:customerproject.api", "handlerId": "customerproject.api.http" } },
    { "layerId": "layer:customerproject.fn.commands", "title": "COMMANDS — project, address, order, notification and import work",
      "flowRef": { "containerId": "container:customerproject.func-app", "handlerId": "customerproject.fn.commands" } },
    { "layerId": "layer:customerproject.fn.domain", "title": "DOMAIN — applying ingress snapshots",
      "flowRef": { "containerId": "container:customerproject.func-app", "handlerId": "customerproject.fn.domain" } },
    { "layerId": "layer:customerproject.fn.ingress", "title": "INGRESS — importing M3 change events",
      "flowRef": { "containerId": "container:customerproject.ingress-app", "handlerId": "customerproject.fn.ingress" } },
    { "layerId": "layer:customerproject.fn.snapshot", "title": "SNAPSHOT — composing address and project-price snapshots",
      "flowRef": { "containerId": "container:customerproject.ingress-app", "handlerId": "customerproject.fn.snapshot" } }
  ],
  "warnings": [],
  "catalogHash": "sha256:<64 lowercase hex>"
}
```
- `catalogHash` (optional, DESIGN-flow-drilldown §8.5) identifies the exact catalog bytes the API validated. It is `sha256:` plus lowercase hex SHA-256 over `UTF8(basename) + "\0" + fileBytes` for `index.json`, then each catalog entry in catalog order. No machine paths go into the hash, so the same files give the same value on the box and on Render. It is present only for a valid catalog and omitted (not `null`) when the catalog is absent or invalid. It changes when any byte of a served file changes, including whitespace. The layer route does not carry it.
- `roots` lists root layers only (sorted ordinally by `layerId`, so `layer:price.api` comes before `layer:price.api-batch`). Descendants such as `func-app.domain-update` are never listed here. They are reached through `children` on the layer route.
- `flowRef` always has both `containerId` and `handlerId` (ADDENDA-011.1). A valid catalog cannot contain a container-only root.
- No catalog for the project (no `architecture/layers/{project}/` folder): `200`, `roots: []`, `warnings: []`. Both registered projects have a catalog now, so this only happens for a new project or a layers root without that folder.
- Invalid catalog: `200`, `roots: []`, `warnings: ["ARCHITECTURE_INVALID: …"]`.
- Unknown project: `404 PROJECT_NOT_FOUND`.

## `GET /api/projects/{id}/architecture/layer?layerId=<url-encoded id>`
Nested example, `layerId=layer:customerproject.func-app.domain-update`:
```jsonc
{
  "projectId": "customerproject.dev",
  "schemaVersion": 1,
  "layer": { /* the authored layer document, validated, minus "$schema"; no flowRef of its own */ },
  "rootFlowRef": { "containerId": "container:customerproject.func-app", "handlerId": "customerproject.fn.domain" },
  "breadcrumbs": [
    { "layerId": "layer:customerproject.fn.domain", "title": "DOMAIN — applying ingress snapshots" },
    { "layerId": "layer:customerproject.func-app.domain-update", "title": "DOMAIN — applying ingress snapshots" }
  ],
  "children": [],
  "warnings": []
}
```
Its parent (`layerId=layer:customerproject.fn.domain`) answers `"children": [{ "layerId": "layer:customerproject.func-app.domain-update", "title": "DOMAIN — applying ingress snapshots", "openedByNodeId": "comp:customerproject.func.snapshot-apply" }]`. Price is the same shape: `layer:price.api.get-prices` has breadcrumbs `price.api → price.api.get-prices` and `rootFlowRef` `{ container:price.api, price.api.http }`.
- `breadcrumbs`: forest root → current layer, inclusive, derived from `parentLayerId`. The UI prepends its own Flow crumb from `projectId` + `rootFlowRef`. Up/Back follows `breadcrumbs[-2]`, not browser history.
- `rootFlowRef`: inherited from the forest root for every depth, always `{containerId, handlerId}`. Descendant documents have no `flowRef` of their own. "View in Flow" / return focuses the originating **handler**.
- `children`: `{layerId, title, openedByNodeId}` for each direct child, sorted by `layerId`. `openedByNodeId` is the inside component in `layer.nodes` that carries the matching `opensLayerId`, so "click node → open child" and the child list always agree.
- No depth ceiling. Every level uses this route with an id from `children`. Never build a file name or path from an id.

| Case | Status | `error.code` |
|---|---|---|
| Unknown project | 404 | `PROJECT_NOT_FOUND` |
| `layerId` missing/blank | 400 | `LAYER_ID_REQUIRED` |
| Catalog invalid (whole catalog rejected) | 503 | `ARCHITECTURE_INVALID` (`retryable: false`) |
| No catalog, or id not in the validated catalog | 404 | `ARCHITECTURE_LAYER_NOT_FOUND` |

The id is looked up only in the in-memory validated catalog. Paths, basenames (`fn.ingress.json`), retired ids (`layer:customerproject.func-app`), traversal (`../…`), absolute paths and other projects' ids all return `404 ARCHITECTURE_LAYER_NOT_FOUND`, and the message does not echo the id back.

## `GET /api/projects/{id}/flow`: additive `opensLayerId` (handler nodes only)
- `nodes[].opensLayerId` (handler nodes only) is set when a valid root's `flowRef.handlerId` names that handler. With the 011.1 catalogs: the 5 customerproject handlers and 8 of the 18 price handlers (tables above), each → its root layer id. Nested layers are never Flow targets.
- `containers[]` **never** carry `opensLayerId`. The field is gone from the container DTO (ADDENDA-011.1; it was set for every container in v1.1).
- When there is no link, the property is **omitted**, not `null`. That covers an absent catalog, an invalid catalog, and handlers without a root (the price subscriptions, `sub-*` and timers).
- Nothing else changes: `schemaVersion` stays 2, and `appIds`/`appId`, ports, positions/`positionSource`, edges, edge ids, `sb` counts, sources and layout are byte-identical with the field removed (covered by tests).
- Invalid catalog: Flow also gets one `ARCHITECTURE_INVALID: …` entry in `warnings`, so the UI can explain why "Explore architecture" is missing. The Flow itself renders normally.
- Links come from the architecture sidecar only. Registration JSON and `registration.schema.json` are untouched; the registration schema rejects `opensLayerId` on containers and handlers. Registration-side `opensLayerId` stays deferred (ADDENDA-011 §7).

UI rule: show "Explore architecture" (click + keyboard action) on a **handler node** only when its `opensLayerId` is present, then call the layer route with that id. Remove the container-header Explore.

## Warning / diagnostic codes
- `ARCHITECTURE_INVALID: Architecture catalog rejected; drill-down disabled, Flow unaffected. <up to 3 V-rule issues> (+N more)` appears on the index `warnings`, the Flow `warnings`, and the layer route's 503 message. Issues name a rule (`V-001`…`V-008`, `V-011`), layer/node/edge ids and catalog basenames only. The full list goes to the server log (`ArchitectureService`).
- Treat the text after the code as a developer diagnostic. Do not parse it.

## Conceptual vs live (must hold in the UI)
- Architecture ids (`layer:`, `comp:`, `aedge:`) are never Flow ids. They do not appear in Flow `nodes`/`containers`/`edges`. `PUT /flow/positions` ignores them, and Flow reset/export never touches them.
- Never join activity, SB counts, DLQ state, glows, console lines or throughput onto `comp:`/`aedge:` ids. The layer payload has no `sb`, count, activity or position fields, and the server never adds any.
- `node.flowEntityId` (e.g. `q:customerproject-ingress`) is only a reference for an optional "View in Flow" navigation. Do not show live status inherited from it.
- Every edge is `kind: "conceptual"`, meaning a message/data/dependency relationship. It says nothing about order, timing, causality or delivery. Do not animate edges or number them as steps. Conceptual cycles are allowed and can appear, but never as a two-edge back-and-forth through one boundary node (see V-007).
- The same `flowEntityId` (e.g. `q:customerproject`) can appear on several nodes and layers. Each one is a reference to the same Flow entity, not a separate runtime entity.
- `confidence: "confirmed"` means the cited artifact supports the statement. It does **not** mean runtime behaviour was observed. Show layer `uncertainties` globally and node/edge `uncertainties` + `sources` on selection.
- Clickable sources: repo refs are `<repo>/<relative path>`, registration refs are `dev-overview/architecture/registrations/<id>.json`, and Confluence refs are `https://<site>.atlassian.net/wiki/…/pages/<id>…`. The server rejects other forms, but the UI must still escape labels and apply its own link allowlist. Never open `file://` or run anything.
- Layout: saved architecture positions use the UI-side namespace `dsp-arch-pos:v1:{project}:{env}:{layerId}` (ADDENDA-011.1; replaces the proposed `apos:` keys). The API stores no architecture positions and never uses FlowPositionStore for them. Ignore (do not delete or remap) keys for the retired ids above.

## Validation (server, atomic per catalog)
The loader reads `architecture/layers/{registration.project}/index.json` plus every layer it lists (nothing else: no directory listing, so `_obsolete/` and other `_`-prefixed directories are never read; project names must match `^[a-z0-9-]+$`), then validates in this order:
1. **V-001 schema**: each document against `architecture-layer.schema.json` (Draft 2020-12, `date-time` format enforced), catalog vs layer branch by document type. Unknown properties, other `schemaVersion`s and non-`conceptual` edge kinds are rejected.
2. **V-003 safe resolution**: catalog paths must be sibling basenames `^[a-z0-9][a-z0-9.-]*\.json$`. Each file, `index.json` included, must resolve through symlinks to a regular file under the project directory, and the project directory must resolve under the layers root. Files are capped at 1 MiB. Nothing is fetched and nothing is read from request input.
3. **V-002 identity/scope**: catalog `project`/`registrationId` must match the registration. All ids must sit in the `layer:|comp:|aedge:{project}.` namespaces. Layer, node and edge ids are unique across the catalog, catalog entries are unique by id and path, and `entry.id` equals `document.id`. A descendant scope cannot widen its parent's scope, and a root scope cannot widen its Flow source's scope.
4. **V-004 parents**: every parent exists, is not the layer itself, and the parent graph is acyclic.
5. **V-005 drill ownership**: each `opensLayerId` exists, is a direct child of the containing layer, and sits on a `kind: component, boundary: inside` node. Each non-root has exactly one opener, and roots have none.
6. **V-006 Flow binding**: the root container exists in the registration, and every `flowEntityId` exists in `registration.entities`.
   **V-011 handler roots (ADDENDA-011.1)**: every root has a non-blank `flowRef.handlerId` (a container-only root is rejected, and the schema rejects it as V-001 too); the handler exists in the registration (`V-011: root … handler … is not in the registration`) and belongs to `flowRef.containerId` (`… belongs to {actual}, not {claimed}`); at most one root per handler (`V-011: more than one root for handler … (ids)`). A root's scope cannot widen its handler's scope (V-002). No legacy/soft-accept mode. `opensLayerId` on a registration container or handler is rejected by the registration schema, and the Flow container DTO has no such field; an explicit handler `opensLayerId` disagreeing with the sidecar therefore has no input yet and is checked once registration-side links exist.
7. **V-007 endpoints**: edge `from`/`to` are nodes of the same layer, with no self edges. The Flow v2 invariant also applies: a `boundary: outside` node must not both receive from and send to the same component (`V-007: boundary node {id} both receives from and sends to the same component {peer}`). Express a self-requeue as a badge in the responsibility text. ADDENDA-011 §10 and `drafts/verify-addenda-011.py` call this rule "V-008 bidirectional". The server reports it under V-007 because V-008 here means evidence (§6). It is the same check.
8. **V-008 evidence**: every layer/node/edge has sources, `inferred`/`uncertain` sources need a `note`, an `inferred` kind is never `confirmed`, and refs use the allowed forms above.

Any issue rejects the whole catalog (`ARCHITECTURE_INVALID`), so partial layers are never served. Other projects and the Flow keep working.

A catalog whose `registrationId` names another env of the same project counts as **absent** for this registration. There is no cross-env fallback.

## Lifetime and packaging
- Loaded once per registration instance (validated at startup and logged). Registration hot reload (`POST /registration/refresh`) re-validates against the new registration. Editing layer files requires an API restart. GET never writes, repairs or generates anything.
- Files ship in the existing artifact: the Dockerfile already has `COPY architecture/ /repo/architecture/`, which covers `architecture/layers/**` and `architecture/architecture-layer.schema.json` (with `Overview__Root=/repo`). On the box the API reads them from the dev-overview checkout.
- Options (relative to `Overview:Root` unless absolute): `Overview:ArchitectureLayersDir` (default `architecture/layers`) and `Overview:ArchitectureSchema` (default `architecture/architecture-layer.schema.json`). Defaults need no configuration.
- Before deployment, box and Render must serve the same catalog. If the front door falls back between upstreams, a catalog mismatch shows up as different `roots`/`opensLayerId` and should be avoided.
