# ADDENDA-011 — proposed UI/Developer handoff
Status: **Accepted baseline (Team Lead 2026-11-06)**; route further implementation decisions through Team Lead. No runtime code or API deployed by this task.

**Team Lead locked 2026-11-06:** forest + this API are the implementation baseline; **auto-layout first**; separate layer-position persistence **deferred**. These are implementation decisions, not deployment authorization.

## Wire proposal
Retain existing authenticated project read routes/policy and front-door upstream behavior. Add read-only endpoints under the same project namespace; do not introduce authentication/API-lockdown changes.

`GET /api/projects/{id}/architecture` →
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
`GET /api/projects/{id}/architecture/layer?layerId=<URL-encoded stable id>` →
```jsonc
{
  "projectId": "customerproject.dev",
  "schemaVersion": 1,
  "layer": { /* the validated layer document, without filesystem paths */ },
  "rootFlowRef": { "containerId": "container:customerproject.ingress-app" },
  "breadcrumbs": [{ "layerId": "layer:customerproject.ingress-app", "title": "Ingress — import and snapshot responsibilities" }],
  "children": [],
  "warnings": []
}
```
`children` contains `{layerId,title,openedByNodeId}` derived from direct-child parents/openers, sorted by layerId. Breadcrumbs contain root through current inclusively. UI prepends a Flow crumb using projectId/rootFlowRef. Arbitrary useful depth uses the same endpoint; never a guessed file path. Lookup IDs in validated in-memory catalog only. Unknown project/layer: 404. Missing optional catalog: index 200 with empty roots; layer lookup 404. Invalid catalog: index 200 empty roots plus `ARCHITECTURE_INVALID` warning; layer lookup 503 `ARCHITECTURE_INVALID`. Do not expose machine paths/stack traces in errors.

`GET /api/projects/{id}/flow`: add optional `opensLayerId?: string` on container DTO and optionally handler-node DTO, resolved from validated root.flowRef. Example ingress container has `opensLayerId: "layer:customerproject.ingress-app"`. No change to graph schemaVersion, appIds/appId, ports, positions, edges, sources or activity. Omit property when no valid layer or when architecture invalid/absent; old clients ignore it. Registration fixtures remain untouched. See addendum §7 before adding explicit registration opensLayerId later.

## UI behavior
- Container header gets “Explore architecture” only with opensLayerId; keyboard action too. Do not repurpose handler selection or SB actions.
- Separate architecture view/canvas. Preserve Flow scope, pan/zoom, selection and layout on return. Root flowRef focuses originating container/handler when explicitly requested.
- Draw inside responsibilities with a boundary frame; outside queues/storage/dependencies remain visibly outside. `outside` does not mean another team's ownership. Labels/responsibilities and provenance panel are first-class.
- Every edge is visibly conceptual/static; no live animation, throughput, trace causality or inferred execution numbering. Keep Activity and console on Flow. A boundary flowEntityId can offer “View in Flow”, not live status inheritance.
- Click an inside component with opensLayerId for the next layer; breadcrumbs/Up follow canonical parents. No empty drill affordances, speculative nodes or arbitrary cross-links.
- Display layer caveats globally plus node/edge caveats on selection; confidence “confirmed” means source-supported, not runtime-observed. Source links must use safe repo/Confluence allowlists, escape labels, never run shell commands or read arbitrary file:// paths.
- Architecture first slice auto-layout only. If positions are persisted later, use separate apos namespace/store; never FlowPositionStore or existing /flow/positions reset/export.

## Implementation acceptance tests
1. Existing Flow normalization, v1 upgrade, appIds joins, partial/default/stored positions, self-requeue and activity unchanged with no catalog.
2. Load example: 1 root, 7 nodes, 9 conceptual edges; correct container link and root crumb.
3. Synthetic 3+ layer tree: canonical breadcrumbs and inherited rootFlowRef; no fixed level ceiling.
4. Reject duplicate IDs/catalog paths, missing parents, nav cycles, mismatched openers, orphan children, cross-project links and dangling local/Flow endpoints. Permit documented conceptual dependency cycles (not navigation cycles).
5. Reject traversal/absolute paths and symlink escapes; unknown ID cannot read source files. Invalid architecture disables only its links, not base Flow.
6. Non-confirmed evidence requires rationale; UI renders uncertainty and source version/hash. No source claim equated with live observation.
7. Flow activity/events do not join comp:/aedge: IDs; architecture reset/navigation cannot alter Flow positions.
8. Before deployment (not authorized here), ensure read-only architecture files are included in the existing API artifact and available on box and Render paths; do not silently mix different catalogs across upstream fallback.

Artifacts: ADDENDA-011-architecture-layers.md, architecture-layer.schema.json, layers/customerproject/{index,ingress-app}.json. Proposed design only, no endpoint implemented.

## v1.1 content for implementation (2026-11-06)
- Catalogs: `layers/customerproject/index.json` (4 layers) and `layers/price/index.json` (5 layers, registrationId `price.dev`).
- Real multi-depth examples for breadcrumbs: Flow → Functions → "Apply ingress snapshots" (`layer:customerproject.func-app.domain-update`); Flow → price Api → "Price reading" (`layer:price.api.get-prices`).
- Auto-layout only; no `apos:*` persistence yet. The same `flowEntityId` may appear on several nodes/layers (e.g. q:customerproject) — they are references, not separate runtime entities.
- New fixture rule V-008: reject a layer where a boundary node both receives from and sends to the same component.
