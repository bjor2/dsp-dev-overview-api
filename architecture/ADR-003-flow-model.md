# ADR-003 — Flow model (customerproject/dev)
Status: Accepted. Contract: `FlowGraph` in the UI handoff.

## Nodes
| id | kind | label |
|---|---|---|
| app:api | app (web) | Api :5011 |
| app:functions | app (func) | Functions :7239 |
| app:ingress | app (func-ingress) | Ingress :7240 |
| q:customerproject | queue | customerproject |
| q:customerproject-ingress | queue | customerproject-ingress |
| q:customerproject-snapshot | queue | customerproject-snapshot |
| q:customerproject-domain | queue | customerproject-domain |
| q:communication | queue (shared, external owner) | communication |
| q:customerproject-topic-inspect | queue (local inspect sink) | customerproject-topic-inspect |
| t:customerproject-topic | topic | customerproject-topic |
| s:customerproject-topic/local-inspect | subscription | local-inspect |
| ext:seed-messages | external source | seed-messages kit |

DLQs are **not** separate nodes. Each queue/subscription node carries `dlqDepth`, and "DLQ-hot" is a node style.

## Edges (`kind`)
- `trigger`: q:customerproject → app:functions (CustomerProjectQueueHandler); q:customerproject-domain → app:functions (CustomerProjectDomainQueueHandler); q:customerproject-ingress → app:ingress (IngressQueueHandler); q:customerproject-snapshot → app:ingress (SnapshotQueueHandler)
- `publish` (inferred from settings): app:api → q:customerproject (commands); app:api → t:customerproject-topic; app:ingress → q:customerproject-snapshot; app:ingress → q:customerproject-domain; app:functions → t:customerproject-topic; app:functions → q:communication
- `subscription`: t:customerproject-topic → s:local-inspect
- `forward`: s:local-inspect → q:customerproject-topic-inspect
- `inject`: ext:seed-messages → q:customerproject-ingress (6 events); ext:seed-messages → q:customerproject (2 commands)

## Deltas vs IA / cloud (explicit)
1. IA Appendix B (checked 2026-11-02) has edges, but differs from the registration:
   - IA `QS --> Functions` ✗ → `customerproject-snapshot` triggers **Ingress** (`SnapshotQueueHandler`).
   - IA `Ingress --> QI` ✗ (Ingress does not publish to its own ingress queue; inbound is seed-kit/M3 subs) → keep only `QI --> Ingress`.
   - Missing in IA: `Ingress --> QS`, `Ingress --> QD`, `Api --> T`, `seed-messages --> QI / QC` inject edges.
   - IA `Functions -.-> QComm` matches (publish, inferred).
   UI/UX should regenerate Appendix B from `FlowGraph`, not hand-maintain it.
2. In **cloud dev**, the topic has subscriptions `customerproject-to-customerproject-subscription` (forward → queue customerproject, label `CustomerProjectOrderReferenceChanged`) and `customerproject→eventpoint` (forward → `eventpointsink-ingress-2`). Inbound subs from price/erpeventpublisher/orderhistory/order also feed `customerproject`, and the m3cloud ingress subs feed `customerproject-ingress`. **Locally only `local-inspect` exists.** v1 Flow shows local by default. A toggle "show cloud-only edges" renders them dashed with `scope:"cloud"`.
3. `local-inspect` and `customerproject-topic-inspect` are **local-only** (not in TF).
4. Publish edges are inferred from settings. They're flagged `confidence:"inferred"` until verified in AI dependency telemetry.

## Layout
Auto-layout v1 (ELK/dagre, LR default, TB toggle). Optional user-dragged positions are persisted in `dev-overview/data/flow-positions/customerproject.dev.json`, never in service repos. Stored positions win per node, and new nodes are auto-placed.

## dlqHot
`dlqHot` is **only** `deadLetterCount > 0` (messages already in DLQ). Near-MaxDeliveryCount is out of scope for this flag — see ADDENDA-003. Nodes may still expose `maxDeliveryCount` (10 local / 3 cloud) for display.
