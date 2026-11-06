# ADDENDA-011 — C4-style architecture layers (v1.1)
Status: **Accepted baseline** (Team Lead 2026-11-06) · Amends ADR-002 / ADR-003 additively; ADDENDA-008 remains authoritative for L0 Flow.

**Locked for implementation (not deploy auth):** forest navigation + proposed API in NOTE-ADDENDA-011-ui-developer; **auto-layout first**; separate layer-position persistence **deferred**. Developer/UI may implement against this contract.

## 1. Decision and scope
Keep Flow exactly as it is: handlers inside flat resource/logical containers, standalone SB entities, self-requeue badges and current activity/layout. Clicking a blue container with an architecture entry opens a separate explanatory graph. Architecture describes responsibilities and data/message relationships, **not code execution flow**. It is C4-inspired rather than a promise of formal C4 levels. L1 is a container explanation; L2+ can explain a useful component, without a fixed depth ceiling.

**v1.1 content (2026-11-06):** customerproject now has 4 layers (ingress-app, func-app, **func-app.domain-update** nested child, api); price has 5 (func-app, ingress-app, api, **api.get-prices** nested child, api-batch thin). See §10.

First slice: `layer:customerproject.ingress-app`, linked to `container:customerproject.ingress-app`, seven nodes and nine conceptual edges. This is the clearest evidence: two queue handlers, change import, two snapshot families and SQL ingress storage. No price service layers or speculative child layers in this slice.

## 2. Files and loading
- `architecture-layer.schema.json`: Draft 2020-12 schema with disjoint `documentType` branches for layer and catalog, independently versioned `schemaVersion: 1` (NOT registration v3).
- `layers/<project>/index.json`: catalog selects a registration via `registrationId` (e.g. `customerproject.dev`) and maps stable layer IDs to sibling JSON basenames. It lists **all** documents, not only roots. Never enumerate arbitrary paths supplied by a client.
- `layers/<project>/<slug>.json`: one layer document; no service-repo writes or runtime code references/imports. All architecture documents live in dev-overview.
- `project` matches registration.project; catalog.registrationId matches registration.id. A project/env-specific catalog can later use another explicit allowlisted directory; no implicit cross-env fallback. First slice is dev only.
- Loader creates an immutable, fully validated catalog snapshot, derives root entry links from `flowRef`, children from `parentLayerId`, and canonical breadcrumbs. No authored duplicate `childLayerIds` or breadcrumb arrays.
- Proposed optional subsystem: malformed/missing architecture must not prevent Flow, activity or SB operations loading. Reject the architecture catalog atomically (do not serve dangling pieces), omit its drill links and emit `ARCHITECTURE_INVALID` diagnostics. Keep the existing Flow available. No file writes, generation, or repair on GET.

## 3. IDs and navigation ownership
- Layer: `layer:<project>.<semantic-path>`; component/boundary instance: `comp:<project>.<semantic-path>`; conceptual edge: `aedge:<project>.<semantic-path>`.
- IDs are lowercase semantic names independent of labels, ports, source lines, directory/class renames and array order. IDs are immutable while the responsibility is the same. Rename labels freely; an intentional identity replacement invalidates its own saved positions, never rewrites Flow IDs. Do not recycle deleted IDs.
- Unique layer IDs, component instance IDs and architecture edge IDs per project catalog. The same external dependency may appear in multiple layers using distinct instance IDs, optionally sharing `flowEntityId` as a reference; that is not a second runtime entity.
- Root: `parentLayerId: null` and required `flowRef: { containerId, handlerId? }`. A container root omits handlerId. A handler-specific root may supply it, but the handler must belong to that container. Exactly one root per Flow source (container or handler).
- Descendant: one non-null `parentLayerId`, no `flowRef`. A component in that parent owns the only `opensLayerId` link to it. Each non-root must be opened exactly once by its direct parent. This is a forest, not an arbitrary navigation graph.
- `opensLayerId` is drill-down only: no self, ancestor, sibling, cross-project or cross-root jumps. External/boundary nodes do not open layers in v1. Do not create an empty child just to show an affordance.
- Breadcrumbs: Flow → root → ... → current, derived from parents; source Flow anchor is inherited from the root. Back/Up follows canonical parent, not browser visitation history. Root depth is 1, computed rather than authored.
- Root links are sidecar-derived in this slice; **customerproject.dev.json stays byte-for-byte unchanged**.

## 4. Layer fields
See the schema and worked JSON for exact types. Required: identity/version, project/title/summary, parentLayerId, scope, reviewedAt (offset timestamp), sources, uncertainties, nodes and edges.

