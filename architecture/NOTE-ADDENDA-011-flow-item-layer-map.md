# NOTE — ADDENDA-011.1 Flow item → root layer map (2026-11-06)
Status: **contract slice / planning**. Layer JSON NOT yet re-cut (follow-on content slice). Registrations byte-stable (cp `5ccc19c9…f39a`, price `ec39d090…8046`).
Statuses: `reuse-id` (same layer id, semantics unchanged, only flowRef gains handlerId) · `new` · `split-from:<old>` (new id built from part of an old container layer) · `deferred` (plausible root, evidence too thin for its own layer yet) · `no-layer` (Flow node already says everything; no root planned).
Position namespace: `dsp-arch-pos:v1:{project}:{env}:{layerId}`. "keep" = saved positions remain valid; "ignore" = UI must ignore saved keys for that layerId (do not delete them, do not remap onto new ids).

## customerproject (first — implementation can start on this)
| handlerId | containerId | proposed root layerId | status | migration note | evidence basis |
|---|---|---|---|---|---|
| customerproject.fn.ingress | container:customerproject.ingress-app | layer:customerproject.fn.ingress | **done** (split-from:layer:customerproject.ingress-app) | old id retired → **ignore** its positions; carry nodes ingress.input, ingress.import, ingress.storage, ingress.snapshot-requests (comp: ids kept) | existing layer + IngressQueueHandler.cs / ChangeEventHandlerBase (strong) |
| customerproject.fn.snapshot | container:customerproject.ingress-app | layer:customerproject.fn.snapshot | **done** (split-from:layer:customerproject.ingress-app) | new id; carry ingress.address-snapshots, ingress.price-snapshots, domain-output; storage/snapshot-requests need NEW instance ids here (comp ids unique per catalog; reuse flowEntityId) | existing layer + SnapshotQueueHandler.cs, CreateIngressSnapshot handlers (strong) |
| customerproject.fn.domain | container:customerproject.func-app | layer:customerproject.fn.domain | **done** (split-from:layer:customerproject.func-app) | carry func.domain-input, func.snapshot-apply (opener), storage instance; child **layer:customerproject.func-app.domain-update keeps id** (reparented, content unchanged → positions **keep**) | existing func-app + domain-update child (strong) |
| customerproject.fn.commands | container:customerproject.func-app | layer:customerproject.fn.commands | **done** (split-from:layer:customerproject.func-app) | carry command-input, project-lifecycle, address-upsert, order-association, notification, image-import, storage, topic, communication, erp, order-history, product-catalog, image-blob | existing func-app (strong; 5 inside responsibilities) |
| customerproject.api.http | container:customerproject.api | layer:customerproject.api | **done** (reuse-id) | add flowRef.handlerId; sole handler in container, semantics unchanged → positions **keep**. Explore moves from container header to handler node | existing api layer (strong) |

CP migrated 2026-11-06 (drafts/build-cp-per-item-roots.py); cross-root edges replaced by boundary nodes comp:customerproject.snapshot.input/.snapshot.storage/.domain.storage/.domain.commands-out. Caveat: func-app edges crossing domain↔commands (e.g. DOMAIN emitting commands) must become boundary nodes in each root; do not draw cross-root edges. Historical ADF↔current-code conflicts (22779013 vs code) carried unchanged.

## price (MIGRATED 2026-11-06T16:04+01:00 via drafts/build-price-per-item-roots.py; strict verifier PASS)
| handlerId | containerId | proposed root layerId | status | migration note | evidence basis |
|---|---|---|---|---|---|
| price.api.http | container:price.api | layer:price.api | reuse-id | add handlerId; child layer:price.api.get-prices keeps id; positions keep | existing (strong) |
| price.api-batch.http | container:price.api-batch | layer:price.api-batch | reuse-id | add handlerId; positions keep; remains deliberately thin | existing (thin, flagged) |
| price.fn.ingress | container:price.ingress-app | layer:price.fn.ingress | split-from:layer:price.ingress-app | old id ignore; carry ingress.input, import, storage, snapshot-queue | existing (strong) |
| price.fn.snapshot | container:price.ingress-app | layer:price.fn.snapshot | split-from:layer:price.ingress-app | carry ingress.snapshots, full-reload, domain-out, optimera-files; CONFIRMED: PriceSnapshotQueueHandler.cs dispatches 6 Reload* subjects (lines 74–119, sha256:73f40adb…) → full reload under SNAPSHOT; reload timers shown as boundary node only | existing (medium) |
| price.fn.timer-reload-m3cloud | container:price.ingress-app | — | no-layer | CONFIRMED: ReloadM3CloudDataTimerTriggers.cs only calls snapshotQueueClient.SendAsync (sha256:b51ebf13…); Flow node suffices | existing node (thin) |
| price.fn.commands | container:price.func-app | layer:price.fn.commands | split-from:layer:price.func-app | old id ignore; carry func.commands-input, func.commands, price-db, topic | existing (strong) |
| price.fn.domain | container:price.func-app | layer:price.fn.domain | split-from:layer:price.func-app | carry domain-input, snapshot-apply | existing (medium; DONE as thin real root — PriceDomainQueueHandler.cs 4 *FromSnapshot subjects, code-evidenced) |
| price.fn.domain-ingress | container:price.func-app | layer:price.fn.domain-ingress | split-from:layer:price.func-app | carry pico-input, pico-import; ADF↔PICO/ASB conflict caveat preserved unresolved | existing + Confluence 22773861 (medium) |
| price.fn.blob-optimera | container:price.func-app | layer:price.fn.blob-optimera | split-from:layer:price.func-app | carry optimera-files, file-import | existing (medium) |
| price.fn.subscriptions | container:price.func-app | — | deferred | reference-sync is one shared node across 5 subs; per-handler split not evidenced | thin |
| price.fn.sub-customerproject | container:price.func-app | — | deferred | as above | thin |
| price.fn.sub-customer | container:price.func-app | — | deferred | as above | thin |
| price.fn.sub-warehouse | container:price.func-app | — | deferred | as above | thin |
| price.fn.sub-currency | container:price.func-app | — | deferred | as above | thin |
| price.fn.sub-product | container:price.func-app | — | deferred | as above | thin |
| price.fn.timer-snapshot | container:price.func-app | — | no-layer | timer only enqueues SaveLowestPriceSnapshot (explained in fn.commands) | thin |
| price.fn.timer-check-campaigns | container:price.func-app | — | no-layer | enqueues CheckItemCampaigns | thin |
| price.fn.timer-campaign-lowest | container:price.func-app | — | no-layer | enqueues SaveCampaignLowestPrices | thin |

Price totals (done): 18 handlers → 8 roots + child layer:price.api.get-prices; obsolete ingress-app/func-app moved to layers/price/_obsolete/ (ignored ids). Was: 8 planned roots (2 reuse-id, 6 split-from), 6 deferred, 4 no-layer.

## Migration / ignore list (`dsp-arch-pos:v1:{project}:{env}:{layerId}`)
- **ignore** (retired, re-cut; never remap): `layer:customerproject.ingress-app`, `layer:customerproject.func-app`, `layer:price.ingress-app`, `layer:price.func-app`.
- **keep**: `layer:customerproject.api`, `layer:customerproject.func-app.domain-update`, `layer:price.api`, `layer:price.api.get-prices`, `layer:price.api-batch`.
- Retired ids are not recycled. Unrelated saved keys are untouched. New split layers start auto-layout (no carryover even where comp: ids are reused).