Nodes have `id`, `label`, `kind: component|store|contract|external`, a concise `responsibility`, `boundary: inside|outside`, sources and uncertainties. Optional `owner` is explanatory only; do not infer authority from it. Outside means outside the compute/responsibility boundary shown, **not necessarily owned by another team**. A queue or SQL store can belong to customerproject but remain outside Ingress compute. `external` requires outside. `flowEntityId` is an optional reference to an existing Flow entity, never an inline definition or permission to perform SB operations. Unknown physical hosting must remain unknown. M3, communication service and other systems must remain boundary abstractions unless researched in their own authorized scope.

Architecture edges have `kind: conceptual` only, directed local endpoints, `relationship: message|data|dependency`, a label, explanation, sources and uncertainties. Direction means message/data supplied or dependency, as described, **not sequencing, timing, causality, transactions or guaranteed delivery**. Do not add call stacks, conditional branches or execution steps. Explanatory data/dependency cycles can be real and are permitted; the **navigation** graph must be acyclic. Existing Flow I-1 is unchanged and is not applied to a separate conceptual component graph.

## 5. Evidence, provenance and uncertainty
Every layer/node/edge has non-empty `sources[]`: `{kind: repo|confluence|registration|inferred, ref, note?, confidence: confirmed|inferred|uncertain, revision?}`. Each also has `uncertainties: string[]` (empty only when no caveat is known). Caveats on a layer apply visibly to the entire layer; narrower caveats belong on the particular node/edge too.
- Repo refs use `<repo-name>/<repo-relative-path>`; no machine paths required. Notes identify reviewed symbols/line ranges. `revision: sha256:<file hash>` records actual reviewed file content independently of git metadata. Optional future `commit:<sha>` must be read, not guessed.
- Registration refs use `dev-overview/architecture/registrations/<id>.json`; hashes identify the actual source snapshot.
- Confluence refs are canonical https page URLs with numeric page ID; revision records the returned version. Record loss of images/macros and freshness limits. Search hits alone are leads, not implementation proof.
- Inference must cite a real supporting ref, have confidence inferred/uncertain and a note explaining the gap. All inferred/uncertain sources require a note. `confirmed` means the cited artifact supports that particular statement; **never** that runtime behavior/deployment was observed.
- Conflicting code/design: state both, prefer local code for the current implementation explanation, and mark unresolved intent/freshness explicitly. Do not silently merge historic ADF diagrams into the Ingress process.
- No secrets, connection values, payload samples with customer data, credentials, tokens or runtime logs in architecture files. Only configuration key names are permitted.

### Sources actually opened for the worked layer
Repo: README and servicecatalog Application Overview; `Ingress.Functions/Functions/{IngressQueueHandler,SnapshotQueueHandler}.cs`; `Ingress.Application/Import/{ChangeEventHandlerBase,CustomerAddressChangedHandler}.cs`; representative `CreateIngressSnapshot/{CreateCustomerAddressSnapshotOnCustomerAddressChangedHandler,CreateProjectWithPriceSnapshotOnHeaderFileChangedHandler}.cs`; `Ingress.Storage/IngressSnapshotStorage.cs` and storage DI; snapshot/domain queue clients. Full paths and file hashes are in the JSON.

Confluence via Atlassian: Customer Project **22779513 v4**, workshop/domain intent (2023), and CustomerProject Analysis **22779013 v6**, historical ADF/M3 analysis (2024). Both full markdown bodies opened. 22779513 loses image/macro content. The ADF SQL is upstream provenance, not evidence that ADF runs inside Ingress. Historical HasProjectPrice logic and current snapshot SQL are not asserted equivalent. No TPL component is invented. Newer architecture page **22774292 v13**, CustomerProject: Convert to PICo / Table Event streams (2024), was found by CQL and opened fully: it supports replacing Optimera ADF CSV import with event streams for both snapshot families, but is design intent rather than proof of deployment. Macro conversion was lossy.

CQL searches completed: `text ~ "CustomerProject" AND type = page ORDER BY lastmodified DESC` (23 results) and `title ~ "Customer Project" ORDER BY lastmodified DESC` (4 results). Newer matches include operational/identity material, not enough by themselves to replace direct code evidence. Order-association and documentation pages are leads for later layers, not cited as opened implementation evidence.

## 6. Validation (cross-document checks are NOT supplied by JSON Schema)
- **V-001 Shape/version:** validate every catalog/layer with the shared schema; unsupported versions rejected for architecture only. Unknown properties rejected. Array uniqueness by ID requires semantic checks.
- **V-002 Identity/scope:** catalog project, registration project/id and all ID project namespaces agree. Layer and node/edge IDs unique per catalog. Catalog entries unique by id and path; entry.id equals loaded document.id. Root/layer scope must be compatible with the root Flow source; descendants cannot widen parent scope (`both` includes local/cloud). No cross-project resolution.
- **V-003 Safe resolution:** catalog path is a sibling basename, not absolute, URL or path traversal. Resolved canonical path must remain under the project architecture directory, including symlink resolution. Load only catalog entries; no HTTP fetching or arbitrary source-path reads from API requests.
- **V-004 Parents/cycles:** every parent exists, is not self and forms an acyclic forest; walking every parent terminates at a root. Compute breadcrumbs/depth from this graph.
- **V-005 Drill ownership:** every opensLayerId exists, targets a direct child of the containing layer, and is on an inside component. Every non-root has exactly one opener in its parent; roots cannot be opened by architecture nodes. No dangling, duplicate-parent or unreachable layers.
- **V-006 Flow binding:** every root container exists. Optional root handler exists and belongs to it. Root Flow source is unique. Optional flowEntityId exists in registration.entities. Future explicit Flow opensLayerId fields must resolve inside the same catalog and agree exactly with its root flowRef, not compete with it. No fake entity creation.
- **V-007 Conceptual endpoints:** each edge endpoint exists in the same layer (not a layer/container/handler or a node in another layer). No self edges in v1; explain self responsibility in text. Only conceptual kind accepted. Never accept live-event fields as architecture evidence.
- **V-008 Evidence:** each ref resolves to a reviewed repo/registration file or an opened Confluence page ID; revision agrees with reviewed snapshot when supplied. Ref allowlists and escaping apply to any clickable UI links. Inferred/uncertain statements have rationale and visible caveats. Historical/code conflicts are explicit.
- **V-009 Compatibility:** no Flow IDs/kinds/edges/layout/activity change. Existing registration schema and fixture still validate. Architecture positions are separate from Flow positions. Existing registration byte hashes remain unchanged.
- **V-010 Read-only safety:** no service repo modifications, fetch, pull, deploy, publishing or global skill updates; no secrets copied. If .git is absent, state that git status cannot prove cleanliness and compare before/after file-tree fingerprints instead.

## 7. Backward compatibility and optional registration fragment
**No change to registration.schema.json or either registration in this slice.** L0 remains schemaVersion 2; ADR-003 behavior as refined by ADDENDA-008 and NOTE-ADDENDA-008-api-contract stays unchanged. New architecture nodes are never injected into FlowGraph.nodes, AllNodeIds or Activity joins.

When implementation is approved, the following is the **same optional property** to merge into BOTH `containers.items.properties` and `handlers.items.properties` (these currently reject additional properties):
```json
"opensLayerId": { "type": "string", "pattern": "^layer:[a-z0-9-]+\\.[a-z0-9.-]+$" }
```
Do not require it, bump registration version, reformat fixture files or let generation discard it. Until that later patch exists, do not put this property into strict registration JSON. The Flow API may compute the optional field from validated root.flowRef sidecars now. Explicit and sidecar sources must agree or fail architecture validation. Missing architecture simply means no drill affordance.

Keep existing `pos:{project}:{env}:{graphId}` Flow keys and default/stored merging unchanged. Proposed architecture UI keys: `apos:v1:{registrationId}:{layerId}:{nodeId}` (coordinates absolute in that layer). Never send them through `/flow/positions` or reset them with Flow reset. First implementation may auto-layout architecture only; no new persistence/API write is required. Breadcrumb navigation should preserve previous Flow pan/zoom/selection/layout and scope. Activity remains attached to existing Flow entities/handlers; an optional explicit “View in Flow” link may navigate to them without propagating counts/glows onto conceptual edges.

## 8. Verification checklist / first-slice results
- [x] Schema validates worked layer and catalog; existing customerproject and price registrations validate unchanged.
- [x] No dangling parents, open links, flow bindings or local edge endpoints; no navigation cycles (single valid root). A synthetic three-layer tree passes; 18 negative fixtures reject invalid shapes/navigation/refs. Runtime loader integration remains for Developer.
- [x] Every example citation is a real file or page actually opened; hashes identify repo evidence. Uncertainty is explicit for historical ADF versus current code and missing commit/deployment provenance.
- [x] No required registration fields changed; customerproject hash `5ccc19c9751934139e195b0c50121ce1b95af6fea5a3d934fc5de6308571f39a`, price hash `ec39d09048cdbbc9236c6ffeaa7ec4e3f1fd6536bf9ca9bac8785c3d2daa8046` preserved.
- [x] All writes limited to overview architecture files; no service repo writes, pull/fetch, deploy or publish. Final customerproject git status is clean, HEAD `b0864c051065f88e978b6d110483096c915d7edf`; price has no .git metadata. Before/after service file fingerprints are unchanged (recorded in verification note).
- [ ] Developer integration tests: optional subsystem failure isolation, API DTO/normalizer compatibility, nested breadcrumbs, scoped roots, layout separation, no architecture live-event joins. This task designs artifacts only, not runtime implementation.

## 9. Open questions / next slices through Team Lead
1. ~~Forest + API~~ **Locked 2026-11-06 (Team Lead):** forest/direct-child rule and NOTE-ADDENDA-011-ui-developer API are the implementation baseline (not deploy authorization).
2. ~~Layout~~ **Locked 2026-11-06 (Team Lead):** auto-layout first; separate layer-position persistence deferred.
3. ~~Next content~~ **Delivered v1.1 (see §10):** flesh customerproject Functions/API with at least one genuine nested child, then price. No speculative empty children; cite discrepancies, do not invent reconciliation.
4. Business owner reconciliation of historical ADF HasProjectPrice versus current snapshot query remains open; it does not block explaining the present code with caveats.
5. Price layers follow only after customerproject; keep source repo read-only and do not create cloud resources.

Design synthesis: Claude Code `--model fable` completed a read-only design pass for the first slice. Considered its richer shared-element catalog/rollup approach, but chose a thin catalog + isolated layers to keep the first slice bounded. Rejected joining live counts, ghost references and cross-project unresolved links because this brief requires static conceptual explanations and no dangling navigation. Accepted stable edge IDs, separate layout and an architecture-specific catalog type name. Execution details are recorded in NOTE-ADDENDA-011-verification.md.

## 10. v1.1 layers (customerproject Functions/API, then price)
| Layer | Parent | Flow anchor | Nodes/edges | Notes |
|---|---|---|---|---|
| `layer:customerproject.ingress-app` | — | container:customerproject.ingress-app | 7/9 | unchanged worked example |
| `layer:customerproject.func-app` | — | container:customerproject.func-app | 15/22 | DOMAIN + COMMANDS; self-requeues are badges, not back-arrows |
| `layer:customerproject.func-app.domain-update` | func-app (opened by `comp:customerproject.func.snapshot-apply`) | inherited | 7/7 | **nested child**: address vs project-price apply, bookkeeping, new-delivery vs modified decision, feature flag |
| `layer:customerproject.api` | — | container:customerproject.api | 13/20 | 4 controllers, project/association/image services, commands + topic out |
| `layer:price.func-app` | — | container:price.func-app | 13/12 | commands, timers, snapshot apply, PICO direct, subscriptions, Optimera CSV import |
| `layer:price.ingress-app` | — | container:price.ingress-app | 9/9 | import, snapshots, full reload → pricedumpfromoptimera |
| `layer:price.api` | — | container:price.api | 9/9 | controllers V1–V3, admin, price reading |
| `layer:price.api.get-prices` | price.api (opened by `comp:price.api.price-reader`) | inherited | 9/11 | **nested child**: tenant strategy selection, DSP calculation vs M3, fallback |
| `layer:price.api-batch` | — | container:price.api-batch | 5/4 | deliberately thin, high uncertainty |

Rules applied: Flow v2 invariant enforced in fixtures as **V-008** (no boundary node both receives from and sends to the same component); handler requeues appear in responsibilities as badges. Current code is authoritative; Confluence 22779513/22779013/22774292 differences are cited as uncertainties without reconciliation. Code-evidenced links missing from Flow registration (price ingress full reload → `b:pricedumpfromoptimera`) are recorded as uncertainties; registrations were **not** modified. Layers are generated reproducibly by `drafts/build-layers-addenda-011.py` (hashes computed from file bytes).

## Changelog
- v1.1 (2026-11-06): Team Lead locks recorded (forest + API baseline, auto-layout first, positions deferred). Added customerproject func-app, func-app.domain-update (nested), api; price catalog with func-app, ingress-app, api, api.get-prices (nested), api-batch. Verifier extended to multi-project catalogs, real depth ≥ 2, V-008 and a 19th negative fixture.
- v1 (2026-11-06): contract + ingress-app worked example.
